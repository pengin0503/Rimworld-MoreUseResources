using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MoreUseResources.Benchmarks.Running;
using MoreUseResources.Core;

namespace MoreUseResources.Benchmarks.Scenarios;

/// <summary>
/// File-system faults while the audit and the prefetch (with native copies) run on the same
/// files: files deleted, truncated, modified in place, renamed, made unreadable or replaced by a
/// directory, some before and some during the pass. The pass must finish, count the failures as
/// errors, let no exception escape to the worker pool, and the second pass (rescan / refresh)
/// must notice modified content. Works on copies in the trial's scratch directory only.
/// </summary>
public sealed class IoFaultScenario : Scenario
{
    public static readonly string[] Faults = { "delete", "truncate", "modify", "rename", "chmod", "replace-dir", "mixed" };
    private const long CopyBytes = 64L << 20;
    private const int CopyFiles = 600;

    public override string Id => "io-faults";
    public override string Title => "I/O 障害（削除・切り詰め・書き換え・改名・権限・ディレクトリ置換）";
    public override string ProductionCode => "AssetAudit.ProcessFile, FileCache.ReadOne/ReadInto (hold) and refresh hash check, WorkerPool";
    public override string NotReproduced =>
        "ディスク自体の読み取りエラー（EIO、要 root と device-mapper）、Windows の共有違反・ウイルス対策ソフトによるロック、Steam による更新中の置換";

    public override IReadOnlyList<Condition> Conditions(RunEnvironment env)
    {
        if (env.Dataset.Files.Count == 0) throw new InvalidOperationException("no input files");
        int w = Common.FittingWorkers(env.Options).Max();
        return Faults.Select(f => Condition.Of(Id, ("fault", f), ("workers", w))).ToList();
    }

    public override Trial CreateTrial(Condition condition, RunEnvironment env) => new FaultTrial(condition, env);

    private sealed class FaultTrial : Trial
    {
        private const long RescanMs = 10 * 60_000L;
        private readonly Condition _c;
        private readonly RunEnvironment _env;
        private readonly ManualClock _clock = new();
        private List<string> _files = new();
        private WorkerPool? _pool;
        private NativeBlockStore? _store;
        private AssetAudit? _audit;
        private FileCache? _cache;

        public FaultTrial(Condition c, RunEnvironment env)
        {
            _c = c;
            _env = env;
        }

        public override void Prepare(TrialContext ctx)
        {
            if (_c.Get("fault") == "chmod" && Environment.UserName == "root")
                ctx.Invalidate("running as root: file permissions do not stop reads");
            string root = Path.Combine(ctx.ScratchDirectory, "files");
            IEnumerable<string> pick = _env.Dataset.Files.Where((_, i) => i % Math.Max(1, _env.Dataset.Files.Count / CopyFiles) == 0).Take(CopyFiles);
            _files = Common.CopyFiles(pick, _env.Dataset.Relative, root, CopyBytes, ctx.Token);
            if (_files.Count < 10) throw new InvalidOperationException("too few files to inject faults");
            _pool = Common.Pool(_c.Workers);
            _store = new NativeBlockStore();
            _audit = new AssetAudit(_pool, clockMs: () => _clock.Now);
            _cache = new FileCache(_store, _pool, clockMs: () => _clock.Now);
            _audit.SetSources(_files);
            _cache.SetSources(_files);
        }

        /// <summary>
        /// Every 10th file gets a fault. Even entries are applied before the pass, so the first pass meets
        /// them. Odd entries are applied while the pass runs, once the audit has gone past the file, so
        /// the first pass indexed the original and only the second pass can notice the fault.
        /// </summary>
        private List<(string Path, string Fault, int Index, bool Before)> Plan()
        {
            string fault = _c.Get("fault")!;
            var plan = new List<(string, string, int, bool)>();
            int k = 0;
            for (int i = 3; i < _files.Count; i += 10, k++)
                plan.Add((_files[i], fault == "mixed" ? Faults[k % (Faults.Length - 1)] : fault, i, k % 2 == 0));
            return plan;
        }

