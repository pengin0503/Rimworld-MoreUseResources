using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using MoreUseResources.Benchmarks.Data;
using MoreUseResources.Benchmarks.Measurement;

namespace MoreUseResources.Benchmarks.Running;

/// <summary>One combination of parameters of a scenario, for example workers=4;cache=cold;variant=cadence.</summary>
public sealed class Condition
{
    public Condition(string scenario, IEnumerable<KeyValuePair<string, string>> parameters)
    {
        Scenario = scenario;
        Parameters = new SortedDictionary<string, string>(parameters.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal);
    }

    public string Scenario { get; }
    public IReadOnlyDictionary<string, string> Parameters { get; }
    public string Key => string.Join(";", Parameters.Select(p => p.Key + "=" + p.Value));

    public string? Get(string name) => Parameters.TryGetValue(name, out string? v) ? v : null;
    public int Workers => int.TryParse(Get("workers"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) ? w : 0;

    public CacheState Cache => Get("cache") switch
    {
        "cold" => CacheState.Cold,
        "warm" => CacheState.Warm,
        _ => CacheState.Unmanaged,
    };

    public override string ToString() => Scenario + "[" + Key + "]";

    public static Condition Of(string scenario, params (string Key, object Value)[] p) =>
        new(scenario, p.Select(x => new KeyValuePair<string, string>(x.Key, Convert.ToString(x.Value, CultureInfo.InvariantCulture) ?? "")));
}

/// <summary>Inputs shared by all trials of a run.</summary>
public sealed class RunEnvironment
{
    public required BenchOptions Options { get; init; }
    public required Dataset Dataset { get; init; }
    public required string ScratchRoot { get; init; }
    public required int LogicalProcessors { get; init; }
    public GcPauseListener? Pauses { get; init; }
    public Action<string> Log { get; init; } = _ => { };
}

/// <summary>
/// A family of trials that exercises one part of the mod. Each scenario names the production
/// code it drives and what Linux cannot reproduce, which the report prints next to the numbers.
/// </summary>
public abstract class Scenario
{
    public abstract string Id { get; }
    /// <summary>Short Japanese title for reports.</summary>
    public abstract string Title { get; }
    /// <summary>The mod code that runs in this scenario.</summary>
    public abstract string ProductionCode { get; }
    /// <summary>What this scenario cannot show on a Linux VM without the game.</summary>
    public virtual string NotReproduced => "";
    public virtual bool RequiresPacks => false;
    public abstract IReadOnlyList<Condition> Conditions(RunEnvironment env);
    public abstract Trial CreateTrial(Condition condition, RunEnvironment env);

    protected static IEnumerable<CacheState> Caches(RunEnvironment env) => env.Options.Caches;
    protected static string CacheName(CacheState c) => c == CacheState.Cold ? "cold" : "warm";
}

/// <summary>
/// One measured execution. <see cref="Prepare"/> and <see cref="IDisposable.Dispose"/> are outside the
/// measured window; the runner controls the page cache of <see cref="CacheTargets"/> in between.
/// </summary>
public abstract class Trial : IDisposable
{
    public virtual IReadOnlyList<string> CacheTargets => Array.Empty<string>();
    public virtual IReadOnlyList<Gauge> Gauges => Array.Empty<Gauge>();
    public virtual void Prepare(TrialContext context) { }
    public abstract void Execute(TrialContext context);
    /// <summary>Runs after the closing snapshot: checks whose cost must not count, such as mincore over the inputs.</summary>
    public virtual void AfterMeasure(TrialContext context) { }
    public virtual void Dispose() { }
}

/// <summary>State of one trial: metrics, notes, timeouts and cancellation.</summary>
public sealed class TrialContext
{
    private readonly Stopwatch _clock = new();

    public TrialContext(Condition condition, RunEnvironment env, string scratch, CancellationToken token)
    {
        Condition = condition;
        Env = env;
        ScratchDirectory = scratch;
        Token = token;
        Timeout = TimeSpan.FromSeconds(env.Options.TrialTimeoutSeconds);
    }

    public Condition Condition { get; }
    public RunEnvironment Env { get; }
    public BenchOptions Options => Env.Options;
    public Dataset Dataset => Env.Dataset;
    public string ScratchDirectory { get; }
    public CancellationToken Token { get; }
    public TimeSpan Timeout { get; set; }
    public Dictionary<string, double> Metrics { get; } = new(StringComparer.Ordinal);
    public List<string> Notes { get; } = new();
    /// <summary>Measured but cut by the trial timeout: rates are valid, completion times are not.</summary>
    public bool Truncated { get; set; }
    public string? InvalidReason { get; private set; }
    public bool Valid => InvalidReason == null;
    public TimeSpan Elapsed => _clock.Elapsed;

    internal void StartClock() => _clock.Restart();

    public void Metric(string name, double value)
    {
        if (double.IsFinite(value)) Metrics[name] = value;
    }

    public void Invalidate(string reason) => InvalidReason ??= reason;

    public void Note(string note)
    {
        if (!Notes.Contains(note)) Notes.Add(note);
    }

    /// <summary>
    /// Polls until <paramref name="done"/> is true. Returns false on trial timeout (and marks the
    /// trial truncated). Cancellation of the run throws.
    /// </summary>
    public bool WaitUntil(Func<bool> done, int pollMs, Action? eachPoll = null, TimeSpan? limit = null)
    {
        var start = Stopwatch.StartNew();
        TimeSpan max = limit ?? (Timeout - _clock.Elapsed);
        while (true)
        {
            Token.ThrowIfCancellationRequested();
            eachPoll?.Invoke();
            if (done()) return true;
            if (start.Elapsed >= max)
            {
                Truncated = true;
                return false;
            }
            Thread.Sleep(pollMs);
        }
    }

    /// <summary>Measures one phase of a trial with full process counters, stored as "phase.metric".</summary>
    public void Measure(string phase, Action action)
    {
        ResourceSnapshot before = ProcessProbe.Capture();
        action();
        ResourceSnapshot after = ProcessProbe.Capture();
        var m = new Dictionary<string, double>();
        ResourceDelta.Add(m, before, after, Env.LogicalProcessors);
        foreach (var pair in m)
            if (pair.Key is "wall_ms" or "cpu_ms" or "cpu_util_pct" or "cpu_parallelism" or "io_storage_read_mib" or "io_read_mib"
                or "rss_anon_delta_mib" or "rss_file_delta_mib" or "alloc_mib" or "gc_pause_ms" or "gc_gen2" or "lock_contentions"
                or "run_queue_wait_ms" or "psi_io_some_ms" or "io_wait_ms")
                Metrics[phase + "." + pair.Key] = pair.Value;
    }
}
