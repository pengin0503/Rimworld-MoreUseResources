using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MoreUseResources.Benchmarks.Running;

public enum RunMode { Smoke, Standard, Stress }

public enum CacheState { Unmanaged, Cold, Warm }

/// <summary>
/// Every knob of a benchmark run. A mode preset fills the defaults; explicit options override it.
/// All values that influence results are written to environment.json so that runs can be compared.
/// </summary>
public sealed class BenchOptions
{
    public string Command { get; set; } = "run";
    public RunMode Mode { get; set; } = RunMode.Smoke;
    public List<string> DataDirectories { get; } = new();
    public List<string> PackFiles { get; } = new();
    public string? GenerateDirectory { get; set; }
    public int GenerateMiB { get; set; } = 96;
    public string OutputDirectory { get; set; } = "bench-results";
    public string? Label { get; set; }
    public List<string> Positional { get; } = new();

    public int[] Workers { get; set; } = { 1, 2, 4 };
    public int Trials { get; set; } = 2;
    public int Warmup { get; set; } = 1;
    public List<string>? Scenarios { get; set; }
    public CacheState[] Caches { get; set; } = { CacheState.Warm, CacheState.Cold };
    /// <summary>fadvise (default, no privileges) or drop-caches (root, also drops directory caches).</summary>
    public string ColdMethod { get; set; } = "fadvise";

    public int RamBudgetMiB { get; set; } = 256;
    public int MaxFiles { get; set; } = 1500;
    public long MaxMiB { get; set; } = 256;
    public int TrialTimeoutSeconds { get; set; } = 20;
    /// <summary>
    /// Measuring window of trials limited by the 250 ms cadence (audit/prefetch "cadence", rescans,
    /// the controller): their rate is steady within seconds while a full pass can take many minutes,
    /// so they stop after this window and report the rate and the projected pass time.
    /// </summary>
    public int RateWindowSeconds { get; set; } = 5;
    public double MaxRuntimeMinutes { get; set; } = 15;
    public long MaxRssMiB { get; set; }
    public long MinFreeMemoryMiB { get; set; } = 1024;
    public long MaxDiskMiB { get; set; } = 8192;
    public long MinFreeDiskMiB { get; set; } = 2048;
    public int Seed { get; set; } = 20261118;
    public int SamplerIntervalMs { get; set; } = 100;
    public bool GcPauseEvents { get; set; } = true;
    public bool FingerprintContent { get; set; }
    public int PumpIntervalMs { get; set; } = 250;

    // ---- soak ----
    public double SoakMinutes { get; set; } = 1;
    public double SoakSampleSeconds { get; set; } = 2;
    public double SoakCheckpointMinutes { get; set; } = 0.5;
    public double SoakRecycleMinutes { get; set; } = 0.5;
    public double SoakPhaseSeconds { get; set; } = 10;
    public bool SoakInjectPressure { get; set; }
    public int SoakWorkers { get; set; }
    public int LifecycleCycles { get; set; } = 10;

    public IReadOnlySet<string> Explicit => _explicit;
    private readonly HashSet<string> _explicit = new(StringComparer.Ordinal);

    public static readonly string[] AllScenarioIds =
    {
        "baseline", "audit", "scan", "discovery", "prefetch", "ram-hold", "mapped-pack", "controller",
        "cancel-resume", "memory-pressure", "io-faults", "xml-cache", "lifecycle",
    };

    public void ApplyMode(RunMode mode)
    {
        Mode = mode;
        switch (mode)
        {
            case RunMode.Smoke:
                Workers = new[] { 1, 2, 4 };
                Trials = 2; Warmup = 1;
                MaxFiles = 1500; MaxMiB = 256; RamBudgetMiB = 256;
                TrialTimeoutSeconds = 20; RateWindowSeconds = 5; MaxRuntimeMinutes = 20;
                SoakMinutes = 1; SoakSampleSeconds = 2; SoakCheckpointMinutes = 0.5; SoakRecycleMinutes = 0.5;
                SoakPhaseSeconds = 10; SoakInjectPressure = false; LifecycleCycles = 10;
                break;
            case RunMode.Standard:
                Workers = new[] { 1, 2, 4, 8, 16 };
                Trials = 5; Warmup = 1;
                MaxFiles = 20000; MaxMiB = 4096; RamBudgetMiB = 2048;
                TrialTimeoutSeconds = 120; RateWindowSeconds = 20; MaxRuntimeMinutes = 360;
                SoakMinutes = 30; SoakSampleSeconds = 5; SoakCheckpointMinutes = 5; SoakRecycleMinutes = 10;
                SoakPhaseSeconds = 60; SoakInjectPressure = false; LifecycleCycles = 50;
                FingerprintContent = true;
                break;
            case RunMode.Stress:
                Workers = new[] { 1, 2, 4, 8, 16, 32 };
                Trials = 3; Warmup = 1;
                MaxFiles = 50000; MaxMiB = 16384; RamBudgetMiB = 8192;
                TrialTimeoutSeconds = 600; RateWindowSeconds = 60; MaxRuntimeMinutes = 600;
                SoakMinutes = 240; SoakSampleSeconds = 5; SoakCheckpointMinutes = 10; SoakRecycleMinutes = 30;
                SoakPhaseSeconds = 30; SoakInjectPressure = true; LifecycleCycles = 300;
                FingerprintContent = true;
                break;
        }
    }

