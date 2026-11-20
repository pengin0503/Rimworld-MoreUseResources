using System;
using System.Reflection;
using MoreUseResources.Core;
using Verse;

namespace MoreUseResources;

internal static class WowgagCompatibility
{
    private const string PackageId = "wowgag.guiperformancepatch";
    private static readonly string[] JitOptions = { "jitProfileReplay", "afterLoadPrecompile", "precompilePatched" };

    internal static bool YieldJit()
    {
        foreach (ModContentPack pack in LoadedModManager.RunningModsListForReading)
        {
            if (!string.Equals(pack.PackageIdPlayerFacing, PackageId, StringComparison.OrdinalIgnoreCase)) continue;
            return HardwareWork.YieldJitToWowgag(true, ReadJitOptions(pack));
        }
        return HardwareWork.YieldJitToWowgag(false, null);
    }

    private static bool? ReadJitOptions(ModContentPack pack)
    {
        try
        {
            foreach (Assembly assembly in pack.assemblies.loadedAssemblies)
            {
                var settings = assembly.GetType("GUIPerformancePatch.GppMod", throwOnError: false)?
                    .GetProperty("Settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (settings == null) continue;
                Type type = settings.GetType();
                foreach (string option in JitOptions)
                {
                    if (type.GetField(option, BindingFlags.Public | BindingFlags.Instance)?.GetValue(settings) is not bool enabled)
                        return null;
                    if (enabled) return true;
                }
                return false;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An unknown version gets precedence instead of running a second JIT pass.
        }
        return null;
    }
}
