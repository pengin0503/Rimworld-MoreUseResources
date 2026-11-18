using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MoreUseResources.Benchmarks.Data;
using MoreUseResources.Benchmarks.Running;
using MoreUseResources.Core;

namespace MoreUseResources.Benchmarks.Scenarios;

/// <summary>
/// Builds the real <see cref="ResourceController"/> the way ResourceRuntime does in the game, with
/// the game's inputs replaced by the dataset: the load folders instead of the running mod list,
/// the dataset's packs instead of the engine's cache directory, and fixed phase signals.
/// </summary>
internal static class ControllerHarness
{
    public static readonly RawGameSignals MainMenu = new() { PlayDataLoaded = true, Stage = ProgramStage.Entry, Focused = true };
    public static readonly RawGameSignals Playing = new() { PlayDataLoaded = true, Stage = ProgramStage.Playing, Focused = true };
    public static readonly RawGameSignals PlayingFast = new() { PlayDataLoaded = true, Stage = ProgramStage.Playing, FastSpeed = true, Focused = true };
    public static readonly RawGameSignals PausedGame = new() { PlayDataLoaded = true, Stage = ProgramStage.Playing, Paused = true, Focused = true };
    public static readonly RawGameSignals Loading = new() { PlayDataLoaded = true, Stage = ProgramStage.MapInitializing, LongEventActive = true, Focused = true };
    public static readonly RawGameSignals Saving = new() { PlayDataLoaded = true, Stage = ProgramStage.Playing, Saving = true, Focused = true };

    /// <summary>All three known performance mods active, their worker settings on automatic, the shared pool busy.</summary>
    public static readonly ExternalParallelism AllActive = new(ExternalFeatureState.Active, ExternalFeatureState.Active, ExternalFeatureState.Active,
        new ExternalWorkerSetting("WOWGAG workers", 0, null), new ExternalWorkerSetting("Overclock workers", 0, null),
        SharedPoolBusy: true, SharedPoolWorkers: Environment.ProcessorCount);

    /// <summary>
    /// The mod's defaults with the benchmark's limits: the native copy budget is the run's RAM budget,
    /// and RamMinFreeMiB is lowered only when the machine could never satisfy the default.
    /// </summary>
    public static ResourceSettings Settings(BenchOptions o, string preset, int maxWorkers, List<string> notes)
    {
        var s = new ResourceSettings();
        if (preset == "asset-boost") s.ApplyAssetBoost();
        s.CpuMaxWorkers = maxWorkers;
        s.RamFileCacheMiB = Math.Min(s.RamFileCacheMiB, o.RamBudgetMiB);
        s.RamResidentPackMiB = Math.Min(s.RamResidentPackMiB, Math.Max(64, o.RamBudgetMiB));
        long available = (new SystemMemoryProbe().Read().AvailablePhysical) / ResourceSettings.MiB;
        long needed = s.RamMinFreeMiB + MemoryGovernor.MarginBytes / ResourceSettings.MiB + 512;
        if (available < needed + o.RamBudgetMiB)
        {
            int lowered = (int)Math.Max(1024, Math.Min(s.RamMinFreeMiB, (available - o.RamBudgetMiB) / 2));
            notes.Add("RamMinFreeMiB lowered from " + s.RamMinFreeMiB + " to " + lowered + " (machine has " + available + " MiB available)");
            s.RamMinFreeMiB = lowered;
        }
        s.Clamp();
        return s;
    }

    /// <summary>
    /// Logical processors handed to the controller. EffectiveMaxWorkers clamps to it, so asking
    /// for more workers than the machine has means emulating a larger processor count.
    /// </summary>
    public static int LogicalProcessors(int requestedWorkers) => Math.Max(requestedWorkers, Environment.ProcessorCount);

