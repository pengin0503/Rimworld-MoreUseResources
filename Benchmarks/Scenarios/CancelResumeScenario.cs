using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using MoreUseResources.Benchmarks.Measurement;
using MoreUseResources.Benchmarks.Running;
using MoreUseResources.Core;

namespace MoreUseResources.Benchmarks.Scenarios;

/// <summary>
/// Stopping and resuming parallel work. Component level: AssetAudit suspended three times
/// (Pump(false) as the controller does when CPU work stops), the pool reconfigured 4→1→4 mid-pass,
/// and WorkerPool.CancelJobs mid-pass (a latent path: in the mod only Dispose calls it).
/// Controller level: Manual = Paused and back, and a settings change of the worker limit and of
/// RamHoldCopies while work is running. Each trial checks that the final result equals an
/// uninterrupted pass (result_hash), how fast work drains, and that nothing is read while paused.
/// </summary>
public sealed class CancelResumeScenario : Scenario
{
    private const int StallMs = 3000;

    public override string Id => "cancel-resume";
    public override string Title => "並列処理の停止・再開・設定変更・キャンセル";
    public override string ProductionCode => "AssetAudit.Suspend/Pump, WorkerPool.Configure/CancelJobs, ResourceController.Manual/UpdateSettings";
    public override string NotReproduced => "セーブ開始・ゲーム終了と同時に起きる停止（ゲームのフックが必要）";

    public override IReadOnlyList<Condition> Conditions(RunEnvironment env)
    {
        if (env.Dataset.Files.Count == 0) throw new InvalidOperationException("no input files");
        int w = Common.FittingWorkers(env.Options).Max();
        string cache = env.Options.Caches.Contains(CacheState.Cold) && PageCache.Supported ? "cold" : "warm";
        var list = new List<Condition>();
        foreach (int workers in new[] { 1, w }.Distinct())
            list.Add(Condition.Of(Id, ("level", "component"), ("action", "suspend"), ("workers", workers), ("cache", cache)));
        list.Add(Condition.Of(Id, ("level", "component"), ("action", "reconfigure"), ("workers", w), ("cache", cache)));
        list.Add(Condition.Of(Id, ("level", "component"), ("action", "cancel-jobs"), ("workers", w), ("cache", "warm")));
        list.Add(Condition.Of(Id, ("level", "controller"), ("action", "manual-pause"), ("workers", w), ("cache", cache)));
        list.Add(Condition.Of(Id, ("level", "controller"), ("action", "settings-change"), ("workers", w), ("cache", cache)));
        return list;
    }

    public override Trial CreateTrial(Condition condition, RunEnvironment env) =>
        condition.Get("level") == "controller" ? new ControllerPauseTrial(condition, env) : new ComponentTrial(condition, env);

    /// <summary>Waits for <paramref name="done"/>; fails as stalled when progress and the pool stay idle for 3 s.</summary>
    private static bool RunUntil(TrialContext ctx, Func<bool> done, Func<long> progress, Func<bool> idle, int pollMs, out bool stalled)
    {
        long last = progress();
        var still = Stopwatch.StartNew();
        bool s = false;
        bool finished = ctx.WaitUntil(() =>
        {
            if (done()) return true;
            long now = progress();
            if (now != last || !idle())
            {
                last = now;
                still.Restart();
            }
            else if (still.ElapsedMilliseconds >= StallMs)
            {
                s = true;
                return true;
            }
            return false;
        }, pollMs);
        stalled = s;
        return finished && !s;
    }

    /// <summary>Reads, CPU and progress over a fixed paused window.</summary>
    private static void PausedWindow(TrialContext ctx, Func<long> progress, int ms, string prefix)
    {
        long p0 = progress();
        ResourceSnapshot a = ProcessProbe.Capture(includeThreads: false);
        ctx.WaitUntil(() => false, 20, limit: TimeSpan.FromMilliseconds(ms));
        ctx.Truncated = false;
        ResourceSnapshot b = ProcessProbe.Capture(includeThreads: false);
        ctx.Metric(prefix + "paused_progress", progress() - p0);
        ctx.Metric(prefix + "paused_cpu_ms", b.CpuTotalMs - a.CpuTotalMs);
        if (a.Io.HasValue && b.Io.HasValue)
            ctx.Metric(prefix + "paused_read_mib", (b.Io.Value.ReadChars - a.Io.Value.ReadChars) / 1048576.0);
    }

    private sealed class ComponentTrial : Trial
    {
        private readonly Condition _c;
        private readonly RunEnvironment _env;
        private WorkerPool? _pool;
        private AssetAudit? _audit;

        public ComponentTrial(Condition c, RunEnvironment env)
        {
            _c = c;
            _env = env;
        }

        public override IReadOnlyList<string> CacheTargets => _env.Dataset.Files;

