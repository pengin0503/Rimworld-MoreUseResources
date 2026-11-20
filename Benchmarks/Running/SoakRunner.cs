using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using MoreUseResources.Benchmarks.Analysis;
using MoreUseResources.Benchmarks.Measurement;
using MoreUseResources.Benchmarks.Scenarios;
using MoreUseResources.Core;

namespace MoreUseResources.Benchmarks.Running;

/// <summary>
/// Hours of the real controller cycling through the game phases, user actions (manual pause,
/// "release memory", RamHoldCopies toggled), a simulated save, optional memory shortages and
/// periodic controller re-creation. Rescan and refresh intervals are shortened to one minute (the
/// mod's minimum) so many passes happen. Samples go to CSV as they are taken; checkpoints after a
/// full collection feed the leak verdicts.
/// </summary>
public sealed class SoakRunner
{
    private static readonly string[] Phases =
    {
        "main-menu", "loading", "playing", "playing-fast", "game-paused", "saving", "manual-pause", "release-memory", "toggle-hold", "pressure",
    };

    private readonly RunEnvironment _env;
    private readonly string _dir;
    private readonly Dictionary<string, object?> _metadata;
    private readonly List<(double Hours, Dictionary<string, double> Values)> _checkpoints = new();
    private readonly List<string> _events = new();
    private StreamWriter? _samples;
    private StreamWriter? _checkpointCsv;
    private StreamWriter? _eventCsv;
    private volatile bool _saving;

    public SoakRunner(RunEnvironment env, string directory, Dictionary<string, object?> metadata)
    {
        _env = env;
        _dir = directory;
        _metadata = metadata;
    }

    public static readonly string[] SampleColumns =
    {
        "t_s", "phase", "rss_mib", "rss_anon_mib", "rss_file_mib", "managed_heap_mib", "gc_committed_mib", "gc_gen0", "gc_gen1", "gc_gen2",
        "gc_pause_total_ms", "threads", "mod_threads", "open_files", "cpu_ms", "io_read_mib", "io_storage_read_mib", "native_held_mib",
        "resident_pack_mib", "files_read", "audit_completed", "audit_passes", "refresh_passes", "discovery_passes", "pending_jobs",
        "workers_active", "jobs_failed", "file_errors", "audit_errors", "pressure_events", "control_error", "mem_available_mib",
    };

