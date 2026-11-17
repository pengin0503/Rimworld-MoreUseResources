using System;
using System.Collections.Generic;

namespace MoreUseResources.Core;

/// <summary>What is known about an optional feature of another mod. Unknown is never read as "off".</summary>
public enum ExternalFeatureState
{
    Absent = 0,
    Inactive = 1,
    Active = 2,
    Unknown = 3,
}

/// <summary>
/// One piece of vanilla work this mod can advance. Ownership is decided per unit: when another
/// mod handles the same method or cache, only that unit is left to it.
/// </summary>
public enum VanillaWorkUnit
{
    XmlLoadStages = 0,
    PatchedXmlCache = 1,
    JitPrewarm = 2,
    GraphicPrepare = 3,
    /// <summary>Not driven by this mod: the game converts and queues a section mesh when it rebuilds it.</summary>
    MeshUpload = 4,
    SectionRegeneration = 5,
    TextureUpload = 6,
    FilePrefetch = 7,
    ShaderCachePrefetch = 8,
    AssetAudit = 9,
}

/// <summary>A Harmony patch another mod registered on a method one of this mod's work units drives.</summary>
/// <param name="Replaces">true for a transpiler or a prefix that can skip the original. Timers and
/// other observers leave the original result in place and do not make the work a duplicate.</param>
public readonly record struct ForeignPatch(VanillaWorkUnit Unit, string Owner, bool Replaces);

/// <summary>A worker setting of another mod: the stored value, never an estimate of what runs.</summary>
/// <param name="Configured">null when the setting could not be read with the expected type.</param>
/// <param name="Running">null unless the other mod exposes its live worker count.</param>
public readonly record struct ExternalWorkerSetting(string Name, int? Configured, int? Running)
{
    /// <summary>Both known mods use 0 for "choose automatically".</summary>
    public bool Automatic => Configured == 0;
}

/// <summary>
/// One consistent reading of the other performance mods, taken on the game thread. It is shown in
/// the status panel and decides which duplicate work units are left to their owner. It is
/// deliberately not an input to this mod's worker count.
/// </summary>
public sealed record ExternalParallelism(
    ExternalFeatureState Wowgag, ExternalFeatureState Overclock, ExternalFeatureState AxenPathGrid,
    ExternalWorkerSetting? WowgagWorkers = null, ExternalWorkerSetting? OverclockWorkers = null,
    bool SharedPoolBusy = false, int SharedPoolWorkers = 0)
{
    public static readonly ExternalParallelism None =
        new(ExternalFeatureState.Absent, ExternalFeatureState.Absent, ExternalFeatureState.Absent);

    public bool AnyActiveOrUnknown =>
        Wowgag is ExternalFeatureState.Active or ExternalFeatureState.Unknown
        || Overclock is ExternalFeatureState.Active or ExternalFeatureState.Unknown
        || AxenPathGrid is ExternalFeatureState.Active or ExternalFeatureState.Unknown;
}

/// <summary>Immutable result of the ownership decision: which units are left to whom.</summary>
public sealed class WorkUnitYields
{
    public static readonly WorkUnitYields None = new(new Dictionary<VanillaWorkUnit, string>());
    private readonly Dictionary<VanillaWorkUnit, string> owners;

    internal WorkUnitYields(Dictionary<VanillaWorkUnit, string> owners) => this.owners = owners;

    public int Count => owners.Count;
    public bool IsYielded(VanillaWorkUnit unit) => owners.ContainsKey(unit);
    public string? OwnerOf(VanillaWorkUnit unit) => owners.TryGetValue(unit, out string? owner) ? owner : null;

    public IEnumerable<KeyValuePair<VanillaWorkUnit, string>> All()
    {
        var keys = new List<VanillaWorkUnit>(owners.Keys);
        keys.Sort();
        foreach (VanillaWorkUnit unit in keys) yield return new KeyValuePair<VanillaWorkUnit, string>(unit, owners[unit]);
    }