        public override void Prepare(TrialContext ctx)
        {
            _pool = Common.Pool(_c.Workers);
            _audit = new AssetAudit(_pool);
            _audit.SetSources(_env.Dataset.Files);
        }

        public override void Execute(TrialContext ctx)
        {
            WorkerPool pool = _pool!;
            AssetAudit audit = _audit!;
            string action = _c.Get("action")!;
            int workers = _c.Workers;
            int sources = _env.Dataset.Files.Count;
            long Progress() => audit.Snapshot.Completed;
            bool Idle() => pool.RunningJobs == 0 && pool.PendingJobs == 0;
            var drains = new List<double>();
            var segments = new List<double>();
            double[] marks = action == "reconfigure" ? new[] { 1 / 3.0, 2 / 3.0 } : action == "cancel-jobs" ? new[] { 0.5 } : new[] { 0.25, 0.5, 0.75 };
            var segment = Stopwatch.StartNew();
            long segmentStart = 0;
            int pauseMs = _env.Options.Mode == RunMode.Smoke ? 300 : 1000;
            bool stalled = false;
            foreach (double mark in marks)
            {
                long target = (long)Math.Ceiling(sources * mark);
                bool reached = RunUntil(ctx, () => { audit.Pump(true, workers); return audit.Snapshot.Completed >= target || audit.Snapshot.Complete; },
                    Progress, Idle, Common.FastPumpMs, out stalled);
                if (!reached || audit.Snapshot.Complete) break;
                segments.Add((Progress() - segmentStart) / Math.Max(segment.Elapsed.TotalSeconds, 1e-9));
                if (action == "suspend")
                {
                    var drain = Stopwatch.StartNew();
                    audit.Pump(false, workers);
                    ctx.WaitUntil(Idle, 1, limit: TimeSpan.FromSeconds(10));
                    drains.Add(drain.Elapsed.TotalMilliseconds);
                    PausedWindow(ctx, Progress, pauseMs, drains.Count == 1 ? "" : "later_");
                    var resume = Stopwatch.StartNew();
                    long before = Progress();
                    ctx.WaitUntil(() => { audit.Pump(true, workers); return Progress() > before || audit.Snapshot.Complete; }, 1, limit: TimeSpan.FromSeconds(10));
                    ctx.Metric(drains.Count == 1 ? "resume_ms" : "later_resume_ms", resume.Elapsed.TotalMilliseconds);
                }
                else if (action == "reconfigure")
                {
                    workers = workers == _c.Workers ? 1 : _c.Workers;
                    pool.Configure(workers, jobsAllowed: true, synthetic: false, dutyPercent: 50, ThreadPriority.Lowest);
                }
                else
                {
                    ctx.Metric("dropped_jobs", pool.PendingJobs);
                    pool.CancelJobs();
                }
                segment.Restart();
                segmentStart = Progress();
            }
            if (!stalled && !audit.Snapshot.Complete)
                RunUntil(ctx, () => { audit.Pump(true, workers); return audit.Snapshot.Complete; }, Progress, Idle, Common.FastPumpMs, out stalled);
            segments.Add((Progress() - segmentStart) / Math.Max(segment.Elapsed.TotalSeconds, 1e-9));
            audit.Suspend();
            AssetAuditSnapshot snap = audit.Snapshot;
            Common.AuditMetrics(ctx, snap);
            ctx.Metric("completed", snap.Complete ? 1 : 0);
            ctx.Metric("stalled", stalled ? 1 : 0);
            ctx.Metric("workers", _c.Workers);
            if (drains.Count > 0)
            {
                ctx.Metric("drain_ms", drains[0]);
                ctx.Metric("drain_max_ms", drains.Max());
            }
            if (action == "reconfigure" && segments.Count >= 3)
            {
                ctx.Metric("rate_before_items_per_s", segments[0]);
                ctx.Metric("rate_reduced_items_per_s", segments[1]);
                ctx.Metric("rate_restored_items_per_s", segments[2]);
            }
            if (stalled) ctx.Note("no progress for " + StallMs + " ms with an idle pool: the pass cannot finish (" + action + ")");
        }

        public override void Dispose()
        {
            _audit?.Suspend();
            _pool?.Dispose();
        }
    }

    private sealed class ControllerPauseTrial : Trial
    {
        private readonly Condition _c;
        private readonly RunEnvironment _env;
        private readonly List<string> _targets;
        private ResourceController? _controller;
        private ResourceSettings? _settings;

        public ControllerPauseTrial(Condition c, RunEnvironment env)
        {
            _c = c;
            _env = env;
            _targets = env.Dataset.Packs.Concat(env.Dataset.DiscoveredFiles).ToList();
        }

        public override IReadOnlyList<string> CacheTargets => _targets;

