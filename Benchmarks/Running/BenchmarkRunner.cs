using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Threading;
using MoreUseResources.Benchmarks.Analysis;
using MoreUseResources.Benchmarks.Measurement;

namespace MoreUseResources.Benchmarks.Running;

/// <summary>
/// Runs scenarios trial by trial. Conditions are interleaved within each round in a seeded order,
/// so slow drift of the machine (other tenants, thermal state, page cache) spreads over all
/// conditions instead of biasing the one measured last.
/// </summary>
public sealed class BenchmarkRunner
{
    public const string ModThreadPrefix = "MUR-";
    private readonly RunEnvironment _env;
    private readonly ResultWriter _writer;
    private readonly string _runId;
    private readonly Dictionary<string, object?> _metadata;
    private int _sequence;

    public BenchmarkRunner(RunEnvironment env, ResultWriter writer, string runId, Dictionary<string, object?> metadata)
    {
        _env = env;
        _writer = writer;
        _runId = runId;
        _metadata = metadata;
    }

    public List<TrialResult> Results { get; } = new();

    public static IReadOnlyList<Scenario> Catalog() => new Scenario[]
    {
        new Scenarios.BaselineScenario(),
        new Scenarios.AuditScenario(),
        new Scenarios.ScanScenario(),
        new Scenarios.DiscoveryScenario(),
        new Scenarios.PrefetchScenario(),
        new Scenarios.RamHoldScenario(),
        new Scenarios.MappedPackScenario(),
        new Scenarios.ControllerScenario(),
        new Scenarios.CancelResumeScenario(),
        new Scenarios.MemoryPressureScenario(),
        new Scenarios.IoFaultScenario(),
        new Scenarios.XmlCacheScenario(),
        new Scenarios.LifecycleScenario(),
    };

    public static IReadOnlyList<Scenario> Select(BenchOptions options)
    {
        IReadOnlyList<Scenario> all = Catalog();
        if (options.Scenarios == null) return all;
        return options.Scenarios.Select(id => all.First(s => s.Id == id)).ToList();
    }

    /// <summary>Runs the scenarios; returns true when every planned trial ran (not interrupted).</summary>
    public bool Run(IReadOnlyList<Scenario> scenarios, CancellationToken token, Func<string?> stopReason)
    {
        foreach (Scenario scenario in scenarios)
        {
            if (token.IsCancellationRequested) break;
            if (scenario.RequiresPacks && _env.Dataset.Packs.Count == 0)
            {
                Skip(scenario, "no Assets.zip-like pack in the input (use --data with official Data or --pack)");
                continue;
            }
            IReadOnlyList<Condition> conditions;
            try { conditions = scenario.Conditions(_env); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                Skip(scenario, ex.Message);
                continue;
            }
            if (conditions.Count == 0)
            {
                Skip(scenario, "no applicable condition for this input");
                continue;
            }
            int total = _env.Options.Warmup + _env.Options.Trials;
            for (int r = 0; r < total && !token.IsCancellationRequested; r++)
            {
                bool warmup = r < _env.Options.Warmup;
                int round = r - _env.Options.Warmup; // warm-up rounds are negative
                foreach (Condition condition in Order(conditions, scenario.Id, round, _env.Options.Seed))
                {
                    if (token.IsCancellationRequested) break;
                    TrialResult result = RunTrial(scenario, condition, round, warmup, token);
                    Results.Add(result);
                    _writer.Append(result);
                    _env.Log(Describe(result, total));
                }
                if (warmup && r == _env.Options.Warmup - 1 && !token.IsCancellationRequested)
                {
                    (double waitedMs, bool settled) = WaitForJitQuiescence(token);
                    _env.Log($"{scenario.Id}: warm-up done, JIT {(settled ? "settled" : "still compiling")} after {waitedMs:F0} ms");
                }
            }
            WriteSummary(interrupted: token.IsCancellationRequested, stopReason());
        }
        WriteSummary(interrupted: token.IsCancellationRequested, stopReason());
        return !token.IsCancellationRequested;
    }

