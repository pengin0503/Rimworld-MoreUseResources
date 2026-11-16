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

    public static StartupPackWriteResult Save(string path, string gameDirectory, string cacheDirectory,
        string modsConfigPath, IReadOnlyList<string> sources, CancellationToken token, Func<bool>? mayPublish = null,
        IReadOnlyList<string>? mountedPacks = null, string? expectedConfigHash = null)
    {
        var entries = new List<StartupPackEntry>();
        var mounted = new HashSet<string>(mountedPacks ?? Array.Empty<string>(),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string source in sources)
        {
            if (token.IsCancellationRequested) return StartupPackWriteResult.Discarded;
            if (entries.Count == 512) break;
            var file = new FileInfo(source);
            if (file.Exists && file.Length > 0 && (file.Attributes & FileAttributes.ReparsePoint) == 0)
                entries.Add(new StartupPackEntry(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                    mounted.Contains(file.FullName) || string.Equals(file.Name, EnginePackFiles.AssetPackFileName,
                        StringComparison.OrdinalIgnoreCase)));
        }

        if (token.IsCancellationRequested) return StartupPackWriteResult.Discarded;
        if (entries.Count == 0) return StartupPackWriteResult.Retry;
        string? configHash = ReadConfigHash(modsConfigPath);
        if (configHash == null || (expectedConfigHash != null && configHash != expectedConfigHash))
            return StartupPackWriteResult.Discarded;
        var manifest = new StartupPackManifest(1, Path.GetFullPath(gameDirectory), Path.GetFullPath(cacheDirectory),
            Path.GetFileName(cacheDirectory).EndsWith("-gpu", StringComparison.Ordinal), Path.GetFullPath(modsConfigPath),
            configHash, entries);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream output = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(output, manifest);
            if (token.IsCancellationRequested || !(mayPublish?.Invoke() ?? true))
                return StartupPackWriteResult.Discarded;
            File.Move(temporary, path, overwrite: true);
            return StartupPackWriteResult.Written;
        }
        finally { File.Delete(temporary); }
    }
}

public sealed record StartupPackEntry(string Path, long Bytes, string LastWriteUtcTicks, bool MountedAssetPack);

public enum StartupPackWriteResult
{
    Written,
    Retry,
    Discarded,
}

public sealed class StartupManifestRetry
{
    private const int MaxAttempts = 3;
    private const long RetryDelayMs = 5000;
    private readonly object gate = new();
    private int attempts;
    private long nextAttempt;
    private bool inFlight;
    private bool completed;

    public bool TryStart(long now)
    {
        lock (gate)
        {
            if (completed || inFlight || attempts >= MaxAttempts || (attempts > 0 && now < nextAttempt))
                return false;
            attempts++;
            inFlight = true;
            nextAttempt = now + RetryDelayMs;
            return true;
        }
    }

    public void Complete(bool retry)
    {
        lock (gate)
        {
            inFlight = false;
            completed = !retry;
        }
    }
}