    public static ResourceController Create(Dataset dataset, ResourceSettings settings, int logicalProcessors, IMemoryProbe? probe = null,
        Func<long>? assetClockMs = null, Func<bool>? savingProbe = null)
    {
        List<string> roots = dataset.Mods.Select(m => m.Path).ToList();
        List<string> folders = dataset.LoadFolders.ToList();
        IReadOnlyList<string> extra = dataset.Packs;
        var controller = new ResourceController(probe ?? new SystemMemoryProbe(), logicalProcessors, savingProbe ?? (() => false), settings,
            assetClockMs: assetClockMs,
            packDiscovery: token => EnginePackFiles.Discover(null, roots, folders, extra, token));
        controller.PublishSignals(MainMenu);
        controller.SetAssetFolders(folders);
        return controller;
    }

    /// <summary>Waits for the controller's shutdown and reports how long it took.</summary>
    public static double Shutdown(ResourceController controller)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        controller.Shutdown();
        return clock.Elapsed.TotalMilliseconds;
    }

    public static void StatusMetrics(TrialContext ctx, ControllerStatus s, string prefix = "")
    {
        ctx.Metric(prefix + "file_sources", s.FileSources);
        ctx.Metric(prefix + "prefetch_files", s.FilesRead);
        ctx.Metric(prefix + "prefetch_mib", s.FileBytesRead / 1048576.0);
        ctx.Metric(prefix + "prefetch_errors", s.FileErrors);
        ctx.Metric(prefix + "deferred_for_budget", s.FilesDeferredForBudget);
        ctx.Metric(prefix + "warmed_without_copy", s.FilesWarmedWithoutCopy);
        ctx.Metric(prefix + "engine_packs", s.EnginePackFiles);
        ctx.Metric(prefix + "audit_sources", s.AuditSources);
        ctx.Metric(prefix + "audit_files", s.AuditCompleted);
        ctx.Metric(prefix + "audit_mib", s.AuditHashedBytes / 1048576.0);
        ctx.Metric(prefix + "audit_errors", s.AuditErrors);
        ctx.Metric(prefix + "duplicates", s.AuditDuplicates);
        ctx.Metric(prefix + "xml_definitions", s.XmlDefinitions);
        ctx.Metric(prefix + "xml_collisions", s.XmlDefinitionCollisions);
        ctx.Metric(prefix + "textures_valid", s.TextureFilesValid);
        ctx.Metric(prefix + "native_held_mib", s.CacheHeldBytes / 1048576.0);
        ctx.Metric(prefix + "native_limit_mib", s.CacheLimitBytes / 1048576.0);
        ctx.Metric(prefix + "resident_pack_mib", s.ResidentPackBytes / 1048576.0);
        ctx.Metric(prefix + "resident_pack_files", s.ResidentPackFiles);
        ctx.Metric(prefix + "pressure_events", s.PressureEvents);
        ctx.Metric(prefix + "allocation_failures", s.AllocationFailures);
        ctx.Metric(prefix + "worker_cap", s.WorkerCap);
        ctx.Metric(prefix + "worker_threads", s.WorkerThreads);
        ctx.Metric(prefix + "worker_cpu_share_pct", s.WorkerCpuShare * 100);
        ctx.Metric(prefix + "discovery_passes", s.FileDiscoveryPasses);
        ctx.Metric(prefix + "audit_passes", s.AuditPassesCompleted);
        ctx.Metric(prefix + "refresh_passes", s.FileRefreshPasses);
        if (s.ControlError != null) ctx.Note("controller error: " + s.ControlError);
        if (s.WorkerLastError != null) ctx.Note("worker error: " + s.WorkerLastError);
        if (s.ResidentPackLastError != null) ctx.Note("resident pack error: " + s.ResidentPackLastError);
    }

    public static double AuditHash(ControllerStatus s) => Common.ResultHash(s.AuditSources, s.AuditCompleted, s.AuditDuplicates,
        s.AuditDuplicateBytes, s.AuditHashedBytes, s.AuditErrors, s.AuditSkipped, s.XmlFilesChecked, s.XmlParseFailures, s.XmlDefinitions,
        s.XmlDefinitionCollisions, s.XmlFilesSkipped, s.TextureFilesChecked, s.TextureFilesValid, s.TextureFilesMalformed, s.TextureRgba8EquivalentBytes);
}