    /// <summary>
    /// Waits until the runtime has compiled no new method for 300 ms, at most 5 s. Tiered compilation
    /// recompiles hot methods on a background thread some 100 ms after the warm-up first calls them; without
    /// this wait that work lands in the first measured trials of short scenarios.
    /// </summary>
    public static (double WaitedMs, bool Settled) WaitForJitQuiescence(CancellationToken token, int quietMs = 300, int maxMs = 5000)
    {
        var total = Stopwatch.StartNew();
        var quiet = Stopwatch.StartNew();
        long last = System.Runtime.JitInfo.GetCompiledMethodCount();
        while (quiet.ElapsedMilliseconds < quietMs)
        {
            if (total.ElapsedMilliseconds >= maxMs || token.IsCancellationRequested) return (total.Elapsed.TotalMilliseconds, false);
            Thread.Sleep(25);
            long now = System.Runtime.JitInfo.GetCompiledMethodCount();
            if (now != last)
            {
                last = now;
                quiet.Restart();
            }
        }
        return (total.Elapsed.TotalMilliseconds, true);
    }

    private void Skip(Scenario scenario, string reason)
    {
        var result = new TrialResult
        {
            RunId = _runId, Sequence = ++_sequence, Scenario = scenario.Id, ConditionKey = "", Parameters = new Dictionary<string, string>(),
            Round = 0, Warmup = false, StartedUtc = DateTime.UtcNow, Status = TrialStatus.Skipped, Error = PathRedactor.Redact(reason),
        };
        Results.Add(result);
        _writer.Append(result);
        _env.Log(scenario.Id + ": skipped (" + reason + ")");
    }

    private string Describe(TrialResult r, int rounds)
    {
        string wall = r.Metrics.TryGetValue("wall_ms", out double w) ? w.ToString("F0") + " ms" : "-";
        string rate = r.Metrics.TryGetValue("items_per_s", out double ips) ? ", " + ips.ToString("F1") + " items/s" : "";
        string state = r.Status != TrialStatus.Ok ? r.Status.ToString().ToLowerInvariant() + (r.Error != null ? " (" + r.Error + ")" : "")
            : !r.Valid ? "not counted (" + r.InvalidReason + ")" : r.Truncated ? "truncated" : "ok";
        return DateTime.Now.ToString("HH:mm:ss") + " " + r.Scenario + " " + r.ConditionKey + (r.Warmup ? " warm-up" : " round " + (r.Round + 1) + "/" + (rounds - _env.Options.Warmup))
            + ": " + wall + rate + ", " + state;
    }

