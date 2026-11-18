using System;
using System.Collections.Generic;
using System.Linq;
using MoreUseResources.Benchmarks.Running;
using MoreUseResources.Core;

namespace MoreUseResources.Benchmarks.Scenarios;

/// <summary>
/// The mod's asset audit (SHA-256 of every file, duplicate detection, XML inventory, PNG/DDS checks)
/// over real files, driven exactly as the controller drives it: <c>Pump(active, workers)</c> with an
/// in-flight limit of clamp(workers × 2, 1, 32). "cadence" calls Pump every 250 ms like the controller's
/// slow tick; "saturated" pumps whenever the workers have taken every queued job (Common.Drive) to show
/// what the work itself allows.
/// "rescan" measures the periodic second pass (CpuAuditRescanMinutes) after a completed first pass.
/// </summary>
public sealed class AuditScenario : Scenario
{
    public override string Id => "audit";
    public override string Title => "実アセットのSHA-256索引化・重複検出・XML/テクスチャ検査（AssetAudit）";
    public override string ProductionCode => "AssetAudit.Pump/ProcessFile, XmlManifestScanner.Scan, TextureAssetInspector.Inspect, WorkerPool";
    public override string NotReproduced =>
        "ゲームのロード処理と同時に走る競合、Windows のファイルキャッシュとウイルス対策ソフトの影響、ゲーム内で設定される実ワーカー優先度";

    public override IReadOnlyList<Condition> Conditions(RunEnvironment env)
    {
        if (env.Dataset.Files.Count == 0) throw new InvalidOperationException("no input files");
        var list = new List<Condition>();
        foreach (int w in env.Options.Workers.Distinct().OrderBy(x => x))
        foreach (string v in new[] { Common.Cadence, Common.Saturated })
        foreach (CacheState c in Caches(env))
            list.Add(Condition.Of(Id, ("workers", w), ("variant", v), ("cache", CacheName(c))));
        int rescanWorkers = Common.FittingWorkers(env.Options).Max();
        list.Add(Condition.Of(Id, ("workers", rescanWorkers), ("variant", "rescan"), ("cache", "warm")));
        return list;
    }

    public override Trial CreateTrial(Condition condition, RunEnvironment env) => new AuditTrial(condition, env);

    private sealed class AuditTrial : Trial
    {
        private const long RescanMs = 10 * 60_000L;
        private readonly Condition _c;
        private readonly RunEnvironment _env;
        private readonly ManualClock _clock = new();
        private WorkerPool? _pool;
        private AssetAudit? _audit;

        public AuditTrial(Condition c, RunEnvironment env)
        {
            _c = c;
            _env = env;
        }

        public override IReadOnlyList<string> CacheTargets => _env.Dataset.Files;

        public override void Prepare(TrialContext ctx)
        {
            _pool = Common.Pool(_c.Workers);
            _audit = new AssetAudit(_pool, clockMs: () => _clock.Now);
            _audit.SetSources(_env.Dataset.Files);
            if (_c.Get("variant") != Common.Saturated) Common.RateWindow(ctx);
            if (_c.Get("variant") == "rescan")
            {
                // First pass outside the measured window, as fast as possible.
                AssetAudit first = _audit;
                WorkerPool pool = _pool;
                bool done = Common.Drive(ctx, pool, 0, () => first.Pump(true, _c.Workers, RescanMs), () => pool.PendingJobs == 0 && first.Snapshot.Complete,
                    limit: TimeSpan.FromSeconds(Math.Max(60, _env.Options.TrialTimeoutSeconds * 4))).Done;
                if (!done) throw new TimeoutException("first audit pass did not finish before the rescan");
                _audit.Pump(true, _c.Workers, RescanMs); // records the finished pass
                ctx.Truncated = false;
            }
        }

        public override void Execute(TrialContext ctx)
        {
            WorkerPool pool = _pool!;
            AssetAudit audit = _audit!;
            long busy = pool.BusyTicks;
            bool rescan = _c.Get("variant") == "rescan";
            int interval = rescan ? _env.Options.PumpIntervalMs : Common.PumpInterval(_c, _env.Options);
            if (rescan) _clock.Advance(RescanMs);
            int passesBefore = audit.Snapshot.PassesCompleted;
            var driven = System.Diagnostics.Stopwatch.StartNew();
            // A rescan resets the results synchronously inside the first Pump, so Complete afterwards means
            // the new pass. A pass can only be complete with nothing queued, so Snapshot (which takes the
            // audit's lock) is read only then.
            Common.DriveResult drive = Common.Drive(ctx, pool, interval, () => audit.Pump(true, _c.Workers, rescan ? RescanMs : 0),
                () => pool.PendingJobs == 0 && audit.Snapshot.Complete);
            Common.DriveMetrics(ctx, drive, interval, driven.Elapsed);
            audit.Suspend();
            AssetAuditSnapshot snap = audit.Snapshot;
            Common.AuditMetrics(ctx, snap);
            Common.ProjectPass(ctx, snap.Sources, snap.Completed);
            ctx.Metric("in_flight_limit", Math.Clamp(_c.Workers * 2, 1, 32));
            ctx.Metric("changed_files", snap.ChangedFiles);
            ctx.Metric("passes_before", passesBefore);
            ctx.Metric("workers", _c.Workers);
            Common.WorkerBusy(ctx, pool, busy, _c.Workers, drive.PumpJobs);
            Common.NoteOversubscription(ctx, _c.Workers);
            if (ctx.Truncated) ctx.Note("audit pass not finished within the trial timeout; rates are valid, completion time is not");
        }

        public override void Dispose()
        {
            _audit?.Suspend();
            _pool?.Dispose();
        }
    }
}