        private static bool Apply(string path, string fault)
        {
            try
            {
                switch (fault)
                {
                    case "delete":
                        File.Delete(path);
                        return true;
                    case "truncate":
                        using (var s = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                            s.SetLength(s.Length / 2);
                        return true;
                    case "modify":
                    {
                        long length = new FileInfo(path).Length;
                        using (var s = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                        {
                            byte[] junk = new byte[Math.Min(4096, Math.Max(1, length))];
                            new Random(path.Length).NextBytes(junk);
                            s.Write(junk, 0, junk.Length);
                        }
                        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));
                        return true;
                    }
                    case "rename":
                        File.Move(path, path + ".moved");
                        return true;
                    case "chmod":
                        if (OperatingSystem.IsWindows()) return false;
                        File.SetUnixFileMode(path, UnixFileMode.None);
                        return true;
                    case "replace-dir":
                        File.Delete(path);
                        Directory.CreateDirectory(path);
                        return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return false;
        }

        public override void Execute(TrialContext ctx)
        {
            WorkerPool pool = _pool!;
            AssetAudit audit = _audit!;
            FileCache cache = _cache!;
            long budget = Math.Min(_env.Options.RamBudgetMiB, 64) * 1048576L;
            var plan = Plan();
            var before = plan.Where(p => p.Before).ToList();
            var during = plan.Where(p => !p.Before).ToList();
            int applied = 0, appliedBefore = 0, appliedAfterIndexed = 0, appliedAfterPass = 0, expectChanged = 0;
            // Content changes the second pass must report: files the first pass indexed as originals.
            void After((string Path, string Fault, int Index, bool Before) f, ref int counter)
            {
                if (!Apply(f.Path, f.Fault)) return;
                applied++;
                counter++;
                if (f.Fault is "modify" or "truncate") expectChanged++;
            }
            foreach (var p in before) if (Apply(p.Path, p.Fault)) { applied++; appliedBefore++; }
            // Jobs finish out of order by at most the in-flight limit.
            int slack = Math.Clamp(_c.Workers * 2, 1, 32) + 1;
            int next = 0;
            void PumpBoth(long refreshMs)
            {
                audit.Pump(true, _c.Workers, RescanMs);
                cache.Pump(active: true, hold: true, limitBytes: budget, allowCopies: true, refreshIntervalMs: refreshMs);
            }
            long Progress() => audit.Snapshot.Completed + cache.FilesRead + cache.Errors;
            bool Done() => audit.Snapshot.Complete && cache.PassComplete;
            long last = -1;
            var still = System.Diagnostics.Stopwatch.StartNew();
            bool stalled = false;
            ctx.WaitUntil(() =>
            {
                PumpBoth(0);
                AssetAuditSnapshot snap = audit.Snapshot;
                // Faults on files the audit has already gone past, while the pass is still running.
                while (next < during.Count && !snap.Complete && snap.Completed > during[next].Index + slack)
                {
                    After(during[next], ref appliedAfterIndexed);
                    next++;
                }
                if (Done()) return true;
                long p = Progress();
                bool idle = pool.RunningJobs == 0 && pool.PendingJobs == 0;
                if (p != last || !idle) { last = p; still.Restart(); }
                else if (still.ElapsedMilliseconds >= 3000) { stalled = true; return true; }
                return false;
            }, Common.FastPumpMs);
            // Files near the end of the list: the pass finished before the audit went past them by the slack.
            for (; next < during.Count; next++) After(during[next], ref appliedAfterPass);
            AssetAuditSnapshot first = audit.Snapshot;
            ctx.Metric("faults_planned", plan.Count);
            ctx.Metric("faults_applied", applied);
            ctx.Metric("faults_before_pass", appliedBefore);
            ctx.Metric("faults_after_indexed", appliedAfterIndexed);
            ctx.Metric("faults_after_pass", appliedAfterPass);
            ctx.Metric("expected_changed_files", expectChanged);
            ctx.Metric("files", _files.Count);
            ctx.Metric("items", first.Completed);
            ctx.Metric("audit_errors", first.Errors);
            ctx.Metric("audit_complete", first.Complete ? 1 : 0);
            ctx.Metric("prefetch_errors", cache.Errors);
            ctx.Metric("prefetch_complete", cache.PassComplete ? 1 : 0);
            ctx.Metric("native_held_mib", _store!.CommittedBytes / 1048576.0);
            ctx.Metric("stalled", stalled ? 1 : 0);

            // Second pass: audit rescan and file-cache refresh after the faults, as after the configured intervals.
            if (!stalled)
            {
                audit.Pump(true, _c.Workers, RescanMs); // records the finished pass
                cache.Pump(active: true, hold: true, limitBytes: budget, allowCopies: true, refreshIntervalMs: 5 * 60_000L);
                long heldBefore = _store.CommittedBytes;
                int refreshes = cache.PeriodicRefreshes;
                _clock.Advance(RescanMs);
                int nudges = 0;
                bool second = ctx.WaitUntil(() =>
                {
                    PumpBoth(5 * 60_000L);
                    bool refreshed = cache.PeriodicRefreshes > refreshes;
                    // A retry pass after interrupted copies comes first; move the clock again for the refresh.
                    if (!refreshed && cache.PassComplete && nudges < 3)
                    {
                        _clock.Advance(5 * 60_000L + 1);
                        nudges++;
                    }
                    return audit.Snapshot.Complete && cache.PassComplete && refreshed;
                }, Common.FastPumpMs, limit: TimeSpan.FromSeconds(Math.Max(10, ctx.Timeout.TotalSeconds - ctx.Elapsed.TotalSeconds)));
                AssetAuditSnapshot again = audit.Snapshot;
                ctx.Metric("rescan_complete", second ? 1 : 0);
                ctx.Metric("rescan_errors", again.Errors);
                ctx.Metric("rescan_changed_files", again.ChangedFiles);
                ctx.Metric("stale_copies_released_mib", Math.Max(0, heldBefore - _store.CommittedBytes) / 1048576.0);
            }
            ctx.Metric("jobs_failed", pool.JobsFailed);
            ctx.Metric("completed", !stalled && first.Complete && cache.PassComplete ? 1 : 0);
            // After the measurement: copies still held for files that no longer exist. Releasing them by
            // key returns exactly the bytes the cache kept for paths it can no longer open.
            long staleBytes = 0;
            int staleFiles = 0;
            foreach (var f in plan.Where(f => !File.Exists(f.Path)))
            {
                long freed = _store.ReleaseKey(f.Path);
                if (freed > 0) { staleBytes += freed; staleFiles++; }
            }
            ctx.Metric("copies_of_missing_files", staleFiles);
            ctx.Metric("copies_of_missing_files_mib", staleBytes / 1048576.0);
            if (pool.LastError != null) ctx.Note("exception escaped to the worker pool: " + pool.LastError);
        }

        public override void Dispose()
        {
            _audit?.Suspend();
            _cache?.Suspend();
            _pool?.Dispose();
            _store?.Dispose();
            // Restore permissions so the runner can delete the scratch directory.
            foreach (string f in _files)
            {
                try { if (!OperatingSystem.IsWindows() && File.Exists(f)) File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
