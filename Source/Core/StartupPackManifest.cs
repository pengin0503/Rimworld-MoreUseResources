using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace MoreUseResources.Core;

/// <summary>Read-only startup hints for the launcher, written after the game establishes the active cache.</summary>
public sealed record StartupPackManifest(int Schema, string GameDirectory, string CacheDirectory,
    bool GpuEncode, string ModsConfigPath, string ModsConfigSha256, IReadOnlyList<StartupPackEntry> Packs,
    string? ShaderCacheDirectory = null, IReadOnlyList<StartupPackEntry>? ShaderCache = null)
{
    public const int MaxShaderCacheEntries = 512;
    public const string FileName = "MoreUseResources-StartupPacks.json";

    public static string? ReadConfigHash(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > 4 * 1024 * 1024) return null;
        string hash = Convert.ToHexString(SHA256.HashData(input));
        ModFileReads.Add(input.Position);
        return hash;
    }

    public static StartupPackWriteResult Save(string path, string gameDirectory, string cacheDirectory,
        string modsConfigPath, IReadOnlyList<string> sources, CancellationToken token, Func<bool>? mayPublish = null,
        IReadOnlyList<string>? mountedPacks = null, string? expectedConfigHash = null,
        IReadOnlyList<string>? shaderCacheFiles = null)
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

        // Compiled shader records are optional hints: all from one directory, exact engine names only.
        var shaders = new List<StartupPackEntry>();
        string? shaderDirectory = null;
        if (shaderCacheFiles != null)
        {
            foreach (string source in shaderCacheFiles)
            {
                if (token.IsCancellationRequested) return StartupPackWriteResult.Discarded;
                if (shaders.Count == MaxShaderCacheEntries) break;
                var file = new FileInfo(source);
                if (!file.Exists || file.Length <= 0 || (file.Attributes & FileAttributes.ReparsePoint) != 0
                    || !EnginePackFiles.IsShaderCacheRecord(file.Name) || file.DirectoryName == null) continue;
                shaderDirectory ??= file.DirectoryName;
                if (!string.Equals(shaderDirectory, file.DirectoryName, StringComparison.Ordinal)) continue;
                shaders.Add(new StartupPackEntry(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks.ToString(
                    System.Globalization.CultureInfo.InvariantCulture), false));
            }
        }

        if (token.IsCancellationRequested) return StartupPackWriteResult.Discarded;
        if (entries.Count == 0) return StartupPackWriteResult.Retry;
        string? configHash = ReadConfigHash(modsConfigPath);
        if (configHash == null || (expectedConfigHash != null && configHash != expectedConfigHash))
            return StartupPackWriteResult.Discarded;
        var manifest = new StartupPackManifest(1, Path.GetFullPath(gameDirectory), Path.GetFullPath(cacheDirectory),
            Path.GetFileName(cacheDirectory).EndsWith("-gpu", StringComparison.Ordinal), Path.GetFullPath(modsConfigPath),
            configHash, entries, shaders.Count == 0 ? null : shaderDirectory, shaders.Count == 0 ? null : shaders);
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
