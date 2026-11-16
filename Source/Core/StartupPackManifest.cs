using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace MoreUseResources.Core;

/// <summary>Read-only startup hints for the launcher, written after the game establishes the active cache.</summary>
public sealed record StartupPackManifest(int Schema, string GameDirectory, string CacheDirectory,
    bool GpuEncode, string ModsConfigPath, string ModsConfigSha256, IReadOnlyList<StartupPackEntry> Packs)
{
    public const string FileName = "MoreUseResources-StartupPacks.json";

    public static string? ReadConfigHash(string path)
    {
        using var input = File.OpenRead(path);
        return input.Length > 4 * 1024 * 1024 ? null : Convert.ToHexString(SHA256.HashData(input));
    }

    public static void Save(string path, string gameDirectory, string cacheDirectory,
        string modsConfigPath, IReadOnlyList<string> sources, CancellationToken token, Func<bool>? mayPublish = null,
        IReadOnlyList<string>? mountedPacks = null, string? expectedConfigHash = null)
    {
        var entries = new List<StartupPackEntry>();
        var mounted = new HashSet<string>(mountedPacks ?? Array.Empty<string>(),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string source in sources)
        {
            if (token.IsCancellationRequested) return;
            if (entries.Count == 512) break;
            var file = new FileInfo(source);
            if (file.Exists && file.Length > 0 && (file.Attributes & FileAttributes.ReparsePoint) == 0)
                entries.Add(new StartupPackEntry(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                    mounted.Contains(file.FullName) || string.Equals(file.Name, EnginePackFiles.AssetPackFileName,
                        StringComparison.OrdinalIgnoreCase)));
        }

        if (entries.Count == 0 || token.IsCancellationRequested) return;
        string? configHash = ReadConfigHash(modsConfigPath);
        if (configHash == null || (expectedConfigHash != null && configHash != expectedConfigHash)) return;
        var manifest = new StartupPackManifest(1, Path.GetFullPath(gameDirectory), Path.GetFullPath(cacheDirectory),
            Path.GetFileName(cacheDirectory).EndsWith("-gpu", StringComparison.Ordinal), Path.GetFullPath(modsConfigPath),
            configHash, entries);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream output = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(output, manifest);
            if (!token.IsCancellationRequested && (mayPublish?.Invoke() ?? true))
                File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}

public sealed record StartupPackEntry(string Path, long Bytes, string LastWriteUtcTicks, bool MountedAssetPack);
