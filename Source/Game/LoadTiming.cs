using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Xml;
using HarmonyLib;
using MoreUseResources.Core;
using RimWorld;
using Verse;

namespace MoreUseResources;

/// <summary>
/// Measures the stages of the game's data load that run after this mod was constructed.
/// The patches only read a clock before and after the original method; they never skip it,
/// change its arguments or swallow its exceptions.
/// </summary>
internal static class LoadTiming
{
    internal static readonly LoadStageRecorder Recorder = new();
    private static readonly long ModStartStamp = Stopwatch.GetTimestamp();

    internal static void Install(Harmony harmony)
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            Recorder.SetStartup((DateTime.Now - process.StartTime).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is NotSupportedException || ex is System.ComponentModel.Win32Exception)
        {
        }

        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var prefix = new HarmonyMethod(typeof(LoadTiming).GetMethod(nameof(Begin), flags));
        var finalizer = new HarmonyMethod(typeof(LoadTiming).GetMethod(nameof(End), flags));
        foreach (MethodBase? method in Targets())
        {
            if (method == null) continue;
            try
            {
                harmony.Patch(method, prefix: prefix, finalizer: finalizer);
            }
            catch (Exception ex)
            {
                Log.Warning("[More Use Resources] Load timing skipped " + method.Name + ": " + ex.Message);
            }
        }
    }

    /// <summary>Called on the main thread once play data is loaded and no long event is pending.</summary>
    internal static void CompleteOnce()
    {
        if (Recorder.Completed) return;
        long peak = 0;
        try
        {
            using Process process = Process.GetCurrentProcess();
            peak = process.PeakWorkingSet64;
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is NotSupportedException || ex is System.ComponentModel.Win32Exception)
        {
        }
        Recorder.Complete(Stopwatch.GetElapsedTime(ModStartStamp).TotalMilliseconds, peak);
        Log.Message("[More Use Resources] Load timing: " + Recorder.Report());
    }

    private static IEnumerable<MethodBase?> Targets()
    {
        Type mods = typeof(LoadedModManager);
        yield return AccessTools.DeclaredMethod(mods, nameof(LoadedModManager.LoadModXML), new[] { typeof(bool) });
        yield return AccessTools.DeclaredMethod(mods, nameof(LoadedModManager.CombineIntoUnifiedXML),
            new[] { typeof(List<LoadableXmlAsset>), typeof(Dictionary<XmlNode, LoadableXmlAsset>) });
        yield return AccessTools.DeclaredMethod(mods, nameof(LoadedModManager.ErrorCheckPatches), Type.EmptyTypes);
        yield return AccessTools.DeclaredMethod(mods, nameof(LoadedModManager.ApplyPatches),
            new[] { typeof(XmlDocument), typeof(Dictionary<XmlNode, LoadableXmlAsset>) });
        yield return AccessTools.DeclaredMethod(mods, nameof(LoadedModManager.ParseAndProcessXML),
            new[] { typeof(XmlDocument), typeof(Dictionary<XmlNode, LoadableXmlAsset>), typeof(bool) });
        yield return AccessTools.DeclaredMethod(typeof(DirectXmlCrossRefLoader),
            nameof(DirectXmlCrossRefLoader.ResolveAllWantedCrossReferences), new[] { typeof(FailMode) });
        yield return AccessTools.DeclaredMethod(typeof(DefGenerator), nameof(DefGenerator.GenerateImpliedDefs_PreResolve), new[] { typeof(bool) });
        yield return AccessTools.DeclaredMethod(typeof(DefGenerator), nameof(DefGenerator.GenerateImpliedDefs_PostResolve), new[] { typeof(bool) });
        yield return AccessTools.DeclaredMethod(typeof(StaticConstructorOnStartupUtility), nameof(StaticConstructorOnStartupUtility.CallAll), Type.EmptyTypes);
        yield return AccessTools.DeclaredMethod(typeof(GlobalTextureAtlasManager), nameof(GlobalTextureAtlasManager.BakeStaticAtlases), Type.EmptyTypes);
    }

    private static void Begin(out long __state) => __state = Stopwatch.GetTimestamp();

    private static void End(MethodBase __originalMethod, long __state)
    {
        if (__state == 0 || Recorder.Completed) return;
        Recorder.Add(__originalMethod.Name, Stopwatch.GetElapsedTime(__state).TotalMilliseconds);
    }
}
