using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MoreUseResources.Benchmarks.Measurement;
using MoreUseResources.Benchmarks.Running;
using MoreUseResources.Core;

namespace MoreUseResources.Benchmarks.Scenarios;

/// <summary>
/// The file prefetch with OS-cache-only reads (RamHoldCopies off, as in the asset boost preset):
/// engine packs first, then loose assets, exactly as the controller orders them. The pump limit is
/// max(2, active workers × 2) reads in flight. "refresh" measures the periodic maintenance pass
/// (RamPrefetchRefreshMinutes) that re-reads every file after the first pass.
/// After each trial the page-cache residency of the inputs shows whether the prefetch had its effect.
/// </summary>
public sealed class PrefetchScenario : Scenario
{
    private const long RefreshMs = 5 * 60_000L;

    public override string Id => "prefetch";
    public override string Title => "先読み（OSファイルキャッシュへの読み込み）と定期リフレッシュ";
    public override string ProductionCode => "FileCache.Pump/ReadOne (prefetchOnly packs + loose files, hold=false), WorkerPool";
    public override string NotReproduced =>
        "Windows のスタンバイリスト／SuperFetch の挙動、ゲームが同時に読む I/O との競合、ゲームが実際に読む順序";

    public override IReadOnlyList<Condition> Conditions(RunEnvironment env)
    {
        if (env.Dataset.Files.Count + env.Dataset.Packs.Count == 0) throw new InvalidOperationException("no input files");
        var list = new List<Condition>();
        foreach (int w in env.Options.Workers.Distinct().OrderBy(x => x))
        foreach (string v in new[] { Common.Cadence, Common.Saturated })
        foreach (CacheState c in Caches(env))
            list.Add(Condition.Of(Id, ("workers", w), ("variant", v), ("cache", CacheName(c))));
        int refreshWorkers = Common.FittingWorkers(env.Options).Max();
        foreach (CacheState c in Caches(env))
            list.Add(Condition.Of(Id, ("workers", refreshWorkers), ("variant", "refresh"), ("cache", CacheName(c))));
        return list;
    }

    public override Trial CreateTrial(Condition condition, RunEnvironment env) => new PrefetchTrial(condition, env);

    private sealed class PrefetchTrial : Trial
    {
        private readonly Condition _c;
        private readonly RunEnvironment _env;
        private readonly ManualClock _clock = new();
        private readonly List<string> _targets;
        private WorkerPool? _pool;
        private NativeBlockStore? _store;
        private FileCache? _cache;

        public PrefetchTrial(Condition c, RunEnvironment env)
        {
            _c = c;
            _env = env;
            _targets = env.Dataset.Packs.Concat(env.Dataset.Files).ToList();
        }

        public override IReadOnlyList<string> CacheTargets => _targets;

        private bool Refresh => _c.Get("variant") == "refresh";

        public override void Prepare(TrialContext ctx)
        {
            _pool = Common.Pool(_c.Workers);
            _store = new NativeBlockStore();
            _cache = new FileCache(_store, _pool, clockMs: () => _clock.Now);
            _cache.SetSources(_targets, prefetchOnly: _env.Dataset.Packs);
            if (_c.Get("variant") != Common.Saturated) Common.RateWindow(ctx);
            if (Refresh)
            {
                WorkerPool pool = _pool;
                FileCache first = _cache;
                bool done = Common.Drive(ctx, pool, 0, () => PumpOnce(0), () => pool.PendingJobs == 0 && first.PassComplete,
                    limit: TimeSpan.FromSeconds(Math.Max(60, _env.Options.TrialTimeoutSeconds * 4))).Done;
                if (!done) throw new TimeoutException("first prefetch pass did not finish before the refresh");
                PumpOnce(RefreshMs); // records the finished pass
                ctx.Truncated = false;
            }
        }

        private void PumpOnce(long refreshMs) =>
            _cache!.Pump(active: true, hold: false, limitBytes: 0, allowCopies: false, refreshIntervalMs: refreshMs);

        public override void Execute(TrialContext ctx)
        {
            WorkerPool pool = _pool!;
            FileCache cache = _cache!;
            long busy = pool.BusyTicks;
            long files0 = cache.FilesRead, bytes0 = cache.BytesRead, refreshes0 = cache.PeriodicRefreshes;
            int interval = Refresh ? _env.Options.PumpIntervalMs : Common.PumpInterval(_c, _env.Options);
            if (Refresh) _clock.Advance(RefreshMs);
            var driven = System.Diagnostics.Stopwatch.StartNew();
            // A maintenance pass is counted in PeriodicRefreshes only once it has finished.
            Common.DriveResult drive = Common.Drive(ctx, pool, interval, () => PumpOnce(Refresh ? RefreshMs : 0),
                () => pool.PendingJobs == 0 && cache.PassComplete && (!Refresh || cache.PeriodicRefreshes > refreshes0));
            Common.DriveMetrics(ctx, drive, interval, driven.Elapsed);
            cache.Suspend();
            ctx.Metric("items", cache.FilesRead - files0);
            Common.ProjectPass(ctx, cache.SourceCount, cache.FilesRead - files0);
            ctx.Metric("bytes_mib", (cache.BytesRead - bytes0) / 1048576.0);
            ctx.Metric("errors", cache.Errors);
            ctx.Metric("sources", cache.SourceCount);
            ctx.Metric("packs", _env.Dataset.Packs.Count);
            ctx.Metric("in_flight_limit", Math.Max(2, pool.ActiveWorkers * 2));
            ctx.Metric("refresh_passes", cache.PeriodicRefreshes - refreshes0);
            ctx.Metric("native_held_mib", _store!.CommittedBytes / 1048576.0);
            ctx.Metric("workers", _c.Workers);
            Common.WorkerBusy(ctx, pool, busy, _c.Workers, drive.PumpJobs);
            Common.NoteOversubscription(ctx, _c.Workers);
        }

        public override void AfterMeasure(TrialContext ctx)
        {
            // FileCache reads each file in one job, so the largest one bounds how short a pass can be.
            long largest = 0, total = 0;
            foreach (string path in _targets)
            {
                try
                {
                    long length = new FileInfo(path).Length;
                    largest = Math.Max(largest, length);
                    total += length;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            ctx.Metric("largest_file_mib", largest / 1048576.0);
            if (total > 0) ctx.Metric("largest_file_share", (double)largest / total);
            Residency r = PageCache.Measure(_targets, ctx.Token);
            if (!double.IsNaN(r.Fraction)) ctx.Metric("cache_resident_after", r.Fraction);
        }

        public override void Dispose()
        {
            _cache?.Suspend();
            _pool?.Dispose();
            _store?.Dispose();
        }
    }
}