    /// <summary>Runs until the duration ends or the token is cancelled. Returns true when the full duration ran.</summary>
    public bool Run(CancellationToken token, Func<string?> stopReason)
    {
        BenchOptions o = _env.Options;
        Directory.CreateDirectory(_dir);
        _samples = Open("soak-samples.csv", string.Join(",", SampleColumns));
        _checkpointCsv = Open("soak-checkpoints.csv", "t_s,managed_heap_mib,rss_anon_mib,native_held_mib,rss_anon_minus_native_mib,gc_committed_mib,threads,mod_threads,open_files");
        _eventCsv = Open("soak-events.csv", "t_s,event,detail");
        var notes = new List<string>();
        ResourceSettings settings = ControllerHarness.Settings(o, "default", o.SoakWorkers, notes);
        settings.RamPrefetchRefreshMinutes = 1;
        settings.CpuAuditRescanMinutes = 1;
        settings.AssetDiscoveryRescanMinutes = 1;
        bool packs = _env.Dataset.Packs.Any(p => p.EndsWith(EnginePackFiles.AssetPackFileName, StringComparison.OrdinalIgnoreCase));
        settings.RamResidentPacks = packs;
        settings.Clamp();
        foreach (string n in notes) Event(0, "note", n);
        var probe = new ScriptedMemoryProbe(new SystemMemoryProbe());
        var clock = Stopwatch.StartNew();
        double durationS = o.SoakMinutes * 60;
        double phaseS = o.SoakPhaseSeconds;
        double sampleS = o.SoakSampleSeconds;
        double checkpointS = o.SoakCheckpointMinutes * 60;
        double recycleS = o.SoakRecycleMinutes * 60;
        int lp = o.SoakWorkers > 0 ? ControllerHarness.LogicalProcessors(o.SoakWorkers) : Environment.ProcessorCount;
        ResourceController controller = ControllerHarness.Create(_env.Dataset, settings, lp, probe, savingProbe: () => _saving);
        controller.Start();
        Event(0, "start", "workers=" + (o.SoakWorkers > 0 ? o.SoakWorkers.ToString(CultureInfo.InvariantCulture) : "auto") + " lp=" + lp
            + " packs=" + packs + " pressure=" + o.SoakInjectPressure);
        int phaseIndex = -1;
        double nextSample = 0, nextCheckpoint = checkpointS, nextRecycle = recycleS, phaseEnd = 0;
        bool holdOn = settings.RamHoldCopies;
        int recycles = 0, leakedAtRecycle = 0, controlErrors = 0, samples = 0;
        long progressLast = -1;
        double lastProgressAt = 0, maxGapActive = 0;
        bool interrupted = false;
        string phase = "";
        var progressByThird = new long[3];
        try
        {
            while (clock.Elapsed.TotalSeconds < durationS)
            {
                if (token.IsCancellationRequested) { interrupted = true; break; }
                double t = clock.Elapsed.TotalSeconds;
                if (t >= phaseEnd)
                {
                    phaseIndex++;
                    phase = Phases[phaseIndex % Phases.Length];
                    ApplyPhase(controller, probe, settings, phase, ref holdOn);
                    phaseEnd = t + phaseS;
                    Event(t, "phase", phase);
                }
                if (t >= nextRecycle)
                {
                    controller.Shutdown();
                    int leaked = BenchmarkRunner.WaitForModThreads(3000);
                    leakedAtRecycle = Math.Max(leakedAtRecycle, leaked);
                    recycles++;
                    Event(t, "recycle", "leaked_mod_threads=" + leaked);
                    probe.Clear();
                    _saving = false;
                    controller = ControllerHarness.Create(_env.Dataset, settings, lp, probe, savingProbe: () => _saving);
                    controller.Start();
                    ApplyPhase(controller, probe, settings, phase, ref holdOn);
                    nextRecycle = t + recycleS;
                }
                if (t >= nextSample)
                {
                    ControllerStatus s = controller.GetStatus();
                    if (s.ControlError != null) { controlErrors++; Event(t, "control-error", s.ControlError); }
                    WriteSample(t, phase, s);
                    samples++;
                    long progress = s.FilesRead + s.AuditCompleted + s.FileRefreshPasses + s.AuditPassesCompleted;
                    bool active = phase is "main-menu" or "loading" or "playing" or "playing-fast" or "game-paused" or "toggle-hold";
                    if (progress != progressLast)
                    {
                        progressLast = progress;
                        lastProgressAt = t;
                    }
                    else if (active) maxGapActive = Math.Max(maxGapActive, t - lastProgressAt);
                    progressByThird[Math.Min(2, (int)(3 * t / durationS))] = progress;
                    nextSample = t + sampleS;
                }
                if (t >= nextCheckpoint)
                {
                    Checkpoint(t, controller);
                    nextCheckpoint = t + checkpointS;
                }
                Thread.Sleep(Math.Clamp((int)((Math.Min(nextSample, Math.Min(phaseEnd, nextCheckpoint)) - clock.Elapsed.TotalSeconds) * 1000), 10, 200));
            }
        }
        finally
        {
            double t = clock.Elapsed.TotalSeconds;
            controller.Shutdown();
            probe.Clear();
            int leaked = BenchmarkRunner.WaitForModThreads(3000);
            Event(t, "shutdown", "leaked_mod_threads=" + leaked);
            Checkpoint(t, null);
            var summary = Summarize(t, durationS, interrupted || token.IsCancellationRequested, stopReason(), recycles, Math.Max(leakedAtRecycle, leaked),
                controlErrors, samples, maxGapActive, progressByThird);
            ResultWriter.WriteJsonAtomic(Path.Combine(_dir, "soak-summary.json"), summary);
            _samples?.Dispose();
            _checkpointCsv?.Dispose();
            _eventCsv?.Dispose();
        }
        return !interrupted && !token.IsCancellationRequested;
    }

