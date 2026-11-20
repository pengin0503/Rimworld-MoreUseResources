using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace MoreUseResources;

/// <summary>
/// Wall time the main thread has spent in the game's tick loop, in the map updates (region, power and glow
/// upkeep and the drawing of the current map) and in the UI, as Stopwatch ticks summed since the patches went in.
/// Each call adds two clock reads; nothing else changes. The prefixes run first and the postfixes last,
/// so other mods' patches of the same methods count as part of the method.
/// </summary>
internal static class MainThreadTimers
{
    private static long tickTicks;
    private static long mapTicks;
    private static long uiTicks;

    /// <summary>True once all three methods are timed; until then the split is not reported.</summary>
    internal static bool Installed { get; private set; }

    /// <summary>Read on the main thread, which is the only thread that writes these.</summary>
    internal static (long Tick, long Map, long Ui) Read() => (tickTicks, mapTicks, uiTicks);

    internal static void Install(Harmony harmony)
    {
        try
        {
            Patch(harmony, AccessTools.DeclaredMethod(typeof(TickManager), nameof(TickManager.TickManagerUpdate), Type.EmptyTypes),
                nameof(BeforeTick), nameof(AfterTick));
            Patch(harmony, AccessTools.DeclaredMethod(typeof(Map), nameof(Map.MapUpdate), Type.EmptyTypes),
                nameof(BeforeMap), nameof(AfterMap));
            Patch(harmony, AccessTools.DeclaredMethod(typeof(Root), nameof(Root.OnGUI), Type.EmptyTypes),
                nameof(BeforeUi), nameof(AfterUi));
            Installed = true;
        }
        catch (Exception ex)
        {
            Log.Warning("[More Use Resources] The main thread's tick, map and UI time will not be measured. " + ex.Message);
        }
    }

    private static void Patch(Harmony harmony, MethodInfo? target, string prefix, string postfix)
    {
        if (target == null) throw new MissingMethodException("A timed game method is missing.");
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        harmony.Patch(target,
            prefix: new HarmonyMethod(typeof(MainThreadTimers).GetMethod(prefix, flags)) { priority = Priority.First },
            postfix: new HarmonyMethod(typeof(MainThreadTimers).GetMethod(postfix, flags)) { priority = Priority.Last });
    }

    private static void BeforeTick(out long __state) => __state = Stopwatch.GetTimestamp();
    private static void AfterTick(long __state) => tickTicks += Stopwatch.GetTimestamp() - __state;
    private static void BeforeMap(out long __state) => __state = Stopwatch.GetTimestamp();
    private static void AfterMap(long __state) => mapTicks += Stopwatch.GetTimestamp() - __state;
    private static void BeforeUi(out long __state) => __state = Stopwatch.GetTimestamp();
    private static void AfterUi(long __state) => uiTicks += Stopwatch.GetTimestamp() - __state;
}
