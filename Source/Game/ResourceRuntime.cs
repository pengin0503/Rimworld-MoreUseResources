using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MoreUseResources.Core;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Waylith.Runtime;

namespace MoreUseResources;

internal sealed class ResourceRuntime
{
    internal const string PackageId = "pengin0503.moreuseresources";
    private static ResourceRuntime? instance;
    private readonly ResourceController controller;
    private readonly TexturePrewarm gpu = new();
    private readonly PerformanceMeter performance = new();
    private ResourceSettings settings;
    private int savingDepth;
    private int stopped;
    private long lastInputMs = Environment.TickCount64;
    private Vector3 lastMouse;
    private bool faulted;
    private bool startupFinished;

    internal ResourceRuntime(ResourceSettings settings)
    {
        this.settings = settings;
        controller = new ResourceController(new SystemMemoryProbe(), Environment.ProcessorCount,
            () => Volatile.Read(ref savingDepth) > 0, settings, CaptureFolders(), packDiscovery: CapturePackDiscovery());
    }

    internal bool HooksReady { get; private set; }
    internal TexturePrewarm Gpu => gpu;
    internal PerformanceSnapshot Performance => performance.Latest;
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
            if (settings.RecordLoadTiming) LoadTiming.Install(harmony);
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
        if (!faulted) controller.UpdateSettings(value.Clone());
    }

    internal void ReleaseRam()
    {
        Manual = ManualMode.Paused;
        controller.RequestReleaseMemory();
    }

    /// <summary>
    /// Captures plain strings on the main thread so that the worker which enumerates the engine's
    /// texture-cache and asset packs never touches game objects.
    /// </summary>
    private static Func<CancellationToken, IReadOnlyList<string>> CapturePackDiscovery()
    {
        var roots = new List<string>();
        foreach (ModContentPack pack in LoadedModManager.RunningModsListForReading)
        {
            if (!string.IsNullOrEmpty(pack.RootDir)) roots.Add(Path.GetFullPath(pack.RootDir));
        }
        List<string> folders = CaptureFolders();
        string? cacheDataPath = WaylithRuntime.Current?.App.CacheDataPath;
        var extra = new[] { Path.Combine(AppContext.BaseDirectory, EnginePackFiles.AssetPackFileName) };
        return token => EnginePackFiles.Discover(cacheDataPath, roots, folders, extra, token);
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
        if (__state != null) Interlocked.Increment(ref __state.savingDepth);
    }

    private static void AfterSave(ResourceRuntime? __state)
    {
        if (__state != null) Interlocked.Decrement(ref __state.savingDepth);
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
            if (loaded && !queuedLongEvent && settings.RecordLoadTiming) LoadTiming.CompleteOnce();

            double frameIntervalMs = Time.unscaledDeltaTime * 1000;
            controller.ReportFrame(frameIntervalMs);
            performance.Sample(now, ticks?.TicksGame, frameIntervalMs);
            // Use the frame's live phase here, not the controller's previous 50 ms sample.
            signals.Saving = Volatile.Read(ref savingDepth) > 0;
            var phase = PhaseResolver.Resolve(signals, settings.IdleBoost ? settings.IdleSeconds : 0);
            var activation = ActivationPlanner.PlanOne(settings, phase, Manual, ResourceKind.Gpu);
            gpu.Update(phase.Phase, activation, settings, controller.FrameThrottled, loaded && !loading);
        }
        catch (Exception ex)
        {
            faulted = true;
            controller.Manual = ManualMode.Paused;
            controller.UpdateSettings(new ResourceSettings { Enabled = false });
            controller.RequestReleaseMemory();
            Log.Error("[More Use Resources] Frame observer stopped. " + ex);
        }
    }

    private void Stop()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        Interlocked.CompareExchange(ref instance, null, this);
        Application.quitting -= Stop;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        controller.Shutdown();
    }
}