        public override void Prepare(TrialContext ctx)
        {
            var notes = new List<string>();
            _settings = ControllerHarness.Settings(_env.Options, "default", _c.Workers, notes);
            foreach (string n in notes) ctx.Note(n);
            _controller = ControllerHarness.Create(_env.Dataset, _settings, Environment.ProcessorCount);
            Common.RateWindow(ctx, 3);
        }

        public override void Execute(TrialContext ctx)
        {
            ResourceController controller = _controller!;
            string action = _c.Get("action")!;
            ControllerStatus Status() => controller.GetStatus();
            long Progress() { ControllerStatus s = Status(); return s.AuditCompleted + s.FilesRead; }
            bool Idle() { ControllerStatus s = Status(); return s.PendingJobs == 0 && s.WorkersActive == 0; }
            bool Done() { ControllerStatus s = Status(); return s.AuditSources > 0 && s.AuditComplete && s.FileSources > 0 && s.FilePassComplete; }
            int pauseMs = _env.Options.Mode == RunMode.Smoke ? 500 : 1500;
            controller.Start();
            // Interrupt after a third of the audit or a third of the window, whichever comes first,
            // with sources known: the controller's own cadence makes a full pass slow on large data.
            double interruptAt = ctx.Timeout.TotalMilliseconds / 3;
            bool reached = RunUntil(ctx, () =>
            {
                ControllerStatus s = Status();
                return Done() || (s.AuditSources > 0 && s.AuditCompleted > 0 && (s.AuditCompleted >= s.AuditSources / 3 || ctx.Elapsed.TotalMilliseconds >= interruptAt));
            }, Progress, Idle, 20, out bool stalled);
            if (reached && !Done())
            {
                if (action == "manual-pause")
                {
                    var drain = Stopwatch.StartNew();
                    controller.Manual = ManualMode.Paused;
                    // The control loop sets the pool to 0 active workers; queued jobs stay parked in the
                    // queue until resume, so "drained" means no active worker, not an empty queue.
                    ctx.WaitUntil(() => Status().WorkersActive == 0, 5, limit: TimeSpan.FromSeconds(10));
                    ctx.Metric("drain_ms", drain.Elapsed.TotalMilliseconds);
                    ctx.Metric("parked_jobs", Status().PendingJobs);
                    PausedWindow(ctx, Progress, pauseMs, "");
                    ctx.Metric("paused_native_held_mib", Status().CacheHeldBytes / 1048576.0);
                    var resume = Stopwatch.StartNew();
                    long before = Progress();
                    controller.Manual = ManualMode.Auto;
                    ctx.WaitUntil(() => Progress() > before || Done(), 5, limit: TimeSpan.FromSeconds(10));
                    ctx.Metric("resume_ms", resume.Elapsed.TotalMilliseconds);
                }
                else
                {
                    ResourceSettings one = _settings!.Clone();
                    one.CpuMaxWorkers = 1;
                    var reconfigure = Stopwatch.StartNew();
                    controller.UpdateSettings(one);
                    ctx.WaitUntil(() => Status().WorkersActive <= 1, 5, limit: TimeSpan.FromSeconds(10));
                    ctx.Metric("reconfigure_ms", reconfigure.Elapsed.TotalMilliseconds);
                    ctx.Metric("workers_after_reduce", Status().WorkersActive);
                    ResourceSettings osOnly = _settings!.Clone();
                    osOnly.RamHoldCopies = false;
                    double heldBefore = Status().CacheHeldBytes / 1048576.0;
                    var toggle = Stopwatch.StartNew();
                    controller.UpdateSettings(osOnly);
                    ctx.WaitUntil(() => Status().CacheHeldBytes == 0, 5, limit: TimeSpan.FromSeconds(10));
                    ctx.Metric("hold_off_release_ms", toggle.Elapsed.TotalMilliseconds);
                    ctx.Metric("held_before_toggle_mib", heldBefore);
                    ctx.Metric("held_after_toggle_mib", Status().CacheHeldBytes / 1048576.0);
                    ctx.WaitUntil(() => Status().WorkersActive >= Math.Min(_c.Workers, 2) || Done(), 5, limit: TimeSpan.FromSeconds(10));
                    ctx.Metric("workers_after_restore", Status().WorkersActive);
                }
            }
            if (!stalled && !Done()) RunUntil(ctx, Done, Progress, Idle, 20, out stalled);
            ControllerStatus final = Status();
            ControllerHarness.StatusMetrics(ctx, final);
            ctx.Metric("items", final.AuditCompleted);
            ctx.Metric("completed", Done() ? 1 : 0);
            ctx.Metric("stalled", stalled ? 1 : 0);
            ctx.Metric("result_hash", ControllerHarness.AuditHash(final));
            ctx.Metric("workers", _c.Workers);
            ctx.Metric("shutdown_ms", ControllerHarness.Shutdown(controller));
            if (stalled) ctx.Note("controller made no progress for " + StallMs + " ms while idle (" + action + ")");
        }

        public override void Dispose() => _controller?.Shutdown();
    }
}
