using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using MoreUseResources.Benchmarks.Analysis;
using MoreUseResources.Benchmarks.Data;
using MoreUseResources.Benchmarks.Measurement;
using MoreUseResources.Benchmarks.Running;

namespace MoreUseResources.Benchmarks;

public static class Program
{
    public const int ExitOk = 0;
    public const int ExitError = 1;
    public const int ExitUsage = 2;
    public const int ExitInterrupted = 3;
    public const int ExitSafetyStop = 4;
    public const int ExitTrialsFailed = 5;

    public static int Main(string[] args)
    {
        BenchOptions options;
        try { options = BenchOptions.Parse(args); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            Console.Error.WriteLine("run with --help for usage");
            return ExitUsage;
        }
        try
        {
            switch (options.Command)
            {
                case "help": Console.WriteLine(Usage); return ExitOk;
                case "run":
                case "soak": return Run(options);
                case "analyze": return Analyze(options);
                case "compare": return Compare(options);
                case "generate-dataset": return Generate(options);
                case "env":
                    Console.WriteLine(JsonSerializer.Serialize(RunMetadata.Collect(options, null, "env"), ResultWriter.Json));
                    return ExitOk;
                default:
                    Console.Error.WriteLine("unknown command " + options.Command);
                    Console.Error.WriteLine(Usage);
                    return ExitUsage;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine("error: " + ex.GetType().Name + ": " + ex.Message);
            return ExitError;
        }
    }

    private static int Analyze(BenchOptions o)
    {
        if (o.Positional.Count == 0) throw new ArgumentException("analyze needs a result directory");
        foreach (string dir in o.Positional)
        {
            Analyzer.Write(dir);
            Console.WriteLine(Path.Combine(dir, "analysis.md"));
        }
        return ExitOk;
    }

    private static int Compare(BenchOptions o)
    {
        if (o.Positional.Count != 2) throw new ArgumentException("compare needs two result directories: A (baseline) and B");
        (string json, string md, bool comparable) = Comparer.Compare(o.Positional[0], o.Positional[1]);
        string outDir = o.Explicit.Contains("out") ? o.OutputDirectory : o.Positional[1];
        Directory.CreateDirectory(outDir);
        ResultWriter.WriteTextAtomic(Path.Combine(outDir, "comparison.json"), json);
        ResultWriter.WriteTextAtomic(Path.Combine(outDir, "comparison.md"), md);
        Console.WriteLine(Path.Combine(outDir, "comparison.md"));
        return comparable ? ExitOk : ExitError;
    }

    private static int Generate(BenchOptions o)
    {
        string dir = o.Positional.FirstOrDefault() ?? o.GenerateDirectory ?? throw new ArgumentException("generate-dataset needs a directory");
        GeneratedTruth truth = DatasetGenerator.EnsureGenerated(Path.GetFullPath(dir), o.GenerateMiB, o.Seed);
        Console.WriteLine(JsonSerializer.Serialize(truth, ResultWriter.Json));
        return ExitOk;
    }

    private static int Run(BenchOptions o)
    {
        bool soak = o.Command == "soak";
        Dictionary<string, object?> code = RunMetadata.Code();
        string sha = code.GetValueOrDefault("commit") as string ?? "unknown";
        string runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + (sha.Length >= 7 ? sha[..7] : sha) + "-"
            + o.Mode.ToString().ToLowerInvariant() + (soak ? "-soak" : "") + (string.IsNullOrWhiteSpace(o.Label) ? "" : "-" + Safe(o.Label!));
        string outDir = Path.Combine(o.OutputDirectory, runId);
        string scratch = Path.Combine(outDir, "scratch");
        Directory.CreateDirectory(scratch);
        using var log = new StreamWriter(new FileStream(Path.Combine(outDir, "run.log"), FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        object logGate = new();
        void Log(string line)
        {
            line = PathRedactor.Redact(line) ?? "";
            lock (logGate)
            {
                Console.WriteLine(line);
                log.WriteLine(line);
            }
        }

        using var cts = new CancellationTokenSource();
        string? signal = null;
        void OnSignal(PosixSignalContext c)
        {
            if (cts.IsCancellationRequested) return; // a second signal terminates as usual
            c.Cancel = true;
            signal = c.Signal.ToString();
            Log("received " + signal + ": finishing the current trial and writing results (send again to abort)");
            cts.Cancel();
        }
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);

        // Input data.
        var roots = new List<string>(o.DataDirectories);
        GeneratedTruth? truth = null;
        string kind = "real";
        string? generator = null;
        string? gen = roots.Count == 0 || o.GenerateDirectory != null
            ? Path.GetFullPath(o.GenerateDirectory ?? Path.Combine(o.OutputDirectory, "generated-data")) : null;
        // Before the first log line that names a data path.
        PathRedactor.Configure(gen == null ? roots : roots.Append(gen).ToList(), o.OutputDirectory, scratch);
        if (gen != null)
        {
            Log("preparing generated data in " + Path.GetFullPath(gen) + " (" + o.GenerateMiB + " MiB target)");
            truth = DatasetGenerator.EnsureGenerated(gen, o.GenerateMiB, o.Seed);
            roots.Add(gen);
            kind = o.DataDirectories.Count == 0 ? "generated" : "mixed";
            generator = DatasetGenerator.Describe(o.GenerateMiB, o.Seed);
        }
        else if (roots.Count == 1 && DatasetGenerator.ReadMarker(roots[0]) is { } existing)
        {
            // --data pointing at a directory written by generate-dataset: the planted truth still applies.
            truth = existing.Truth;
            kind = "generated";
            generator = existing.Description;
        }
        PathRedactor.Configure(roots, o.OutputDirectory, scratch);
        foreach (string r in roots)
            if (!Directory.Exists(r)) throw new ArgumentException("data directory not found: " + r);
        var load = Stopwatch.StartNew();
        Dataset dataset = Dataset.Load(roots, o.PackFiles, o.MaxFiles, o.MaxMiB * 1048576L, o.FingerprintContent, kind, generator, cts.Token);
        Log($"dataset: {dataset.Mods.Count} roots, {dataset.LoadFolders.Count} load folders, {dataset.Files.Count}/{dataset.DiscoveredFiles.Count} files "
            + $"({dataset.FileBytes / 1048576.0:F1} MiB), {dataset.Packs.Count} packs ({dataset.PackBytes / 1048576.0:F1} MiB), loaded in {load.Elapsed.TotalSeconds:F1} s");

        Dictionary<string, object?> metadata = RunMetadata.Collect(o, dataset, runId);
        if (truth != null) metadata["generated_truth"] = truth;

        GcPauseListener? pauses = null;
        if (o.GcPauseEvents)
        {
            pauses = new GcPauseListener();
            GC.Collect();
            var wait = Stopwatch.StartNew();
            while (pauses.EventsSeen == 0 && wait.ElapsedMilliseconds < 2000) Thread.Sleep(20);
            if (pauses.EventsSeen == 0)
            {
                Log("runtime GC events not received; per-pause maximum and P95 are not recorded");
                pauses.Dispose();
                pauses = null;
            }
        }
        metadata["gc_pause_events"] = pauses != null;
        ResultWriter.WriteJsonAtomic(Path.Combine(outDir, "metadata.json"), metadata);

        var env = new RunEnvironment
        {
            Options = o, Dataset = dataset, ScratchRoot = scratch, LogicalProcessors = Environment.ProcessorCount, Pauses = pauses, Log = Log,
        };
        using var guard = new SafetyGuard(o, outDir, scratch, cts);
        guard.Start();
        bool completed;
        int failed = 0;
        try
        {
            if (soak)
            {
                Log($"soak: {o.SoakMinutes} min, samples every {o.SoakSampleSeconds} s, checkpoints every {o.SoakCheckpointMinutes} min, results in {outDir}");
                completed = new SoakRunner(env, outDir, metadata).Run(cts.Token, () => guard.StopReason ?? signal);
            }
            else
            {
                IReadOnlyList<Scenario> scenarios = BenchmarkRunner.Select(o);
                Log($"run {runId}: {scenarios.Count} scenarios, trials {o.Trials} + warm-up {o.Warmup}, workers {string.Join(",", o.Workers)}, results in {outDir}");
                using var writer = new ResultWriter(outDir);
                var runner = new BenchmarkRunner(env, writer, runId, metadata);
                completed = runner.Run(scenarios, cts.Token, () => guard.StopReason ?? signal);
                failed = runner.Results.Count(r => r.Status == TrialStatus.Failed);
            }
        }
        finally
        {
            guard.Dispose();
            pauses?.Dispose();
            try { Directory.Delete(scratch, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("could not delete scratch: " + ex.Message); }
        }
        try
        {
            Analyzer.Write(outDir);
            Log("analysis: " + Path.Combine(outDir, "analysis.md"));
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            Log("analysis failed: " + ex.Message);
        }
        if (guard.StopReason != null)
        {
            Log("stopped by safety limit: " + guard.StopReason);
            return ExitSafetyStop;
        }
        if (!completed)
        {
            Log("interrupted" + (signal != null ? " by " + signal : "") + "; results so far are kept in " + outDir);
            return ExitInterrupted;
        }
        if (failed > 0)
        {
            Log(failed + " trials failed; see trials.csv (status=failed, error column)");
            return ExitTrialsFailed;
        }
        Log("done: " + outDir);
        return ExitOk;
    }

    private static string Safe(string s) => new(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());

    public const string Usage = """
        MoreUseResources benchmark (Linux, no game). Drives the mod's own Core code.

        usage:
          MoreUseResources.Benchmarks run   [--mode smoke|standard|stress] [options]
          MoreUseResources.Benchmarks soak  [--mode ...] [--duration-min N] [options]
          MoreUseResources.Benchmarks analyze <result-dir>...
          MoreUseResources.Benchmarks compare <result-dir-A> <result-dir-B> [--out DIR]
          MoreUseResources.Benchmarks generate-dataset <dir> [--generate-mib N] [--seed N]
          MoreUseResources.Benchmarks env

        input:
          --data DIR            data root (repeatable): a RimWorld Data folder, a Mods folder or one mod;
                                mod roots are found by About/About.xml, Assets.zip next to load folders
          --pack FILE           extra pack file such as Game/Assets.zip (repeatable)
          --generate DIR        use (or create) a generated data set; default when --data is missing
          --generate-mib N      size of generated data (default 96)
          --max-files N         files used by component scenarios (subset chosen by path hash)
          --max-mib N           bytes used by component scenarios
          --fingerprint-content hash file contents for the dataset fingerprint (slow on large data)

        trials:
          --scenarios a,b       baseline,audit,scan,discovery,prefetch,ram-hold,mapped-pack,controller,
                                cancel-resume,memory-pressure,io-faults,xml-cache,lifecycle
          --workers 1,2,4,8,16  worker counts (more than nproc is emulated and marked oversubscribed)
          --trials N            measured trials per condition (median, P95, CV reported)
          --warmup N            unmeasured warm-up rounds before the trials
          --cache cold,warm     page-cache states; cold uses posix_fadvise unless --cold-method drop-caches (root)
          --ram-budget-mib N    native copy budget (RamFileCacheMiB) and pack residency limit
          --cold-method M       fadvise (default, per file) or drop-caches (root, whole page cache and metadata)
          --pump-ms N           controller cadence emulated by "cadence" variants (default 250)
          --rate-window-s N     cadence-bound trials stop after N s and report rates and the projected pass time
          --trial-timeout-s N   per-trial limit; cut trials keep rates, not completion times
          --lifecycle-cycles N  create/stop cycles per lifecycle trial
          --seed N              order of conditions per round and random reads

        soak:
          --duration-min N  --sample-s N  --checkpoint-min N  --recycle-min N  --phase-s N
          --inject-pressure / --no-inject-pressure   --soak-workers N (0 = auto)

        safety (a crossed limit stops the run and keeps the results):
          --max-runtime-min N  --max-rss-mib N  --min-free-mib N  --max-disk-mib N  --min-free-disk-mib N

        output:
          --out DIR (default bench-results)  --label TEXT  --sampler-ms N  --no-gc-events
          Each run writes <out>/<utc>-<commit>-<mode>[-label]/: metadata.json, trials.csv, metrics.csv,
          trials.jsonl, summary.json, analysis.md, analysis.json, run.log (soak: soak-*.csv/json).

        exit codes: 0 ok, 1 error, 2 bad arguments, 3 interrupted (results kept), 4 safety stop, 5 trials failed
        """;
}
