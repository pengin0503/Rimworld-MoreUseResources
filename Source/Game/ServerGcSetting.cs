using System;
using System.IO;
using System.Reflection;
using System.Runtime;
using MoreUseResources.Core;
using Verse;

namespace MoreUseResources;

/// <summary>
/// Keeps the game's start-up file asking for the server GC while the setting is on. The GC kind is fixed when the
/// process starts, so the file is checked at every launch (a game update can put the original file back) and again
/// whenever the setting changes; the result shows from the next launch.
/// </summary>
internal static class ServerGcSetting
{
    private static bool? applied;

    internal static ServerGcFileResult Last { get; private set; }

    /// <summary>The file next to RimWorld.dll that the game's host reads before the runtime starts.</summary>
    internal static string ConfigPath =>
        Path.Combine(AppContext.BaseDirectory, (Assembly.GetEntryAssembly()?.GetName().Name ?? "RimWorld") + ".runtimeconfig.json");

    internal static void Apply(bool serverGc)
    {
        if (applied == serverGc) return;
        applied = serverGc;
        string path = "RimWorld.runtimeconfig.json";
        try
        {
            path = ConfigPath;
            Last = RuntimeConfigGc.Apply(path, serverGc);
        }
        catch (Exception ex)
        {
            // This runs in the mod's constructor, where an exception would stop the whole mod from loading.
            Last = new ServerGcFileResult(ServerGcFileState.WriteFailed, ex.GetType().Name + ": " + ex.Message);
        }
        switch (Last.State)
        {
            case ServerGcFileState.Added:
                Log.Message("[More Use Resources] Added " + RuntimeConfigGc.ServerKey + " to " + path + ". The server GC starts with the next launch.");
                break;
            case ServerGcFileState.Removed:
                Log.Message("[More Use Resources] Removed this mod's " + RuntimeConfigGc.ServerKey + " from " + path + ". The next launch uses the game's default GC.");
                break;
            case ServerGcFileState.Unreadable:
            case ServerGcFileState.WriteFailed:
                Log.Warning("[More Use Resources] Left " + path + " as it was (" + Last.State + "): " + Last.Error);
                break;
        }
    }

    /// <summary>
    /// What set the GC kind of this process, in the order the runtime reads them. The environment is read now, not
    /// at start-up, so a value that disagrees with the GC in use is reported as such rather than as the cause.
    /// </summary>
    internal static string LaunchSourceKey(out string detail)
    {
        bool server = GCSettings.IsServerGC;
        foreach (string name in new[] { "DOTNET_gcServer", "COMPlus_gcServer" })
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
            {
                detail = name + "=" + value;
                // The runtime reads this variable as a hexadecimal number.
                bool on = uint.TryParse(value.Trim(), System.Globalization.NumberStyles.HexNumber, null, out uint number) && number != 0;
                return on == server ? "MUR_GcSourceEnvironment" : "MUR_GcSourceMismatch";
            }
        }
        if (AppContext.GetData(RuntimeConfigGc.ServerKey) is string file && file.Length > 0)
        {
            detail = RuntimeConfigGc.ServerKey + "=" + file;
            return (file == "true") == server ? "MUR_GcSourceFile" : "MUR_GcSourceMismatch";
        }
        detail = "";
        return server ? "MUR_GcSourceUnknown" : "MUR_GcSourceDefault";
    }

    internal static string LaunchGc => GCSettings.IsServerGC ? "Server" : "Workstation";
}