    /// <summary>Seeded order per round; the same seed reproduces the same schedule.</summary>
    public static List<Condition> Order(IReadOnlyList<Condition> conditions, string scenario, int round, int seed = 20261118)
    {
        var list = conditions.ToList();
        var random = new Random(unchecked(seed * 31 + StableHash(scenario) * 17 + round));
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    public static int StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in s) h = (h ^ c) * 16777619;
            return (int)(h & 0x7fffffff);
        }
    }

    private TrialResult RunTrial(Scenario scenario, Condition condition, int round, bool warmup, CancellationToken token)
    {
        var result = new TrialResult
        {
            RunId = _runId, Sequence = ++_sequence, Scenario = scenario.Id, ConditionKey = condition.Key,
            Parameters = condition.Parameters, Round = round, Warmup = warmup, StartedUtc = DateTime.UtcNow,
            CacheRequested = condition.Cache == CacheState.Unmanaged ? "unmanaged" : CacheName(condition.Cache),
        };
        string scratch = Path.Combine(_env.ScratchRoot, "trial-" + result.Sequence);
        Directory.CreateDirectory(scratch);
        var context = new TrialContext(condition, _env, scratch, token);
        Dictionary<int, string> baselineFds = ProcessProbe.OpenDescriptors();
        Trial? trial = null;
        PeakSampler? sampler = null;
        try
        {
            trial = scenario.CreateTrial(condition, _env);
            trial.Prepare(context);
            PrepareCache(condition.Cache, trial.CacheTargets, context, result);
            CollectFully();
            bool peakReset = ProcessProbe.ResetPeakRss();
            sampler = new PeakSampler(_env.Options.SamplerIntervalMs, trial.Gauges);
            sampler.Start();
            ResourceSnapshot before = ProcessProbe.Capture();
            DateTime startUtc = DateTime.UtcNow;
            context.StartClock();
            trial.Execute(context);
            ResourceSnapshot after = ProcessProbe.Capture();
            DateTime endUtc = DateTime.UtcNow;
            sampler.Stop();
            trial.AfterMeasure(context);
            ResourceDelta.Add(result.Metrics, before, after, _env.LogicalProcessors, peakRssReset: peakReset);
            if (sampler.PeakRssAnonBytes > 0) result.Metrics["rss_anon_peak_mib"] = sampler.PeakRssAnonBytes / ResourceDelta.MiB;
            if (sampler.PeakRssFileBytes > 0) result.Metrics["rss_file_peak_mib"] = sampler.PeakRssFileBytes / ResourceDelta.MiB;
            foreach (Gauge g in sampler.Gauges) result.Metrics[g.Name + "_peak_mib"] = sampler.PeakOf(g.Name) / ResourceDelta.MiB;
            AddPauses(result.Metrics, startUtc, endUtc);
            foreach (var pair in context.Metrics) result.Metrics[pair.Key] = pair.Value;
            AddRates(result.Metrics);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            result.Status = TrialStatus.Interrupted;
            result.Error = "run stopped";
        }
        catch (Exception ex)
        {
            result.Status = TrialStatus.Failed;
            result.Error = ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            sampler?.Dispose();
            var teardown = Stopwatch.StartNew();
            try { trial?.Dispose(); }
            catch (Exception ex)
            {
                result.Status = TrialStatus.Failed;
                result.Error ??= "teardown " + ex.GetType().Name + ": " + ex.Message;
            }
            result.Metrics["teardown_ms"] = teardown.Elapsed.TotalMilliseconds;
            result.Metrics["leaked_mod_threads"] = WaitForModThreads(3000);
            (int files, int runtime, List<string> targets) = WaitForDescriptors(baselineFds, 1000);
            result.Metrics["leaked_open_files"] = files;
            result.Metrics["leaked_runtime_fds"] = runtime;
            if (targets.Count > 0) context.Note("descriptors still open after teardown: " + string.Join(", ", targets.Take(8)));
            try { Directory.Delete(scratch, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        result.Truncated = context.Truncated;
        if (!context.Valid)
        {
            result.Valid = false;
            result.InvalidReason = context.InvalidReason;
        }
        result.Notes.AddRange(context.Notes.Select(n => PathRedactor.Redact(n)!));
        result.Error = PathRedactor.Redact(result.Error);
        result.InvalidReason = PathRedactor.Redact(result.InvalidReason);
        return result;
    }

    private static string CacheName(CacheState c) => c == CacheState.Cold ? "cold" : "warm";

    /// <summary>Puts the inputs in the requested page-cache state and verifies it with mincore.</summary>
    private void PrepareCache(CacheState state, IReadOnlyList<string> files, TrialContext context, TrialResult result)
    {
        if (state == CacheState.Unmanaged || files.Count == 0) return;
        if (!PageCache.Supported)
        {
            result.CacheVerified = false;
            context.Invalidate("page cache state cannot be controlled on this OS");
            return;
        }
        if (state == CacheState.Cold)
        {
            bool dropped = false;
            if (_env.Options.ColdMethod == "drop-caches")
            {
                dropped = PageCache.DropAll(includeMetadata: true, out string? error);
                if (!dropped) context.Note("drop_caches failed (" + error + "); used fadvise");
            }
            if (!dropped) PageCache.Evict(files, context.Token);
        }
        else PageCache.Warm(files, context.Token);
        Residency r = PageCache.Measure(files, context.Token);
        context.Metric("cache_resident_before", r.Fraction);
        bool verified = r.TotalPages > 0 && (state == CacheState.Cold ? r.Fraction <= 0.02 : r.Fraction >= 0.98);
        result.CacheVerified = verified;
        if (!verified)
            context.Invalidate((state == CacheState.Cold ? "cold" : "warm") + " page cache not reproduced (resident "
                + (double.IsNaN(r.Fraction) ? "n/a" : (r.Fraction * 100).ToString("F1") + "%") + ")");
    }

    private static void CollectFully()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private void AddPauses(Dictionary<string, double> m, DateTime startUtc, DateTime endUtc)
    {
        if (_env.Pauses == null) return;
        int expected = (int)(m.GetValueOrDefault("gc_gen0") + m.GetValueOrDefault("gc_gen1") + m.GetValueOrDefault("gc_gen2"));
        List<GcPause> pauses = _env.Pauses.Between(startUtc, endUtc, expected);
        m["gc_pauses"] = pauses.Count;
        if (pauses.Count == 0)
        {
            if (expected == 0) m["gc_pause_max_ms"] = 0;
            return;
        }
        var ms = pauses.Select(p => p.Milliseconds).OrderBy(x => x).ToArray();
        m["gc_pause_max_ms"] = ms[^1];
        m["gc_pause_p95_ms"] = Statistics.PercentileSorted(ms, 0.95);
        m["gc_pause_median_ms"] = Statistics.PercentileSorted(ms, 0.5);
    }

    /// <summary>Rates from the trial's item and byte counts over the measured wall time, unless the scenario set them.</summary>
    public static void AddRates(Dictionary<string, double> m)
    {
        if (!m.TryGetValue("wall_ms", out double wall) || wall <= 0) return;
        double active = m.TryGetValue("active_ms", out double a) && a > 0 ? a : wall;
        if (m.TryGetValue("items", out double items) && !m.ContainsKey("items_per_s")) m["items_per_s"] = items / (active / 1000.0);
        if (m.TryGetValue("bytes_mib", out double mib) && !m.ContainsKey("mib_per_s")) m["mib_per_s"] = mib / (active / 1000.0);
    }

    /// <summary>Counts this mod's threads (named MUR-*) still alive, waiting up to the timeout for them to exit.</summary>
    public static int WaitForModThreads(int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            int alive = CountModThreads();
            if (alive == 0 || clock.ElapsedMilliseconds >= timeoutMs) return alive;
            Thread.Sleep(20);
        }
    }

    public static int CountModThreads()
    {
        if (!OperatingSystem.IsLinux()) return 0;
        int n = 0;
        foreach (int tid in ProcessProbe.ThreadIds())
            if (ProcessProbe.ThreadName(tid)?.StartsWith(ModThreadPrefix, StringComparison.Ordinal) == true) n++;
        return n;
    }

    /// <summary>
    /// Waits until no descriptor opened during the trial is still open. Returns the remaining ones:
    /// files (the code under test kept a handle) and runtime descriptors (pipes, sockets, events,
    /// devices, which the runtime may open lazily and keep), with their targets.
    /// </summary>
    private static (int Files, int Runtime, List<string> Targets) WaitForDescriptors(Dictionary<int, string> baseline, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var added = ProcessProbe.OpenDescriptors().Where(p => !baseline.TryGetValue(p.Key, out string? t) || t != p.Value).Select(p => p.Value).ToList();
            if (added.Count == 0) return (0, 0, new List<string>());
            if (clock.ElapsedMilliseconds >= timeoutMs)
            {
                int files = added.Count(ProcessProbe.IsFileDescriptor);
                var targets = added.OrderBy(t => t, StringComparer.Ordinal).Select(t => PathRedactor.Redact(System.Text.RegularExpressions.Regex.Replace(t, @"\[\d+\]$", ""))!).Distinct().ToList();
                return (files, added.Count - files, targets);
            }
            Thread.Sleep(20);
        }
    }

    public void WriteSummary(bool interrupted, string? stopReason)
    {
        object summary = SummaryBuilder.Build(_runId, _metadata, Catalog(), Results, interrupted, stopReason);
        _writer.WriteJson("summary.json", summary);
    }
}
