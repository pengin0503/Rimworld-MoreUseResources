using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MoreUseResources.Core;

/// <summary>Identifies a texture by the mod that provides it and its key inside that mod's content.</summary>
public readonly record struct TextureDemandKey(string PackageId, string ContentKey);

public readonly record struct TextureDemandSample(TextureDemandKey Key, int Count);

/// <summary>
/// Remembers which textures the game really had to load again while playing, so the next launch
/// can prepare those first. A plain optional cache file: it is never part of a save, never holds an
/// absolute path, and anything unreadable simply means "no history".
/// </summary>
public static class TextureDemandHistory
{
    public const string FileName = "MoreUseResources-TextureDemand.json";
    public const int Schema = 1;
    public const int MaxEntries = 512;
    public const int MaxFileBytes = 1024 * 1024;
    private const int MaxKeyLength = 512;

    private sealed record Document(int Schema, string Fingerprint, List<Entry> Entries);
    private sealed record Entry(string PackageId, string ContentKey, int Count);

    /// <summary>Returns the stored ranking, best first, or an empty list when it does not apply.</summary>
    public static IReadOnlyList<TextureDemandSample> Load(string path, string fingerprint)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length <= 0 || file.Length > MaxFileBytes) return Array.Empty<TextureDemandSample>();
            Document? document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path));
            if (document == null || document.Schema != Schema || document.Entries == null
                || !string.Equals(document.Fingerprint, fingerprint, StringComparison.Ordinal))
                return Array.Empty<TextureDemandSample>();
            var samples = new List<TextureDemandSample>();
            foreach (Entry? entry in document.Entries)
                if (entry != null) samples.Add(new TextureDemandSample(new TextureDemandKey(entry.PackageId, entry.ContentKey), entry.Count));
            return Normalize(samples);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or NotSupportedException or ArgumentException)
        {
            return Array.Empty<TextureDemandSample>();
        }
    }

    /// <summary>Replaces the file through a temporary file in the same folder.</summary>
    public static void Save(string path, string fingerprint, IReadOnlyList<TextureDemandSample> ranked)
    {
        var entries = new List<Entry>();
        foreach (TextureDemandSample sample in Normalize(ranked))
            entries.Add(new Entry(sample.Key.PackageId, sample.Key.ContentKey, sample.Count));
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new Document(Schema, fingerprint, entries));
        // Drop the least demanded entries until the file fits its bound.
        while (payload.Length > MaxFileBytes && entries.Count > 0)
        {
            entries.RemoveRange(entries.Count / 2, entries.Count - entries.Count / 2);
            payload = JsonSerializer.SerializeToUtf8Bytes(new Document(Schema, fingerprint, entries));
        }

        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream output = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                output.Write(payload, 0, payload.Length);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    /// <summary>
    /// Combines this session's ranking with the stored one, so a short session does not erase what
    /// earlier sessions learned. A texture not seen again keeps half of its stored count, which lets
    /// it fade out over a few sessions instead of staying forever.
    /// </summary>
    public static IReadOnlyList<TextureDemandSample> Merge(IReadOnlyList<TextureDemandSample> session,
        IReadOnlyList<TextureDemandSample>? previous)
    {
        var combined = new List<TextureDemandSample>(session);
        if (previous != null)
        {
            var seen = new HashSet<TextureDemandKey>();
            foreach (TextureDemandSample sample in session) seen.Add(sample.Key);
            foreach (TextureDemandSample sample in previous)
                if (!seen.Contains(sample.Key)) combined.Add(sample with { Count = sample.Count / 2 });
        }
        return Normalize(combined);
    }

    /// <summary>Removes invalid and duplicate keys, orders by demand and applies the entry limit.</summary>
    public static IReadOnlyList<TextureDemandSample> Normalize(IReadOnlyList<TextureDemandSample> samples)
    {
        var seen = new HashSet<TextureDemandKey>();
        var valid = new List<(TextureDemandSample Sample, int Order)>();
        for (int i = 0; i < samples.Count; i++)
        {
            TextureDemandSample sample = samples[i];
            if (sample.Count <= 0 || !IsValidKey(sample.Key) || !seen.Add(sample.Key)) continue;
            valid.Add((sample, i));
        }
        valid.Sort((a, b) =>
        {
            int byCount = b.Sample.Count.CompareTo(a.Sample.Count);
            return byCount != 0 ? byCount : a.Order.CompareTo(b.Order);
        });
        var result = new List<TextureDemandSample>(Math.Min(valid.Count, MaxEntries));
        for (int i = 0; i < valid.Count && i < MaxEntries; i++) result.Add(valid[i].Sample);
        return result;
    }

    /// <summary>A key is a mod id plus a relative content key. Anything that looks like a file-system path is refused.</summary>
    public static bool IsValidKey(TextureDemandKey key)
    {
        if (string.IsNullOrWhiteSpace(key.PackageId) || string.IsNullOrWhiteSpace(key.ContentKey)) return false;
        if (key.PackageId.Length > MaxKeyLength || key.ContentKey.Length > MaxKeyLength) return false;
        string content = key.ContentKey;
        if (content[0] == '/' || content[0] == '\\' || content.Contains(':') || content.Contains("..")) return false;
        foreach (char c in key.PackageId)
            if (c == '/' || c == '\\' || c == ':' || char.IsControl(c)) return false;
        foreach (char c in content)
            if (char.IsControl(c)) return false;
        return true;
    }

    /// <summary>
    /// Hashes everything that decides which texture a key resolves to: the ordered mod list, the
    /// game build, the engine's cache layout and the packs it reads. Pack order is normalised; the
    /// mod order is kept because a later mod can replace an earlier mod's texture of the same name.
    /// </summary>
    public static string Fingerprint(IReadOnlyList<string> orderedMods, string gameBuild, string cacheLayout,
        IReadOnlyList<(string Identity, long Bytes, long WriteTicks)> packs)
    {
        var text = new StringBuilder();
        text.Append("v1\n").Append(gameBuild).Append('\n').Append(cacheLayout).Append('\n');
        foreach (string mod in orderedMods) text.Append("m:").Append(mod).Append('\n');
        var sorted = new List<(string Identity, long Bytes, long WriteTicks)>(packs);
        sorted.Sort((a, b) => StringComparer.Ordinal.Compare(a.Identity, b.Identity));
        foreach (var pack in sorted)
            text.Append("p:").Append(pack.Identity).Append('|').Append(pack.Bytes).Append('|').Append(pack.WriteTicks).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>A stable stand-in for a pack path that does not reveal where the user keeps their files.</summary>
    public static string PathIdentity(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path)))).Substring(0, 16)
        + ":" + Path.GetFileName(path);
}

