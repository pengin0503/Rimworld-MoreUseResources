using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MoreUseResources.Core;
using Verse;
using Waylith.Jobs;

namespace MoreUseResources;

/// <summary>
/// Reads what WOWGAG's Performance Patch, Overclock and Axen's Performance Enhancements are doing.
/// Everything here is read-only and runs on the game thread: known public settings, state fields of
/// a matching type and Harmony's own patch list. Nothing is written to another mod, and a missing
/// mod, an unknown version or a failed read becomes "unknown" instead of a guess.
/// The result is diagnostics plus the list of duplicated work units; it does not limit workers.
/// </summary>
internal sealed class PerformanceModCompatibility
{
    internal const string WowgagId = "wowgag.guiperformancepatch";
    internal const string OverclockId = "soap.overclock";
    internal const string AxenId = "axeno7.performanceplus";
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly string[] WowgagParallelOptions =
    {
        "parMapPost", "parPawnTick", "parPawnComps", "parPredictConst", "parPredictFail", "parPredictJob", "parWork", "parWorldPath",
    };
    private static readonly FieldInfo? ModSettingsField = typeof(Mod).GetField("modSettings", AnyInstance);

    private long nextPoll;
    private List<ForeignPatch> foreignPatches = new();
    private ExternalFeatureState axen = ExternalFeatureState.Absent;
    private readonly ExternalFeatureState launchXmlReuse;

    internal PerformanceModCompatibility(ExternalFeatureState launchXmlReuse) => this.launchXmlReuse = launchXmlReuse;

    internal ExternalParallelism Parallelism { get; private set; } = ExternalParallelism.None;
    internal WorkUnitYields Yields { get; private set; } = WorkUnitYields.None;
    /// <summary>Harmony owners registered on the path grid scheduler, for the status panel.</summary>
    internal string PathGridOwners { get; private set; } = string.Empty;
    internal bool SharedPoolReadable { get; private set; }

