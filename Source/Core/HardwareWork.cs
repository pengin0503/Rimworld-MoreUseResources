using System;
using System.Collections.Generic;

namespace MoreUseResources.Core;

public static class HardwareWork
{
    public static bool MayPrepare(GamePhase phase, bool active, bool throttled, bool memoryOk) =>
        active && !throttled && memoryOk && phase is GamePhase.MainMenu or GamePhase.Paused;

    public static bool YieldJitToWowgag(bool loaded, bool? compiles) => loaded && compiles != false;

    public static bool HasFrameBudget(double elapsedMs, int budgetTenthsMs, float intensity) =>
        elapsedMs < budgetTenthsMs * 0.1 * intensity;

    public static bool CanImproveMeshFiltering(int mipCount, int anisotropy, bool repeat, bool filtered) =>
        mipCount > 1 && anisotropy is (1 or 2) && repeat && filtered;

    public static int TextureTarget(VramInfo memory, int normal, long reserve, bool boost, bool offloadUnderPressure) =>
        boost && offloadUnderPressure && memory.DriverReported
            && memory.UsageBytes < memory.CapBytes(ResourceSettings.MaxGpuTargetPercent, reserve)
            ? ResourceSettings.MaxGpuTargetPercent : normal;
}

/// <summary>Tracks a global engine option while this mod owns its current value.</summary>
public sealed class OwnedSetting<T>(Func<T> read, Action<T> write) where T : struct
{
    private bool owned;
    private bool displaced;
    private T original;
    private T applied;

    public bool Apply(T desired)
    {
        if (displaced) return false;
        var comparer = EqualityComparer<T>.Default;
        T current = read();
        if (owned && !comparer.Equals(current, applied))
        {
            owned = false;
            displaced = true;
            return false;
        }
        if (comparer.Equals(current, desired)) return true;
        if (!owned) original = current;
        write(desired);
        applied = desired;
        owned = true;
        return true;
    }

    public void Restore()
    {
        if (!owned) return;
        owned = false;
        if (EqualityComparer<T>.Default.Equals(read(), applied)) write(original);
        else displaced = true;
    }
}