    /// <summary>Parses "command [--option value]...". The mode preset is applied before the other options.</summary>
    public static BenchOptions Parse(IReadOnlyList<string> args)
    {
        var o = new BenchOptions();
        var pairs = new List<(string Key, string? Value)>();
        int i = 0;
        if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)) o.Command = args[i++];
        for (; i < args.Count; i++)
        {
            string a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal)) { o.Positional.Add(a); continue; }
            string key = a.Substring(2);
            string? value = null;
            int eq = key.IndexOf('=');
            if (eq >= 0) { value = key.Substring(eq + 1); key = key.Substring(0, eq); }
            else if (!IsFlag(key))
            {
                if (i + 1 >= args.Count) throw new ArgumentException("--" + key + " needs a value");
                value = args[++i];
            }
            pairs.Add((key, value));
        }
        var mode = pairs.LastOrDefault(p => p.Key == "mode");
        o.ApplyMode(mode.Key == null ? RunMode.Smoke : ParseMode(mode.Value!));
        foreach (var (key, value) in pairs)
        {
            o._explicit.Add(key);
            o.Set(key, value);
        }
        if (o.MaxRssMiB <= 0) o.MaxRssMiB = DefaultMaxRssMiB();
        o.Validate();
        return o;
    }

    private static bool IsFlag(string key) => key is "no-gc-events" or "fingerprint-content" or "no-fingerprint-content"
        or "inject-pressure" or "no-inject-pressure" or "help";

    private static RunMode ParseMode(string v) => v.ToLowerInvariant() switch
    {
        "smoke" => RunMode.Smoke,
        "standard" => RunMode.Standard,
        "stress" => RunMode.Stress,
        _ => throw new ArgumentException("unknown mode " + v + " (smoke, standard, stress)"),
    };

    private void Set(string key, string? value)
    {
        string v = value ?? "";
        switch (key)
        {
            case "mode": break;
            case "data": DataDirectories.Add(Path.GetFullPath(v)); break;
            case "pack": PackFiles.Add(Path.GetFullPath(v)); break;
            case "generate": GenerateDirectory = Path.GetFullPath(v); break;
            case "generate-mib": GenerateMiB = PositiveInt(key, v); break;
            case "out": OutputDirectory = Path.GetFullPath(v); break;
            case "label": Label = v; break;
            case "workers": Workers = v.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => PositiveInt(key, x)).Distinct().OrderBy(x => x).ToArray(); break;
            case "trials": Trials = PositiveInt(key, v); break;
            case "warmup": Warmup = NonNegativeInt(key, v); break;
            case "scenarios": Scenarios = v.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList(); break;
            case "cache":
                Caches = v.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim().ToLowerInvariant() switch
                {
                    "cold" => CacheState.Cold,
                    "warm" => CacheState.Warm,
                    _ => throw new ArgumentException("--cache accepts cold,warm"),
                }).Distinct().ToArray();
                break;
            case "cold-method":
                if (v is not ("fadvise" or "drop-caches")) throw new ArgumentException("--cold-method accepts fadvise or drop-caches");
                ColdMethod = v;
                break;
            case "ram-budget-mib": RamBudgetMiB = PositiveInt(key, v); break;
            case "max-files": MaxFiles = PositiveInt(key, v); break;
            case "max-mib": MaxMiB = PositiveInt(key, v); break;
            case "trial-timeout-s": TrialTimeoutSeconds = PositiveInt(key, v); break;
            case "rate-window-s": RateWindowSeconds = PositiveInt(key, v); break;
            case "max-runtime-min": MaxRuntimeMinutes = PositiveDouble(key, v); break;
            case "max-rss-mib": MaxRssMiB = PositiveInt(key, v); break;
            case "min-free-mib": MinFreeMemoryMiB = NonNegativeInt(key, v); break;
            case "max-disk-mib": MaxDiskMiB = PositiveInt(key, v); break;
            case "min-free-disk-mib": MinFreeDiskMiB = NonNegativeInt(key, v); break;
            case "seed": Seed = int.Parse(v, CultureInfo.InvariantCulture); break;
            case "sampler-ms": SamplerIntervalMs = PositiveInt(key, v); break;
            case "no-gc-events": GcPauseEvents = false; break;
            case "fingerprint-content": FingerprintContent = true; break;
            case "no-fingerprint-content": FingerprintContent = false; break;
            case "pump-ms": PumpIntervalMs = PositiveInt(key, v); break;
            case "duration-min": SoakMinutes = PositiveDouble(key, v); break;
            case "sample-s": SoakSampleSeconds = PositiveDouble(key, v); break;
            case "checkpoint-min": SoakCheckpointMinutes = PositiveDouble(key, v); break;
            case "recycle-min": SoakRecycleMinutes = PositiveDouble(key, v); break;
            case "phase-s": SoakPhaseSeconds = PositiveDouble(key, v); break;
            case "inject-pressure": SoakInjectPressure = true; break;
            case "no-inject-pressure": SoakInjectPressure = false; break;
            case "soak-workers": SoakWorkers = NonNegativeInt(key, v); break;
            case "lifecycle-cycles": LifecycleCycles = PositiveInt(key, v); break;
            case "help": Command = "help"; break;
            default: throw new ArgumentException("unknown option --" + key);
        }
    }

    public void Validate()
    {
        if (Workers.Length == 0 || Workers.Any(w => w > 256)) throw new ArgumentException("--workers must list 1..256");
        if (Caches.Length == 0) throw new ArgumentException("--cache needs at least one state");
        if (Scenarios != null)
            foreach (string s in Scenarios)
                if (Array.IndexOf(AllScenarioIds, s) < 0) throw new ArgumentException("unknown scenario " + s + " (" + string.Join(",", AllScenarioIds) + ")");
        if (RamBudgetMiB + 512 > MaxRssMiB)
            throw new ArgumentException("--ram-budget-mib (" + RamBudgetMiB + ") must stay at least 512 MiB below --max-rss-mib (" + MaxRssMiB + ")");
        // A soak longer than the mode's run-time limit would be cut by the safety guard.
        if (Command == "soak" && !Explicit.Contains("max-runtime-min")) MaxRuntimeMinutes = Math.Max(MaxRuntimeMinutes, SoakMinutes + 15);
        if (Command == "soak" && MaxRuntimeMinutes < SoakMinutes)
            throw new ArgumentException("--max-runtime-min (" + MaxRuntimeMinutes + ") is shorter than --duration-min (" + SoakMinutes + ")");
    }

    /// <summary>Three quarters of physical memory, or 4 GiB when it cannot be read.</summary>
    public static long DefaultMaxRssMiB()
    {
        long total = 0;
        if (Measurement.ProcFs.TryRead("/proc/meminfo") is { } meminfo
            && Measurement.ProcFs.ParseStatus(meminfo).TryGetValue("MemTotal", out long memTotal))
            total = memTotal;
        if (total <= 0)
        {
            try { total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; }
            catch (InvalidOperationException) { total = 0; }
        }
        return total > 0 ? Math.Max(1024, total / (1024 * 1024) * 3 / 4) : 4096;
    }

    private static int PositiveInt(string key, string v)
    {
        if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n <= 0)
            throw new ArgumentException("--" + key + " must be a positive integer");
        return n;
    }

    private static int NonNegativeInt(string key, string v)
    {
        if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 0)
            throw new ArgumentException("--" + key + " must be zero or a positive integer");
        return n;
    }

    private static double PositiveDouble(string key, string v)
    {
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || n <= 0)
            throw new ArgumentException("--" + key + " must be a positive number");
        return n;
    }

    public Dictionary<string, object?> Describe() => new()
    {
        ["command"] = Command,
        ["mode"] = Mode.ToString().ToLowerInvariant(),
        ["label"] = Label,
        ["workers"] = Workers,
        ["trials"] = Trials,
        ["warmup"] = Warmup,
        ["scenarios"] = Scenarios,
        ["caches"] = Caches.Select(c => c.ToString().ToLowerInvariant()).ToArray(),
        ["cold_method"] = ColdMethod,
        ["ram_budget_mib"] = RamBudgetMiB,
        ["max_files"] = MaxFiles,
        ["max_mib"] = MaxMiB,
        ["trial_timeout_s"] = TrialTimeoutSeconds,
        ["rate_window_s"] = RateWindowSeconds,
        ["max_runtime_min"] = MaxRuntimeMinutes,
        ["max_rss_mib"] = MaxRssMiB,
        ["min_free_mib"] = MinFreeMemoryMiB,
        ["max_disk_mib"] = MaxDiskMiB,
        ["min_free_disk_mib"] = MinFreeDiskMiB,
        ["seed"] = Seed,
        ["sampler_ms"] = SamplerIntervalMs,
        ["gc_pause_events"] = GcPauseEvents,
        ["pump_ms"] = PumpIntervalMs,
        ["generate_mib"] = GenerateDirectory == null ? null : GenerateMiB,
        ["soak_min"] = SoakMinutes,
        ["soak_sample_s"] = SoakSampleSeconds,
        ["soak_checkpoint_min"] = SoakCheckpointMinutes,
        ["soak_recycle_min"] = SoakRecycleMinutes,
        ["soak_phase_s"] = SoakPhaseSeconds,
        ["soak_inject_pressure"] = SoakInjectPressure,
        ["soak_workers"] = SoakWorkers,
        ["lifecycle_cycles"] = LifecycleCycles,
    };
}
