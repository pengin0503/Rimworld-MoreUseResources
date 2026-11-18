using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MoreUseResources.Benchmarks.Measurement;
using MoreUseResources.Benchmarks.Running;
using MoreUseResources.Core;

namespace MoreUseResources.Benchmarks.Scenarios;

/// <summary>
/// The whole mod as it runs from the main menu: the real ResourceController with its own control
/// thread, discovery job, prefetch, audit, memory governor and (asset boost) pack residency.
/// Milestones are read from GetStatus like the status panel reads them. external=all-active
/// reports WOWGAG, Overclock and Axen as active with a busy shared pool; the worker count must not
/// change because of it (the coexistence rule of this mod).
/// </summary>
public sealed class ControllerScenario : Scenario
{
    public override string Id => "controller";
    public override string Title => "MOD全体（ResourceController）の起動から先読み・索引化完了まで";
    public override string ProductionCode => "ResourceController (ControlLoop, QueueAssetScan, FileCache, AssetAudit, MemoryGovernor, MappedPackResidency)";
    public override string NotReproduced =>
        "ゲームのメインスレッド・フレーム時間（フレームガード）、他MODの実際の並列処理による競合、セーブ中判定、GPU 関連機能、JIT 先行コンパイル";

    public override IReadOnlyList<Condition> Conditions(RunEnvironment env)
    {
        if (env.Dataset.LoadFolders.Count == 0) throw new InvalidOperationException("no load folders");
        var list = new List<Condition>();
        foreach (int w in env.Options.Workers.Distinct().OrderBy(x => x))
        foreach (CacheState c in Caches(env))
            list.Add(Condition.Of(Id, ("workers", w), ("preset", "default"), ("external", "none"), ("cache", CacheName(c))));
        foreach (CacheState c in Caches(env))
            list.Add(Condition.Of(Id, ("workers", "auto"), ("preset", "asset-boost"), ("external", "none"), ("cache", CacheName(c))));
        foreach (string external in new[] { "none", "all-active" })
            list.Add(Condition.Of(Id, ("workers", "auto"), ("preset", "default"), ("external", external), ("cache", "warm")));
        return list;
    }

    public override Trial CreateTrial(Condition condition, RunEnvironment env) => new ControllerTrial(condition, env);

    private sealed class ControllerTrial : Trial
    {
        private readonly Condition _c;
        private readonly RunEnvironment _env;
        private readonly List<string> _targets;
        private ResourceController? _controller;
        private ResourceSettings? _settings;
        private int _lp;

        public ControllerTrial(Condition c, RunEnvironment env)
        {
            _c = c;
            _env = env;
            _targets = env.Dataset.Packs.Concat(env.Dataset.DiscoveredFiles).ToList();
        }

        public override IReadOnlyList<string> CacheTargets => _targets;

        public override void Prepare(TrialContext ctx)
        {
            int requested = _c.Workers; // 0 for "auto"
            var notes = new List<string>();
            _settings = ControllerHarness.Settings(_env.Options, _c.Get("preset")!, requested, notes);
            foreach (string n in notes) ctx.Note(n);
            _lp = requested > 0 ? ControllerHarness.LogicalProcessors(requested) : Environment.ProcessorCount;
            if (_lp > Environment.ProcessorCount)
                ctx.Note("emulated " + _lp + " logical processors on " + Environment.ProcessorCount + " (oversubscribed)");
            _controller = ControllerHarness.Create(_env.Dataset, _settings, _lp);
            Common.RateWindow(ctx, 2);
            if (_c.Get("external") == "all-active") _controller.SetExternalParallelism(ControllerHarness.AllActive);
        }

        public override void Execute(TrialContext ctx)
        {
            ResourceController controller = _controller!;
            ResourceSettings s = _settings!;
            bool wantPacks = s.RamResidentPacks && _env.Dataset.Packs.Any(p => p.EndsWith(EnginePackFiles.AssetPackFileName, StringComparison.OrdinalIgnoreCase));
            double sources = double.NaN, prefetch = double.NaN, audit = double.NaN, packs = double.NaN;
            int maxActive = 0;
            // The controller's threads start and stop inside this method, so the runner's snapshots around
            // it cannot see them: their CPU is taken here, from before Start to before Shutdown.
            var threadsBefore = ProcessProbe.Threads();
            var clock = Stopwatch.StartNew();
            controller.Start();
            ControllerStatus status = controller.GetStatus();
            bool done = ctx.WaitUntil(() =>
            {
                status = controller.GetStatus();
                double t = clock.Elapsed.TotalMilliseconds;
                maxActive = Math.Max(maxActive, status.WorkersActive);
                if (double.IsNaN(sources) && status.FileSources > 0) sources = t;
                if (double.IsNaN(prefetch) && status.FileSources > 0 && status.FilePassComplete) prefetch = t;
                if (double.IsNaN(audit) && status.AuditSources > 0 && status.AuditComplete) audit = t;
                if (double.IsNaN(packs) && status.ResidentPackPasses > 0) packs = t;
                return !double.IsNaN(prefetch) && !double.IsNaN(audit) && (!wantPacks || !double.IsNaN(packs));
            }, 50); // GetStatus allocates; 50 ms keeps the observer's cost small against multi-second runs
            double active = clock.Elapsed.TotalMilliseconds;
            if (threadsBefore != null && ProcessProbe.Threads() is { } threadsAfter && ResourceDelta.ModRunNs(threadsBefore, threadsAfter) is { } modNs)
            {
                ctx.Metric("mod_cpu_ms", modNs / 1e6);
                ctx.Metric("mod_cpu_parallelism", modNs / 1e6 / Math.Max(active, 1e-9));
            }
            status = controller.GetStatus();
            ControllerHarness.StatusMetrics(ctx, status);
            ctx.Metric("active_ms", active);
            if (!double.IsNaN(sources)) ctx.Metric("time_to_sources_ms", sources);
            if (!double.IsNaN(prefetch)) ctx.Metric("time_to_prefetch_ms", prefetch);
            if (!double.IsNaN(audit)) ctx.Metric("time_to_audit_ms", audit);
            if (!double.IsNaN(packs)) ctx.Metric("time_to_packs_ms", packs);
            ctx.Metric("items", status.AuditCompleted);
            if (status.AuditSources > 0 && status.AuditCompleted > 0)
                ctx.Metric("projected_audit_s", status.AuditSources / (status.AuditCompleted / Math.Max(active / 1000.0, 1e-9)));
            ctx.Metric("bytes_mib", (status.FileBytesRead + status.AuditHashedBytes) / 1048576.0);
            ctx.Metric("errors", status.FileErrors + status.AuditErrors);
            ctx.Metric("workers_active_max", maxActive);
            ctx.Metric("workers_requested", _c.Workers);
            ctx.Metric("logical_processors_given", _lp);
            ctx.Metric("external_active", _c.Get("external") == "all-active" ? 1 : 0);
            ctx.Metric("result_hash", ControllerHarness.AuditHash(status));
            Common.NoteOversubscription(ctx, Math.Max(maxActive, 1));
            if (!done) ctx.Note("not all milestones reached within the trial timeout");
            ctx.Metric("shutdown_ms", ControllerHarness.Shutdown(controller));
        }

        public override void Dispose() => _controller?.Shutdown();
    }
}