    /// <summary>
    /// WOWGAG's XML reuse, read once while this mod is constructed. WOWGAG loads before the game's
    /// core, so its settings exist by then; when they do not, the answer is Unknown and wins.
    /// </summary>
    internal static ExternalFeatureState ReadWowgagXmlReuse()
    {
        ModContentPack? pack = FindPack(WowgagId, prefix: false);
        if (pack == null) return ExternalFeatureState.Absent;
        try
        {
            object? settings = WowgagSettings(pack);
            if (settings?.GetType().GetField("reusePatchResult", AnyInstance)?.GetValue(settings) is bool reuse)
                return reuse ? ExternalFeatureState.Active : ExternalFeatureState.Inactive;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
        return ExternalFeatureState.Unknown;
    }

    /// <summary>Re-reads Harmony's patch list. Called when loading or a hot reload has finished.</summary>
    internal void RescanPatches()
    {
        var found = new List<ForeignPatch>();
        var owners = new SortedSet<string>(StringComparer.Ordinal);
        MethodBase? pathGrid = AccessTools.DeclaredMethod(typeof(PathFinder), "ScheduleGridJobs", Type.EmptyTypes);
        bool axenPatched = false;
        try
        {
            foreach (MethodBase method in Harmony.GetAllPatchedMethods())
            {
                Type? type = method.DeclaringType;
                if (type == null) continue;
                bool isPathGrid = pathGrid != null && method == pathGrid;
                VanillaWorkUnit[] units = UnitsDriving(method, type);
                if (!isPathGrid && units.Length == 0) continue;
                Patches? info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                Collect(info.Prefixes, canReplace: true, units, isPathGrid, found, owners, ref axenPatched);
                Collect(info.Transpilers, canReplace: true, units, isPathGrid, found, owners, ref axenPatched);
                Collect(info.Postfixes, canReplace: false, units, isPathGrid, found, owners, ref axenPatched);
                Collect(info.Finalizers, canReplace: false, units, isPathGrid, found, owners, ref axenPatched);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warning("[More Use Resources] Could not read Harmony patch owners. " + ex.Message);
        }
        foreignPatches = found;
        PathGridOwners = string.Join(", ", owners);
        bool axenInstalled = FindPack(AxenId, prefix: true) != null;
        axen = axenPatched ? ExternalFeatureState.Active
            : axenInstalled ? ExternalFeatureState.Inactive : ExternalFeatureState.Absent;
        nextPoll = 0;
    }

    /// <summary>Refreshes settings and live state about every two seconds. Returns true when something changed.</summary>
    internal bool Poll(long now, bool wowgagCompilesJit)
    {
        if (now < nextPoll) return false;
        nextPoll = now + 2000;
        ExternalFeatureState wowgag = ReadWowgag(out ExternalWorkerSetting? wowgagWorkers);
        ExternalFeatureState overclock = ReadOverclock(out ExternalWorkerSetting? overclockWorkers);
        bool busy = false;
        int poolWorkers = 0;
        try
        {
            // The engine created this pool long before play data finished loading.
            poolWorkers = WaylithJobRuntime.WorkerCount;
            busy = WaylithJobRuntime.HasPendingWork;
            SharedPoolReadable = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SharedPoolReadable = false;
        }
        var next = new ExternalParallelism(wowgag, overclock, axen, wowgagWorkers, overclockWorkers, busy, poolWorkers);
        WorkUnitYields yields = CompatibilityPolicy.ResolveYields(foreignPatches, launchXmlReuse, wowgagCompilesJit);
        bool changed = next != Parallelism || !yields.SameAs(Yields);
        Parallelism = next;
        Yields = yields;
        return changed;
    }

    /// <summary>true when the change is worth a log line: the busy flag of the shared pool flips constantly.</summary>
    internal static bool Notable(ExternalParallelism before, ExternalParallelism after) =>
        before with { SharedPoolBusy = false } != after with { SharedPoolBusy = false };

    private static VanillaWorkUnit[] UnitsDriving(MethodBase method, Type type)
    {
        if (type == typeof(GraphicData) || type == typeof(GraphicDatabase)
            || type == typeof(Graphic_Single) || type == typeof(Graphic_Multi))
            return GraphicUnits;
        if (type == typeof(Section) || type == typeof(MapDrawer)) return SectionUnits;
        if (typeof(MapDrawLayer).IsAssignableFrom(type))
            return method.Name is "DrawLayer" or "Regenerate" ? SectionUnits : NoUnits;
        return NoUnits;
    }

    private static readonly VanillaWorkUnit[] NoUnits = Array.Empty<VanillaWorkUnit>();
    private static readonly VanillaWorkUnit[] GraphicUnits = { VanillaWorkUnit.GraphicPrepare };
    private static readonly VanillaWorkUnit[] SectionUnits = { VanillaWorkUnit.SectionRegeneration };

    private static void Collect(IEnumerable<Patch>? patches, bool canReplace, VanillaWorkUnit[] units, bool isPathGrid,
        List<ForeignPatch> found, SortedSet<string> pathGridOwners, ref bool axenPatched)
    {
        if (patches == null) return;
        foreach (Patch patch in patches)
        {
            string owner = patch.owner ?? string.Empty;
            if (owner.Length == 0 || string.Equals(owner, ResourceRuntime.PackageId, StringComparison.OrdinalIgnoreCase)) continue;
            if (isPathGrid)
            {
                pathGridOwners.Add(owner);
                if (owner.StartsWith(AxenId, StringComparison.OrdinalIgnoreCase)) axenPatched = true;
            }
            // A prefix can only take the original's place when it returns a bool; a transpiler always can.
            bool replaces = canReplace && (patch.PatchMethod == null || patch.PatchMethod.ReturnType != typeof(void));
            foreach (VanillaWorkUnit unit in units) found.Add(new ForeignPatch(unit, owner, replaces));
        }
    }

    private static ExternalFeatureState ReadWowgag(out ExternalWorkerSetting? workers)
    {
        workers = null;
        ModContentPack? pack = FindPack(WowgagId, prefix: false);
        if (pack == null) return ExternalFeatureState.Absent;
        try
        {
            object? settings = WowgagSettings(pack);
            Type? parallel = FindType(pack, "GUIPerformancePatch.GppParallel");
            int? running = parallel?.GetProperty("WorkerCount", AnyStatic)?.GetValue(null) is int count && count > 0 ? count : null;
            int? configured = settings?.GetType().GetField("parWorkers", AnyInstance)?.GetValue(settings) is int value ? value : null;
            workers = new ExternalWorkerSetting("parWorkers", configured, running);
            if (settings == null) return ExternalFeatureState.Unknown;

            bool? anyEnabled = false;
            Type settingsType = settings.GetType();
            foreach (string option in WowgagParallelOptions)
            {
                if (settingsType.GetField(option, AnyInstance)?.GetValue(settings) is not bool enabled)
                {
                    anyEnabled = null;
                    break;
                }
                if (enabled) anyEnabled = true;
            }
            // 0 = not decided yet, 1 = stopped because of another mod, 2 = not stopped.
            bool? blocked = parallel?.GetField("foreignBlockedState", AnyStatic)?.GetValue(null) is int state && state != 0
                ? state == 1 : null;
            bool? attached = parallel?.GetField("Installed", AnyStatic)?.GetValue(null) is bool installed ? installed : null;
            return CompatibilityPolicy.ParallelState(true, anyEnabled, blocked, attached);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ExternalFeatureState.Unknown;
        }
    }

    private static ExternalFeatureState ReadOverclock(out ExternalWorkerSetting? workers)
    {
        workers = null;
        ModContentPack? pack = FindPack(OverclockId, prefix: false);
        if (pack == null) return ExternalFeatureState.Absent;
        try
        {
            int? configured = null;
            foreach (Mod handle in LoadedModManager.ModHandles)
            {
                if (handle.Content != pack || ModSettingsField?.GetValue(handle) is not { } settings) continue;
                if (settings.GetType().GetField("parallelThreads", AnyInstance)?.GetValue(settings) is int value)
                    configured = value;
            }
            // Overclock starts its pool once, when parallel pawn ticks are applied; the setting is read then.
            int? running = FindType(pack, "Overclock.Parallel.Worker")?.GetProperty("ThreadCount", AnyStatic)?.GetValue(null)
                is int count && count > 0 ? count : null;
            workers = new ExternalWorkerSetting("parallelThreads", configured, running);
            object? active = FindType(pack, "Overclock.Optimisations.ParallelPawnTicks")?.GetField("active", AnyStatic)?.GetValue(null);
            return active is bool on
                ? (on ? ExternalFeatureState.Active : ExternalFeatureState.Inactive)
                : ExternalFeatureState.Unknown;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ExternalFeatureState.Unknown;
        }
    }

    private static object? WowgagSettings(ModContentPack pack) =>
        FindType(pack, "GUIPerformancePatch.GppMod")?.GetProperty("Settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

    private static Type? FindType(ModContentPack pack, string name)
    {
        foreach (Assembly assembly in pack.assemblies.loadedAssemblies)
            if (assembly.GetType(name, throwOnError: false) is { } type) return type;
        return null;
    }

    private static ModContentPack? FindPack(string packageId, bool prefix)
    {
        foreach (ModContentPack pack in LoadedModManager.RunningModsListForReading)
        {
            string id = pack.PackageIdPlayerFacing ?? string.Empty;
            if (prefix ? id.StartsWith(packageId, StringComparison.OrdinalIgnoreCase)
                : string.Equals(id, packageId, StringComparison.OrdinalIgnoreCase))
                return pack;
        }
        return null;
    }
}
