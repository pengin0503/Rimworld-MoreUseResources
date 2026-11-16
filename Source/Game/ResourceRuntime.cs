using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Runtime;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MoreUseResources.Core;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Waylith.Runtime;
using Waylith.Rendering;
using Waylith.Assets;

namespace MoreUseResources;

internal sealed class ResourceRuntime
{
    internal const string PackageId = "pengin0503.moreuseresources";
    private static ResourceRuntime? instance;
    private readonly ResourceController controller;
    private readonly TexturePrewarm gpu = new();
    private readonly PerformanceMeter performance = new();
    private readonly AdaptiveWorkerBudget adaptiveWorkers = new();
    private (GamePhase Phase, TimeSpeed? Speed, float? EffectiveRate, bool Focused, bool Adaptive) performanceContext;
    private long nextJitCompatibilityCheck;
    private volatile bool exportStartupPacks;
    private PackSourceSnapshot? lastManifestQueued;
    private int manifestInFlight;
    private readonly string? startupModsConfigHash;
    internal bool JitYieldedToWowgag { get; private set; }
    private readonly GcMonitor gcMonitor = new();
    private readonly EngineExperiments experiments = new();
    private bool jitSourcesPublished;
    private bool gcBaselineLoaded;
    private GamePhase benchmarkPhase;
    private GCLatencyMode benchmarkLatency;
    private long gcComparableSince;
    private int benchmarkInterrupted;
    private bool experimentsFaulted;
    private int dataReloaded;
    internal GcBenchmark? GcCurrent { get; private set; }
    internal GcBenchmark? GcBaseline { get; private set; }
    internal EngineExperiments Experiments => experiments;
    internal JitProgress Jit => controller.Jit;
    private static string GcBaselinePath => Path.Combine(GenFilePaths.SaveDataFolderPath, "MoreUseResources-GC.json");
    private readonly bool recordLoadTimingOnLaunch;
    private ResourceSettings settings;
    private PackSourceSnapshot? packSources;

    private sealed class PackSourceSnapshot
    {
        public readonly string? CacheDataPath;
        public readonly List<string> Roots;
        public readonly List<string> Folders;
        public readonly string[] ExtraFiles;
        public readonly string? ActiveCacheRoot;
        public readonly string? InstallId;
        public readonly string GameDirectory;
        public readonly string ManifestPath;
        public readonly string ModsConfigPath;
        public readonly string[]? MountedPacks;

        public PackSourceSnapshot(string? cacheDataPath, List<string> roots, List<string> folders, string[] extraFiles,
            string? activeCacheRoot, string? installId, string gameDirectory, string manifestPath, string modsConfigPath, string[]? mountedPacks)
        {
            CacheDataPath = cacheDataPath;
            Roots = roots;
            Folders = folders;
            ExtraFiles = extraFiles;
            ActiveCacheRoot = activeCacheRoot;
            InstallId = installId;
            GameDirectory = gameDirectory;
            ManifestPath = manifestPath;
            ModsConfigPath = modsConfigPath;
            MountedPacks = mountedPacks;
        }
    }
    private int savingDepth;
    private int stopped;
    private long lastInputMs = Environment.TickCount64;
    private Vector3 lastMouse;
    private bool faulted;
    private bool startupFinished;