    private void ApplyPhase(ResourceController c, ScriptedMemoryProbe probe, ResourceSettings settings, string phase, ref bool holdOn)
    {
        c.Manual = ManualMode.Auto;
        _saving = false;
        probe.Clear();
        switch (phase)
        {
            case "main-menu": c.PublishSignals(ControllerHarness.MainMenu); break;
            case "loading": c.PublishSignals(ControllerHarness.Loading); break;
            case "playing": c.PublishSignals(ControllerHarness.Playing); break;
            case "playing-fast": c.PublishSignals(ControllerHarness.PlayingFast); break;
            case "game-paused": c.PublishSignals(ControllerHarness.PausedGame); break;
            case "saving":
                _saving = true;
                c.PublishSignals(ControllerHarness.Playing);
                break;
            case "manual-pause":
                c.PublishSignals(ControllerHarness.Playing);
                c.Manual = ManualMode.Paused;
                break;
            case "release-memory":
                c.PublishSignals(ControllerHarness.Playing);
                c.RequestReleaseMemory();
                break;
            case "toggle-hold":
                holdOn = !holdOn;
                ResourceSettings copy = settings.Clone();
                copy.RamHoldCopies = holdOn;
                c.UpdateSettings(copy);
                c.PublishSignals(ControllerHarness.MainMenu);
                break;
            case "pressure":
                c.PublishSignals(ControllerHarness.MainMenu);
                if (_env.Options.SoakInjectPressure)
                    probe.ForceAvailable(Math.Max(0, settings.RamMinFreeMiB * ResourceSettings.MiB - (512L << 20)));
                break;
        }
    }

    private void WriteSample(double t, string phase, ControllerStatus s)
    {
        ResourceSnapshot snap = ProcessProbe.Capture(includeThreads: false);
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        var v = new List<string>
        {
            F(t), phase, F(snap.RssBytes / 1048576.0), F(snap.RssAnonBytes / 1048576.0), F(snap.RssFileBytes / 1048576.0),
            F(GC.GetTotalMemory(false) / 1048576.0), F(info.TotalCommittedBytes / 1048576.0),
            GC.CollectionCount(0).ToString(CultureInfo.InvariantCulture), GC.CollectionCount(1).ToString(CultureInfo.InvariantCulture),
            GC.CollectionCount(2).ToString(CultureInfo.InvariantCulture), F(GC.GetTotalPauseDuration().TotalMilliseconds),
            snap.Threads.ToString(CultureInfo.InvariantCulture), BenchmarkRunner.CountModThreads().ToString(CultureInfo.InvariantCulture),
            (snap.OpenFiles ?? -1).ToString(CultureInfo.InvariantCulture), F(snap.CpuTotalMs),
            F(snap.Io?.ReadChars / 1048576.0), F(snap.Io?.StorageReadBytes / 1048576.0), F(s.CacheHeldBytes / 1048576.0),
            F(s.ResidentPackBytes / 1048576.0), s.FilesRead.ToString(CultureInfo.InvariantCulture), s.AuditCompleted.ToString(CultureInfo.InvariantCulture),
            s.AuditPassesCompleted.ToString(CultureInfo.InvariantCulture), s.FileRefreshPasses.ToString(CultureInfo.InvariantCulture),
            s.FileDiscoveryPasses.ToString(CultureInfo.InvariantCulture), s.PendingJobs.ToString(CultureInfo.InvariantCulture),
            s.WorkersActive.ToString(CultureInfo.InvariantCulture), s.JobsFailed.ToString(CultureInfo.InvariantCulture),
            s.FileErrors.ToString(CultureInfo.InvariantCulture), s.AuditErrors.ToString(CultureInfo.InvariantCulture),
            s.PressureEvents.ToString(CultureInfo.InvariantCulture), s.ControlError == null ? "0" : "1", F(snap.MemAvailableBytes / 1048576.0),
        };
        _samples!.WriteLine(string.Join(",", v.Select(ResultWriter.Csv)));
        _samples.Flush();
    }

