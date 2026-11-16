using MoreUseResources.Core;
using Verse;

namespace MoreUseResources;

internal sealed class ResourceModSettings : ModSettings
{
    private static readonly ResourceSettings Defaults = CreateDefaults();
    internal readonly ResourceSettings Value = CreateDefaults();

    private static ResourceSettings CreateDefaults() => new()
    {
        CpuMaxWorkers = 4,
        AllowWhenUnfocused = false,
        GpuEnabled = false,
        RamFileCacheMiB = 512,
        RamHoldCopies = false,
        RamReserveMiB = 1024,
        GpuFrameBudgetTenthMs = 10,
    };

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref Value.Enabled, "enabled", Defaults.Enabled);
        Scribe_Values.Look(ref Value.Preset, "preset", Defaults.Preset);
        Scribe_Values.Look(ref Value.AllowWhenUnfocused, "allowWhenUnfocused", Defaults.AllowWhenUnfocused);
        Scribe_Values.Look(ref Value.AllowInWorldView, "allowInWorldView", Defaults.AllowInWorldView);
        Scribe_Values.Look(ref Value.ReleaseWhenInactive, "releaseWhenInactive", Defaults.ReleaseWhenInactive);
        Scribe_Values.Look(ref Value.IdleBoost, "idleBoost", Defaults.IdleBoost);
        Scribe_Values.Look(ref Value.IdleSeconds, "idleSeconds", Defaults.IdleSeconds);
        Scribe_Values.Look(ref Value.FrameGuardMs, "frameGuardMs", Defaults.FrameGuardMs);

        Scribe_Values.Look(ref Value.CpuEnabled, "cpuEnabled", Defaults.CpuEnabled);
        Scribe_Values.Look(ref Value.CpuMaxWorkers, "cpuMaxWorkers", Defaults.CpuMaxWorkers);
        Scribe_Values.Look(ref Value.CpuSyntheticLoad, "cpuSyntheticLoad", Defaults.CpuSyntheticLoad);
        Scribe_Values.Look(ref Value.CpuSyntheticDutyPercent, "cpuSyntheticDutyPercent", Defaults.CpuSyntheticDutyPercent);

        Scribe_Values.Look(ref Value.RamEnabled, "ramEnabled", Defaults.RamEnabled);
        Scribe_Values.Look(ref Value.RamFileCache, "prefetchFiles", Defaults.RamFileCache);
        Scribe_Values.Look(ref Value.RamHoldCopies, "holdCopies", Defaults.RamHoldCopies);
        Scribe_Values.Look(ref Value.RamFileCacheMiB, "cacheMiB", Defaults.RamFileCacheMiB);
        Scribe_Values.Look(ref Value.RamMinFreeMiB, "keepFreeMiB", Defaults.RamMinFreeMiB);
        Scribe_Values.Look(ref Value.RamReserve, "ramReserve", Defaults.RamReserve);
        Scribe_Values.Look(ref Value.RamReserveMiB, "reserveMiB", Defaults.RamReserveMiB);

        Scribe_Values.Look(ref Value.GpuEnabled, "gpuPrewarm", Defaults.GpuEnabled);
        Scribe_Values.Look(ref Value.GpuTargetPercent, "vramTargetPercent", Defaults.GpuTargetPercent);
        Scribe_Values.Look(ref Value.GpuKeepFreeMiB, "vramKeepFreeMiB", Defaults.GpuKeepFreeMiB);
        Scribe_Values.Look(ref Value.GpuFrameBudgetTenthMs, "gpuFrameBudgetTenthMs", Defaults.GpuFrameBudgetTenthMs);

        for (int i = 0; i < ResourceSettings.PhaseCount; i++)
        {
            string phase = ((GamePhase)i).ToString();
            Scribe_Values.Look(ref Value.CpuPhases[i], "cpu" + phase, Defaults.CpuPhases[i]);
            Scribe_Values.Look(ref Value.RamPhases[i], "ram" + phase, Defaults.RamPhases[i]);
            Scribe_Values.Look(ref Value.GpuPhases[i], "gpu" + phase, Defaults.GpuPhases[i]);
            Scribe_Values.Look(ref Value.PhaseIntensity[i], "intensity" + phase, Defaults.PhaseIntensity[i]);
        }
        Value.Clamp();
    }
}