    internal ResourceRuntime(ResourceSettings settings)
    {
        this.settings = settings;
        exportStartupPacks = settings.Enabled && settings.ExportStartupPacks;
        if (TexturePrewarm.SupportedBuild)
        {
            try { startupModsConfigHash = StartupPackManifest.ReadConfigHash(GenFilePaths.ModsConfigFilePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        recordLoadTimingOnLaunch = settings.RecordLoadTiming;
        // Mod constructors run before all mods finish loading. Defer asset-list
        // capture until the first fully loaded frame on the game thread.
        controller = new ResourceController(new SystemMemoryProbe(), Environment.ProcessorCount,
            () => Volatile.Read(ref savingDepth) > 0, settings, packDiscovery: CapturePackDiscovery());
    }

    internal bool HooksReady { get; private set; }
    internal TexturePrewarm Gpu => gpu;
    internal PerformanceSnapshot Performance => performance.Latest;
    internal GcMonitor Gc => gcMonitor;
    internal int AdaptiveWorkerPercent => controller.AdaptiveWorkerPercent;
    internal ControllerStatus Status => controller.GetStatus();
    internal ManualMode Manual { get => controller.Manual; set => controller.Manual = value; }
    internal bool Faulted => faulted;

    internal void Install()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var harmony = new Harmony(PackageId);
        try
        {
            harmony.Patch(AccessTools.DeclaredMethod(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.SaveGame), new[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(ResourceRuntime).GetMethod(nameof(BeforeSave), flags)),
                finalizer: new HarmonyMethod(typeof(ResourceRuntime).GetMethod(nameof(AfterSave), flags)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Root), nameof(Root.Update), Type.EmptyTypes),
                prefix: new HarmonyMethod(typeof(ResourceRuntime).GetMethod(nameof(BeforeUpdate), flags)),
                postfix: new HarmonyMethod(typeof(ResourceRuntime).GetMethod(nameof(AfterUpdate), flags)));
            Volatile.Write(ref instance, this);
            try
            {
                harmony.Patch(AccessTools.DeclaredMethod(typeof(LoadedModManager), nameof(LoadedModManager.LoadAllActiveMods)),
                    prefix: new HarmonyMethod(typeof(ResourceRuntime).GetMethod(nameof(BeforeReload), flags)));
            }
            catch (Exception ex)
            {
                Log.Warning("[More Use Resources] Could not observe definition reloads. " + ex.Message);
            }
            if (recordLoadTimingOnLaunch) LoadTiming.Install(harmony);
            if (settings.Enabled && settings.CachePatchedXml)
            {
                try { PatchedXmlCache.Install(harmony); }
                catch (Exception ex) { Log.Warning("[More Use Resources] Could not install patched XML cache. " + ex.Message); }
            }
            UpdateExperiments(loaded: false, ready: false, default, default, 0);
            Application.quitting += Stop;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            controller.Start();
            HooksReady = true;
            Log.Message("[More Use Resources] Frame, save and shutdown observers installed.");
        }
        catch (Exception ex)
        {
            Stop();
            Log.Error("[More Use Resources] Could not install runtime observers. " + ex);
        }
    }

    internal void SettingsChanged(ResourceSettings value)
    {
        bool export = value.Enabled && value.ExportStartupPacks;
        bool exportJustEnabled = export && !exportStartupPacks;
        exportStartupPacks = export;
        if (faulted) return;
        controller.UpdateSettings(value.Clone());
        if (exportJustEnabled) lastManifestQueued = null;
    }

    internal void ReleaseRam()
    {
        Manual = ManualMode.Paused;
        controller.RequestReleaseMemory();
    }

    internal void SaveGcBaseline()
    {
        if (GcCurrent is not { } sample) return;
        try
        {
            GcBenchmark.Save(GcBaselinePath, sample);
            GcBaseline = sample;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning("[More Use Resources] Could not save GC baseline. " + ex.Message);
        }
    }

    private void UpdateGc(long now, GamePhase phase)
    {
        GCLatencyMode latency = GCSettings.LatencyMode;
        bool interrupted = Interlocked.Exchange(ref benchmarkInterrupted, 0) != 0;
        if (interrupted || phase != benchmarkPhase || latency != benchmarkLatency)
        {
            benchmarkPhase = phase;
            benchmarkLatency = latency;
            gcComparableSince = now;
            GcCurrent = null;
        }
        if (!gcMonitor.Sample(now, latency)) return;
        if (phase is GamePhase.MainMenu or GamePhase.Paused or GamePhase.PlayingNormal or GamePhase.PlayingFast
            && now - gcComparableSince >= gcMonitor.Latest.WindowSeconds * 1000 - 0.01)
            GcCurrent = GcBenchmark.From(gcMonitor.Latest, phase);
    }

    private void UpdateJitSources(long now)
    {
        if (!settings.Enabled || !settings.CpuEnabled || !settings.CpuJitPrewarm)
        {
            nextJitCompatibilityCheck = 0;
            return;
        }
        if (now < nextJitCompatibilityCheck) return;
        nextJitCompatibilityCheck = now + 2000;
        JitYieldedToWowgag = WowgagCompatibility.YieldJit();
        if (JitYieldedToWowgag)
        {
            if (jitSourcesPublished) controller.ClearJitSources();
            jitSourcesPublished = false;
        }
        else if (!jitSourcesPublished) PublishJitSources();
    }

    private void PublishJitSources()
    {
        jitSourcesPublished = true;
        try
        {
            var assemblies = new List<Assembly> { typeof(Root).Assembly };
            var seen = new HashSet<Assembly>(assemblies) { typeof(ResourceRuntime).Assembly };
            foreach (ModContentPack pack in LoadedModManager.RunningModsListForReading)
                foreach (Assembly assembly in pack.assemblies.loadedAssemblies)
                    if (!assembly.IsDynamic && seen.Add(assembly)) assemblies.Add(assembly);
            controller.SetJitSources(assemblies, Harmony.GetAllPatchedMethods());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warning("[More Use Resources] JIT preparation could not start. " + ex.Message);
        }
    }

    private void UpdateExperiments(bool loaded, bool ready, GamePhase phase, ResourceActivation activation, long frameStart)
    {
        if (experimentsFaulted) return;
        try
        {
            experiments.UpdateCodec(settings.Enabled && settings.UncompressedTextureCache);
            if (frameStart != 0)
                experiments.Update(settings, phase, activation, ready, loaded,
                    settings.FrameGuardMs > 0 && controller.FrameThrottled, controller.ExtraMemoryAvailable, frameStart);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            experimentsFaulted = true;
            experiments.Restore();
            Log.Warning("[More Use Resources] Engine experiments stopped. " + ex.Message);
        }
    }

    /// <summary>
    /// The worker reads only the published immutable snapshot. It never reads RimWorld
    /// objects, and an absent snapshot simply means discovery has not started yet.
    /// </summary>
    private Func<CancellationToken, IReadOnlyList<string>> CapturePackDiscovery()
    {
        return token =>
        {
            PackSourceSnapshot? snapshot = Volatile.Read(ref packSources);
            return snapshot == null ? Array.Empty<string>() : DiscoverPacks(snapshot, token);
        };
    }

    private static List<string> DiscoverPacks(PackSourceSnapshot snapshot, CancellationToken token) =>
        EnginePackFiles.Discover(snapshot.CacheDataPath, snapshot.Roots, snapshot.Folders,
            snapshot.ExtraFiles, token, snapshot.ActiveCacheRoot, snapshot.InstallId, snapshot.MountedPacks);

    private void QueueStartupManifest()
    {
        PackSourceSnapshot? snapshot = Volatile.Read(ref packSources);
        if (!exportStartupPacks || startupModsConfigHash == null
            || snapshot?.ActiveCacheRoot == null || ReferenceEquals(snapshot, lastManifestQueued)
            || Interlocked.CompareExchange(ref manifestInFlight, 1, 0) != 0)
            return;
        lastManifestQueued = snapshot;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                List<string> packs = DiscoverPacks(snapshot, CancellationToken.None);
                StartupPackManifest.Save(snapshot.ManifestPath, snapshot.GameDirectory,
                    snapshot.ActiveCacheRoot, snapshot.ModsConfigPath, packs, CancellationToken.None,
                    () => exportStartupPacks && Volatile.Read(ref stopped) == 0
                        && ReferenceEquals(snapshot, Volatile.Read(ref packSources)),
                    snapshot.MountedPacks, startupModsConfigHash);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning("[More Use Resources] Could not update the startup pack manifest. " + ex.Message);
            }
            finally { Interlocked.Exchange(ref manifestInFlight, 0); }
        });
    }

    private static string[]? CaptureMountedPacks()
    {
        if (!TexturePrewarm.SupportedBuild) return null;
        var mounts = ContentPack.Mounts;
        var paths = new string[mounts.Count];
        for (int i = 0; i < mounts.Count; i++) paths[i] = mounts[i].Pack.Path;
        return paths;
    }

    private static (string? Root, string? InstallId) CaptureActiveTextureCache()
    {
        if (!TexturePrewarm.SupportedBuild) return default;
        try
        {
            var root = typeof(GpuTextureService).GetProperty("TextureCacheRoot",
                BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as string;
            return string.IsNullOrWhiteSpace(root) ? default : (root, CacheFileWriter.InstallId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return default;
        }
    }

    private void UpdatePerformance(long now, TickManager? ticks, double frameIntervalMs, GamePhase phase, bool focused)
    {
        var context = (phase, ticks?.CurTimeSpeed, ticks?.TickRateMultiplier, focused, settings.AdaptiveWorkers);
        if (context != performanceContext)
        {
            performanceContext = context;
            performance.Reset();
            adaptiveWorkers.Reset();
            controller.SetAdaptiveWorkerPercent(100);
        }
        bool completed = performance.Sample(now, ticks?.TicksGame, frameIntervalMs);
        if (!settings.AdaptiveWorkers || !focused || phase is not (GamePhase.PlayingNormal or GamePhase.PlayingFast))
            return;
        if (!completed) return;
        var sample = performance.Latest;
        adaptiveWorkers.Observe(sample.P95FrameIntervalMs, sample.TicksPerSecond);
        controller.SetAdaptiveWorkerPercent(adaptiveWorkers.Percent);
    }

    private static List<string> CaptureFolders()
    {
        var folders = new List<string>();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        foreach (ModContentPack pack in LoadedModManager.RunningModsListForReading)
        {
            foreach (string folder in pack.foldersToLoadDescendingOrder)
            {
                string fullPath = Path.GetFullPath(folder);
                string relative = Path.GetRelativePath(pack.RootDir, fullPath);
                if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    continue;
                if (seen.Add(fullPath)) folders.Add(fullPath);
            }
        }
        return folders;
    }

    private static void BeforeSave(out ResourceRuntime? __state)
    {
        __state = Volatile.Read(ref instance);
        if (__state == null) return;
        Interlocked.Increment(ref __state.savingDepth);
        Interlocked.Exchange(ref __state.benchmarkInterrupted, 1);
    }

    private static void AfterSave(ResourceRuntime? __state)
    {
        if (__state != null) Interlocked.Decrement(ref __state.savingDepth);
    }

    private static void BeforeReload(bool hotReload)
    {
        if (hotReload && Volatile.Read(ref instance) is { } runtime)
            Interlocked.Exchange(ref runtime.dataReloaded, 1);
    }

    private static void BeforeUpdate() => Volatile.Read(ref instance)?.Observe(endOfUpdate: false);
    private static void AfterUpdate() => Volatile.Read(ref instance)?.Observe(endOfUpdate: true);
    private void OnProcessExit(object? sender, EventArgs args) => Stop();

    private void Observe(bool endOfUpdate)
    {
        if (Volatile.Read(ref stopped) != 0 || faulted) return;
        try
        {
            // Before the first play data load, the default startup signal allows CPU/RAM
            // initialization even though Mod is constructed by the asynchronous loader.
            bool queuedLongEvent = LongEventHandler.AnyEventNowOrWaiting;
            if (!endOfUpdate && !queuedLongEvent) return;
            if (queuedLongEvent) Interlocked.Exchange(ref benchmarkInterrupted, 1);
            bool loaded = PlayDataLoader.Loaded;
            startupFinished |= loaded;
            bool loading = queuedLongEvent || (startupFinished && !loaded);
            var stage = Current.ProgramState switch
            {
                ProgramState.MapInitializing => ProgramStage.MapInitializing,
                ProgramState.Playing => ProgramStage.Playing,
                _ => ProgramStage.Entry,
            };
            bool steadyPlay = stage == ProgramStage.Playing && !loading && Current.Game != null;
            TickManager? ticks = steadyPlay ? Find.TickManager : null;
            bool focused = Application.isFocused;
            long now = Environment.TickCount64;
            Vector3 mouse = Input.mousePosition;
            if (!focused || Input.anyKey || Input.mouseScrollDelta != Vector2.zero || mouse != lastMouse)
                lastInputMs = now;
            lastMouse = mouse;
            var signals = new RawGameSignals
            {
                PlayDataLoaded = startupFinished,
                LongEventActive = loading,
                Stage = stage,
                Paused = ticks?.Paused ?? true,
                FastSpeed = ticks != null && ticks.CurTimeSpeed > TimeSpeed.Normal,
                WorldView = steadyPlay && WorldRendererUtility.WorldRendered,
                Focused = focused,
                SecondsSinceInput = (now - lastInputMs) / 1000f,
            };
            controller.PublishSignals(signals);
            if (!endOfUpdate) return;
            bool reloadCompleted = loaded && !loading && Interlocked.Exchange(ref dataReloaded, 0) != 0;
            if (loaded && !queuedLongEvent && (reloadCompleted || Volatile.Read(ref packSources) == null))
            {
                var roots = new List<string>();
                foreach (ModContentPack pack in LoadedModManager.RunningModsListForReading)
                    if (!string.IsNullOrEmpty(pack.RootDir)) roots.Add(Path.GetFullPath(pack.RootDir));
                List<string> folders = CaptureFolders();
                string resources = Path.Combine(AppContext.BaseDirectory, "Resources");
                if (Directory.Exists(resources)) roots.Add(Path.GetFullPath(resources));
                var extra = new[] { Path.Combine(AppContext.BaseDirectory, EnginePackFiles.AssetPackFileName) };
                var (cacheRoot, installId) = CaptureActiveTextureCache();
                string gameDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                var snapshot = new PackSourceSnapshot(WaylithRuntime.Current?.App.CacheDataPath, roots, folders, extra,
                    cacheRoot, installId, gameDirectory, Path.Combine(GenFilePaths.SaveDataFolderPath, StartupPackManifest.FileName),
                    GenFilePaths.ModsConfigFilePath, CaptureMountedPacks());
                Volatile.Write(ref packSources, snapshot);
                controller.SetAssetFolders(folders);
            }
            if (loaded && !queuedLongEvent && recordLoadTimingOnLaunch) LoadTiming.CompleteOnce();

            double frameIntervalMs = Time.unscaledDeltaTime * 1000;
            controller.ReportFrame(frameIntervalMs);
            // Use the frame's live phase here, not the controller's previous 50 ms sample.
            signals.Saving = Volatile.Read(ref savingDepth) > 0;
            var phase = PhaseResolver.Resolve(signals, settings.IdleBoost ? settings.IdleSeconds : 0);
            UpdatePerformance(now, ticks, frameIntervalMs, phase.Phase, focused);
            var activation = ActivationPlanner.PlanOne(settings, phase, Manual, ResourceKind.Gpu);
            bool ready = loaded && !loading;
            if (ready && !gcBaselineLoaded)
            {
                gcBaselineLoaded = true;
                GcBaseline = GcBenchmark.Load(GcBaselinePath);
            }
            UpdateGc(now, phase.Phase);
            if (ready)
            {
                QueueStartupManifest();
                UpdateJitSources(now);
            }

            if (reloadCompleted)
            {
                experiments.ResetGraphicsScans();
                gpu.InvalidateScan();
            }
            long frameStart = Stopwatch.GetTimestamp();
            gpu.Update(phase.Phase, activation, settings, controller.FrameThrottled, ready);
            UpdateExperiments(loaded, ready, phase.Phase, activation, frameStart);
        }
        catch (Exception ex)
        {
            faulted = true;
            controller.Manual = ManualMode.Paused;
            controller.UpdateSettings(new ResourceSettings { Enabled = false });
            controller.RequestReleaseMemory();
            gpu.Stop();
            experiments.Restore();
            Log.Error("[More Use Resources] Frame observer stopped. " + ex);
        }
    }

    private void Stop()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        Interlocked.CompareExchange(ref instance, null, this);
        Application.quitting -= Stop;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        try { gpu.Stop(); experiments.Restore(); }
        finally { controller.Shutdown(); }
    }
}