    private void Checkpoint(double t, ResourceController? controller)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        ResourceSnapshot snap = ProcessProbe.Capture(includeThreads: false);
        double native = controller == null ? 0 : (controller.GetStatus().CacheHeldBytes) / 1048576.0;
        double anon = (snap.RssAnonBytes ?? 0) / 1048576.0;
        var values = new Dictionary<string, double>
        {
            ["managed_heap_mib"] = GC.GetTotalMemory(false) / 1048576.0,
            ["rss_anon_mib"] = anon,
            ["native_held_mib"] = native,
            ["rss_anon_minus_native_mib"] = anon - native,
            ["gc_committed_mib"] = GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576.0,
            ["threads"] = snap.Threads,
            ["mod_threads"] = BenchmarkRunner.CountModThreads(),
            ["open_files"] = snap.OpenFiles ?? double.NaN,
        };
        _checkpoints.Add((t / 3600.0, values));
        _checkpointCsv!.WriteLine(string.Join(",", new[] { F(t) }.Concat(new[]
        {
            "managed_heap_mib", "rss_anon_mib", "native_held_mib", "rss_anon_minus_native_mib", "gc_committed_mib", "threads", "mod_threads", "open_files",
        }.Select(k => F(values[k])))));
        _checkpointCsv.Flush();
    }

    private Dictionary<string, object?> Summarize(double elapsedS, double durationS, bool interrupted, string? stopReason, int recycles,
        int leakedThreads, int controlErrors, int samples, double maxGapActive, long[] progressByThird)
    {
        // The final checkpoint is taken after Shutdown and only counts for threads and files.
        var running = _checkpoints.Take(Math.Max(0, _checkpoints.Count - 1)).ToList();
        var verdicts = new List<LeakVerdict>();
        foreach (var pair in LeakDetector.Defaults)
        {
            var series = running.Where(c => c.Values.ContainsKey(pair.Key) && double.IsFinite(c.Values[pair.Key]))
                .Select(c => (c.Hours, c.Values[pair.Key])).ToList();
            string unit = pair.Key.EndsWith("_mib", StringComparison.Ordinal) ? "MiB" : "count";
            verdicts.Add(LeakDetector.Evaluate(pair.Key, unit, series, pair.Value));
        }
        bool progressedLate = durationS >= 600 && progressByThird[2] > progressByThird[1];
        double expectedGap = 60 + _env.Options.SoakPhaseSeconds * 4 + 30;
        return new Dictionary<string, object?>
        {
            ["schema"] = 1,
            ["elapsed_minutes"] = elapsedS / 60,
            ["planned_minutes"] = durationS / 60,
            ["interrupted"] = interrupted,
            ["stop_reason"] = stopReason,
            ["samples"] = samples,
            ["checkpoints"] = _checkpoints.Count,
            ["recycles"] = recycles,
            ["leaked_mod_threads_max"] = leakedThreads,
            ["control_errors"] = controlErrors,
            ["max_no_progress_active_s"] = maxGapActive,
            ["expected_max_gap_s"] = expectedGap,
            ["stall_suspected"] = maxGapActive > expectedGap,
            ["work_continues_last_third"] = durationS >= 600 ? progressedLate : null,
            ["accelerated_intervals"] = "RamPrefetchRefreshMinutes=1, CpuAuditRescanMinutes=1, AssetDiscoveryRescanMinutes=1 (defaults 5/10/10)",
            ["leaks"] = verdicts,
            ["code"] = _metadata.GetValueOrDefault("code"),
            ["machine"] = _metadata.GetValueOrDefault("machine"),
            ["dataset"] = _metadata.GetValueOrDefault("dataset"),
        };
    }

    private void Event(double t, string kind, string detail)
    {
        detail = PathRedactor.Redact(detail) ?? "";
        _events.Add(kind);
        _eventCsv?.WriteLine(string.Join(",", new[] { F(t), kind, detail }.Select(ResultWriter.Csv)));
        _eventCsv?.Flush();
        _env.Log(DateTime.Now.ToString("HH:mm:ss") + " soak " + kind + " " + detail);
    }

    private StreamWriter Open(string name, string header)
    {
        string path = Path.Combine(_dir, name);
        bool exists = File.Exists(path) && new FileInfo(path).Length > 0;
        var w = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        if (!exists) { w.WriteLine(header); w.Flush(); }
        return w;
    }

    private static string F(double? v) => v.HasValue && double.IsFinite(v.Value) ? ResultWriter.Format(v.Value) : "";
}