    public bool SameAs(WorkUnitYields other)
    {
        if (other.owners.Count != owners.Count) return false;
        foreach (var pair in owners)
            if (!other.owners.TryGetValue(pair.Key, out string? owner) || owner != pair.Value) return false;
        return true;
    }
}

public static class CompatibilityPolicy
{
    public const string WowgagOwner = "WOWGAG";

    /// <summary>WOWGAG's XML reuse replaces the load stages this mod would time and cache.</summary>
    public static bool YieldXmlToWowgag(ExternalFeatureState reuse) =>
        reuse is ExternalFeatureState.Active or ExternalFeatureState.Unknown;

    /// <summary>
    /// The worker count this mod may use. Other mods' parallel features and a busy shared engine
    /// pool never lower it: the user's own limit, the phase intensity, the adaptive control and
    /// the frame guard are the only reductions, and they are applied by the caller.
    /// </summary>
    public static int LimitWorkers(int requested, GamePhase phase, ExternalParallelism external)
    {
        _ = phase;
        _ = external;
        return Math.Max(0, requested);
    }

    /// <summary>
    /// Leaves exactly the duplicated units to their owner. A unit without a replacing foreign patch
    /// keeps running whichever other mods are installed.
    /// </summary>
    public static WorkUnitYields ResolveYields(IEnumerable<ForeignPatch>? patches,
        ExternalFeatureState wowgagXmlReuse = ExternalFeatureState.Absent, bool wowgagCompilesJit = false)
    {
        var owners = new Dictionary<VanillaWorkUnit, string>();
        if (patches != null)
        {
            foreach (ForeignPatch patch in patches)
            {
                if (!patch.Replaces || string.IsNullOrEmpty(patch.Owner)) continue;
                if (!owners.ContainsKey(patch.Unit)) owners.Add(patch.Unit, patch.Owner);
            }
        }
        if (YieldXmlToWowgag(wowgagXmlReuse))
        {
            owners[VanillaWorkUnit.XmlLoadStages] = WowgagOwner;
            owners[VanillaWorkUnit.PatchedXmlCache] = WowgagOwner;
        }
        if (wowgagCompilesJit) owners[VanillaWorkUnit.JitPrewarm] = WowgagOwner;
        return owners.Count == 0 ? WorkUnitYields.None : new WorkUnitYields(owners);
    }

    /// <summary>
    /// Resolves another mod's parallel feature from its own switches. Enabled but stopped by that
    /// mod's own guard is Inactive; enabled without a confirmed installation stays Unknown.
    /// </summary>
    public static ExternalFeatureState ParallelState(bool installed, bool? anyEnabled, bool? blocked, bool? attached)
    {
        if (!installed) return ExternalFeatureState.Absent;
        if (anyEnabled == null) return ExternalFeatureState.Unknown;
        if (anyEnabled == false || blocked == true) return ExternalFeatureState.Inactive;
        if (attached == false) return ExternalFeatureState.Inactive;
        return blocked == false && attached == true ? ExternalFeatureState.Active : ExternalFeatureState.Unknown;
    }
}

/// <summary>
/// Shares one per-frame preparation budget between independent work units. A unit that is left to
/// another mod, switched off or finished gives its turns to the units that still have work, so the
/// budget is spent on other vanilla work instead of being dropped.
/// </summary>
public sealed class PreparationRotation
{
    private readonly VanillaWorkUnit[] units;
    private int next;

    public PreparationRotation(params VanillaWorkUnit[] units)
    {
        if (units == null || units.Length == 0) throw new ArgumentException("At least one unit is required.", nameof(units));
        this.units = (VanillaWorkUnit[])units.Clone();
    }

    /// <summary>Returns the next unit with work, or null when none may run.</summary>
    public VanillaWorkUnit? Next(Func<VanillaWorkUnit, bool> hasWork, WorkUnitYields yields)
    {
        for (int i = 0; i < units.Length; i++)
        {
            VanillaWorkUnit candidate = units[(next + i) % units.Length];
            if (yields.IsYielded(candidate) || !hasWork(candidate)) continue;
            next = (next + i + 1) % units.Length;
            return candidate;
        }
        return null;
    }
}
