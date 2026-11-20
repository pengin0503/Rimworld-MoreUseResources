using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using MoreUseResources.Benchmarks.Analysis;
using MoreUseResources.Benchmarks.Measurement;
using MoreUseResources.Benchmarks.Running;
using MoreUseResources.Core;

namespace MoreUseResources.Benchmarks.Scenarios;

/// <summary>
/// Create, start, work briefly and shut down, many times in one trial: the real controller (its
/// control thread, worker pool, discovery job and file reads are interrupted by Shutdown) and the
/// worker pool alone. Per cycle the remaining MUR-* threads, open files, RSS and managed heap are
/// recorded; a positive Theil–Sen slope over the cycles indicates a leak.
/// </summary>
public sealed class LifecycleScenario : Scenario
{
    public override string Id => "lifecycle";
    public override string Title => "生成・開始・停止の繰り返しによるリソース解放の確認";
    public override string ProductionCode => "ResourceController.Start/Shutdown, WorkerPool.Configure/Dispose, NativeBlockStore.Dispose, MappedPackResidency.Dispose";
    public override string NotReproduced => "ゲーム終了時（Application.quitting）の呼び出し順、MOD 設定画面からの再構成";

    public override IReadOnlyList<Condition> Conditions(RunEnvironment env) => new[]
    {
        Condition.Of(Id, ("kind", "controller"), ("workers", Common.DefaultWorkers)),
        Condition.Of(Id, ("kind", "pool"), ("workers", Common.FittingWorkers(env.Options).Max())),
    };

    public override Trial CreateTrial(Condition condition, RunEnvironment env) => new LifecycleTrial(condition, env);

    private sealed class LifecycleTrial : Trial
    {
        private readonly Condition _c;
        private readonly RunEnvironment _env;

        public LifecycleTrial(Condition c, RunEnvironment env)
        {
            _c = c;
            _env = env;
        }

        public override void Execute(TrialContext ctx)
        {
            int cycles = _env.Options.LifecycleCycles;
            bool controller = _c.Get("kind") == "controller";
            var notes = new List<string>();
            ResourceSettings settings = ControllerHarness.Settings(_env.Options, "default", 0, notes);
            foreach (string n in notes) ctx.Note(n);
            var anon = new List<(double, double)>();
            var heap = new List<(double, double)>();
            var fds = new List<(double, double)>();
            var shutdown = new List<double>();
            int maxThreads = 0;
            int done = 0;
            IReadOnlyList<string> files = _env.Dataset.Files;
            // About 20 samples whatever the cycle count, so the slopes always have enough points.
            int step = Math.Max(1, cycles / 20);
            for (int i = 0; i < cycles; i++)
            {
                ctx.Token.ThrowIfCancellationRequested();
                if (ctx.Elapsed >= ctx.Timeout)
                {
                    ctx.Truncated = true;
                    break;
                }
                if (controller)
                {
                    var c = ControllerHarness.Create(_env.Dataset, settings, Environment.ProcessorCount);
                    c.Start();
                    Thread.Sleep(300 + (i % 5) * 50); // vary where Shutdown lands
                    var clock = Stopwatch.StartNew();
                    c.Shutdown();
                    shutdown.Add(clock.Elapsed.TotalMilliseconds);
                }
                else
                {
                    var pool = Common.Pool(_c.Workers);
                    int finished = 0;
                    int jobs = _c.Workers * 4;
                    for (int j = 0; j < jobs; j++)
                    {
                        string path = files[(i * jobs + j) % files.Count];
                        pool.Enqueue(_ =>
                        {
                            try { System.IO.File.ReadAllBytes(path); }
                            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
                            Interlocked.Increment(ref finished);
                        });
                    }
                    ctx.WaitUntil(() => Volatile.Read(ref finished) >= jobs, 1, limit: TimeSpan.FromSeconds(10));
                    var clock = Stopwatch.StartNew();
                    pool.Dispose();
                    shutdown.Add(clock.Elapsed.TotalMilliseconds);
                }
                maxThreads = Math.Max(maxThreads, BenchmarkRunner.WaitForModThreads(2000));
                if (i % step == step - 1 || i == cycles - 1)
                {
                    // Sample after a full collection so managed garbage does not look like growth.
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                    ResourceSnapshot s = ProcessProbe.Capture(includeThreads: false);
                    if (s.RssAnonBytes.HasValue) anon.Add((i, s.RssAnonBytes.Value / 1024.0));
                    heap.Add((i, GC.GetTotalMemory(false) / 1024.0));
                    if (s.OpenFiles.HasValue) fds.Add((i, s.OpenFiles.Value));
                }
                done++;
            }
            ctx.Metric("items", done);
            ctx.Metric("cycles", done);
            ctx.Metric("leaked_threads_max", maxThreads);
            if (shutdown.Count > 0)
            {
                MetricSummary sd = Statistics.Summarize(shutdown)!;
                ctx.Metric("shutdown_median_ms", sd.Median);
                ctx.Metric("shutdown_max_ms", sd.Max);
            }
            if (anon.Count >= 3) ctx.Metric("rss_anon_slope_kib_per_cycle", Statistics.TheilSenSlope(anon));
            if (heap.Count >= 3) ctx.Metric("heap_slope_kib_per_cycle", Statistics.TheilSenSlope(heap));
            if (fds.Count >= 3) ctx.Metric("open_files_slope_per_cycle", Statistics.TheilSenSlope(fds));
            if (anon.Count >= 2) ctx.Metric("rss_anon_growth_mib", (anon[^1].Item2 - anon[0].Item2) / 1024.0);
            ctx.Metric("workers", _c.Workers);
        }
    }
}
