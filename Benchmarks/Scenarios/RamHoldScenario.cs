using System;
using System.Collections.Generic;
using System.Linq;
using MoreUseResources.Benchmarks.Measurement;
using MoreUseResources.Benchmarks.Running;
using MoreUseResources.Core;

namespace MoreUseResources.Benchmarks.Scenarios;

/// <summary>
/// What a later reader gains from each retention strategy, starting from a cold page cache:
/// none (the reader goes to storage), os-prefetch (FileCache with hold=false), hold-copies
/// (FileCache with private native copies within the RAM budget, the default RamHoldCopies=true) and
/// hold-evicted (copies held, then the page cache of the files dropped as memory pressure would).
/// The reader is one thread reading every file once, like a loader. RimWorld never reads the mod's
/// private copies, so any benefit has to come from the page cache; hold-evicted shows that directly.
/// </summary>
public sealed class RamHoldScenario : Scenario
{
    public static readonly string[] Strategies = { "none", "os-prefetch", "hold-copies", "hold-evicted" };

    public override string Id => "ram-hold";
    public override string Title => "OSファイルキャッシュとRAMコピー保持の比較（後続の読み込みへの効果）";
    public override string ProductionCode => "FileCache.Pump/ReadOne/ReadInto (hold=true/false), NativeBlockStore";
    public override string NotReproduced =>
        "ゲーム本体の読み込み（1.7 は Assets.zip とテクスチャキャッシュから読むため、ここでの後続読み込みはルーズファイルの逐次読みで代用）、Windows のメモリ圧迫時の挙動";

    public override IReadOnlyList<Condition> Conditions(RunEnvironment env)
    {
        if (env.Dataset.Files.Count == 0) throw new InvalidOperationException("no input files");
        if (!env.Options.Caches.Contains(CacheState.Cold) || !PageCache.Supported)
            throw new InvalidOperationException("ram-hold compares strategies from a cold page cache; --cache must include cold");
        return Strategies.Select(s => Condition.Of(Id, ("strategy", s), ("workers", Common.DefaultWorkers), ("cache", "cold"))).ToList();
    }

    public override Trial CreateTrial(Condition condition, RunEnvironment env) => new RamHoldTrial(condition, env);

    private sealed class RamHoldTrial : Trial
    {
        private readonly Condition _c;
        private readonly RunEnvironment _env;
        private WorkerPool? _pool;
        private NativeBlockStore? _store;
        private FileCache? _cache;

        public RamHoldTrial(Condition c, RunEnvironment env)
        {
            _c = c;
            _env = env;
        }

        public override IReadOnlyList<string> CacheTargets => _env.Dataset.Files;

        public override IReadOnlyList<Gauge> Gauges => _store == null ? Array.Empty<Gauge>() : new[] { new Gauge("native", () => _store.CommittedBytes) };

        public override void Prepare(TrialContext ctx)
        {
            _pool = Common.Pool(_c.Workers);
            _store = new NativeBlockStore();
            _cache = new FileCache(_store, _pool);
            _cache.SetSources(_env.Dataset.Files);
        }

        public override void Execute(TrialContext ctx)
        {
            string strategy = _c.Get("strategy")!;
            FileCache cache = _cache!;
            long budget = _env.Options.RamBudgetMiB * 1048576L;
            bool hold = strategy is "hold-copies" or "hold-evicted";
            if (strategy != "none")
            {
                ctx.Measure("prefetch", () => ctx.WaitUntil(() =>
                {
                    cache.Pump(active: true, hold: hold, limitBytes: hold ? budget : 0, allowCopies: hold);
                    return cache.PassComplete;
                }, Common.FastPumpMs));
                cache.Suspend();
            }
            ctx.Metric("native_held_mib", _store!.CommittedBytes / 1048576.0);
            ctx.Metric("copies_deferred_for_budget", cache.DeferredForBudget);
            ctx.Metric("warmed_without_copy", cache.BudgetFallbackWarms);
            ctx.Metric("prefetch_files", cache.FilesRead);
            ctx.Metric("prefetch_errors", cache.Errors);
            if (strategy == "hold-evicted") PageCache.Evict(_env.Dataset.Files, ctx.Token);
            Residency before = PageCache.Measure(_env.Dataset.Files, ctx.Token);
            if (!double.IsNaN(before.Fraction)) ctx.Metric("consumer_resident_before", before.Fraction);
            long bytes = 0;
            int errors = 0;
            ctx.Measure("consumer", () => bytes = Common.ConsumeSequential(_env.Dataset.Files, ctx.Token, out errors));
            ctx.Metric("items", _env.Dataset.Files.Count);
            ctx.Metric("bytes_mib", bytes / 1048576.0);
            ctx.Metric("errors", errors);
            ctx.Metric("budget_mib", _env.Options.RamBudgetMiB);
            ctx.Metric("workers", _c.Workers);
            if (ctx.Metrics.TryGetValue("consumer.wall_ms", out double consumerMs))
            {
                ctx.Metric("consumer_ms", consumerMs);
                if (consumerMs > 0) ctx.Metric("consumer_mib_per_s", bytes / 1048576.0 / (consumerMs / 1000.0));
            }
        }

        public override void Dispose()
        {
            _cache?.Suspend();
            _pool?.Dispose();
            _store?.Dispose();
        }
    }
}