/// <summary>Decides when the observed ranking is worth writing: real demand, changed, and not too often.</summary>
public sealed class TextureDemandSaveSchedule
{
    public const long MinIntervalMs = 60_000;
    private long lastSavedAt = long.MinValue;
    private string? lastSignature;
    private bool inFlight;
    private readonly object gate = new();

    public bool TryBegin(long now, IReadOnlyList<TextureDemandSample> ranked)
    {
        if (ranked.Count == 0) return false;
        string signature = Signature(ranked);
        lock (gate)
        {
            if (inFlight) return false;
            if (lastSavedAt != long.MinValue && now - lastSavedAt < MinIntervalMs) return false;
            if (signature == lastSignature) return false;
            inFlight = true;
            lastSignature = signature;
            lastSavedAt = now;
            return true;
        }
    }

    /// <summary>Safe to call from the worker that wrote the file.</summary>
    public void Complete(bool written)
    {
        lock (gate)
        {
            inFlight = false;
            if (!written) lastSignature = null;
        }
    }

    private static string Signature(IReadOnlyList<TextureDemandSample> ranked)
    {
        var text = new StringBuilder();
        foreach (TextureDemandSample sample in ranked)
            text.Append(sample.Key.PackageId).Append('\u001f').Append(sample.Key.ContentKey).Append('\u001f').Append(sample.Count).Append('\n');
        return text.ToString();
    }
}
