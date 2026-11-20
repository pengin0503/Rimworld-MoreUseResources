using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using MoreUseResources.Core;

namespace MoreUseResources.Benchmarks.Data;

/// <summary>A mod (or official content) root and the folders RimWorld would load from it, highest priority first.</summary>
public sealed record ModRoot(string Path, IReadOnlyList<string> LoadFolders, bool Official);

/// <summary>
/// The input files of a run. File lists come from the mod's own <c>GameAssetFiles.Discover</c> and
/// <c>EnginePackFiles.Discover</c>, so the benchmark visits exactly the files the mod would visit for
/// the same load folders. Only the load-folder resolution is the benchmark's: it follows RimWorld's
/// loadFolders.xml rule for v1.7 and otherwise uses "1.7", "Common" and the root.
/// </summary>
public sealed class Dataset
{
    public const string GameVersionFolder = "1.7";
    private static readonly string[] OfficialOrder = { "Core", "Royalty", "Ideology", "Biotech", "Anomaly", "Odyssey" };

    public required string Kind { get; init; }
    public required IReadOnlyList<string> DataRoots { get; init; }
    public required IReadOnlyList<ModRoot> Mods { get; init; }
    public required IReadOnlyList<string> LoadFolders { get; init; }
    /// <summary>Every file the mod's discovery returned, in its order.</summary>
    public required IReadOnlyList<string> DiscoveredFiles { get; init; }
    /// <summary>The files used by component scenarios after --max-files / --max-mib, in discovery order.</summary>
    public required IReadOnlyList<string> Files { get; init; }
    public required IReadOnlyList<string> Packs { get; init; }
    public required long FileBytes { get; init; }
    public required long PackBytes { get; init; }
    public required IReadOnlyDictionary<string, (int Count, long Bytes)> ByExtension { get; init; }
    public required string ShapeFingerprint { get; init; }
    public string? ContentFingerprint { get; init; }
    public string? GeneratorDescription { get; init; }

    public bool Subset => Files.Count < DiscoveredFiles.Count;

    /// <summary>Source/Core/GameAssetFiles.cs: MaxFiles.</summary>
    public const int DiscoveryLimit = 50_000;

    public IEnumerable<string> FilesWithExtension(params string[] extensions) =>
        Files.Where(f => extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Path relative to its data root, prefixed with the root index, for result files.</summary>
    public string Relative(string path)
    {
        for (int i = 0; i < DataRoots.Count; i++)
        {
            string rel = System.IO.Path.GetRelativePath(DataRoots[i], path);
            if (!rel.StartsWith("..", StringComparison.Ordinal) && !System.IO.Path.IsPathRooted(rel))
                return "d" + i + "/" + rel.Replace('\\', '/');
        }
        return System.IO.Path.GetFileName(path);
    }

    public Dictionary<string, object?> Describe() => new()
    {
        ["kind"] = Kind,
        ["generator"] = GeneratorDescription,
        ["mod_roots"] = Mods.Count,
        ["official_roots"] = Mods.Count(m => m.Official),
        ["load_folders"] = LoadFolders.Count,
        ["discovered_files"] = DiscoveredFiles.Count,
        // GameAssetFiles stops at this many files; beyond it the mod neither prefetches nor indexes.
        ["discovery_limit_reached"] = DiscoveredFiles.Count >= DiscoveryLimit,
        ["mods_with_older_version_folders_only"] = Mods.Count(m => OlderVersionFoldersOnly(m.Path)),
        ["selected_files"] = Files.Count,
        ["selected_mib"] = Math.Round(FileBytes / 1048576.0, 2),
        ["subset"] = Subset,
        ["packs"] = Packs.Count,
        ["pack_mib"] = Math.Round(PackBytes / 1048576.0, 2),
        ["by_extension"] = ByExtension.OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(p => p.Key, p => (object)new Dictionary<string, object> { ["files"] = p.Value.Count, ["mib"] = Math.Round(p.Value.Bytes / 1048576.0, 2) }),
        ["shape_fingerprint"] = ShapeFingerprint,
        ["content_fingerprint"] = ContentFingerprint,
    };

    public static Dataset Load(IReadOnlyList<string> dataRoots, IReadOnlyList<string> extraPacks, int maxFiles, long maxBytes,
        bool fingerprintContent, string kind, string? generator = null, CancellationToken token = default)
    {
        var mods = new List<ModRoot>();
        foreach (string root in dataRoots)
            foreach (string mod in FindModRoots(root))
                mods.Add(new ModRoot(mod, ResolveLoadFolders(mod), IsOfficial(mod)));
        mods = mods
            .GroupBy(m => m.Path, StringComparer.Ordinal).Select(g => g.First())
            .OrderBy(m => m.Official ? OfficialIndex(m.Path) : 100)
            .ThenBy(m => m.Path, StringComparer.Ordinal)
            .ToList();
        var folders = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ModRoot m in mods)
            foreach (string f in m.LoadFolders)
                if (seen.Add(f)) folders.Add(f);

        List<string> discovered = GameAssetFiles.Discover(folders, token, () => true);
        var extra = new List<string>(extraPacks);
        foreach (string root in dataRoots)
            foreach (string candidate in new[] { System.IO.Path.Combine(root, EnginePackFiles.AssetPackFileName),
                         System.IO.Path.Combine(root, "Game", EnginePackFiles.AssetPackFileName) })
                if (File.Exists(candidate)) extra.Add(candidate);
        List<string> packs = EnginePackFiles.Discover(null, mods.Select(m => m.Path).ToList(), folders, extra, token);

        List<string> selected = SelectSubset(discovered, dataRoots, maxFiles, maxBytes);
        long bytes = 0;
        var byExt = new Dictionary<string, (int, long)>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in selected)
        {
            long len = SafeLength(f);
            bytes += len;
            string ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
            byExt.TryGetValue(ext, out var c);
            byExt[ext] = (c.Item1 + 1, c.Item2 + len);
        }
        long packBytes = packs.Sum(SafeLength);
        var fingerprinted = selected.Concat(packs).ToList();
        return new Dataset
        {
            Kind = kind,
            DataRoots = dataRoots,
            Mods = mods,
            LoadFolders = folders,
            DiscoveredFiles = discovered,
            Files = selected,
            Packs = packs,
            FileBytes = bytes,
            PackBytes = packBytes,
            ByExtension = byExt,
            GeneratorDescription = generator,
            ShapeFingerprint = Fingerprint(dataRoots, fingerprinted, content: false, token),
            ContentFingerprint = fingerprintContent ? Fingerprint(dataRoots, fingerprinted, content: true, token) : null,
        };
    }

    /// <summary>
    /// When the input exceeds the limits, files are chosen in the order of the SHA-256 of their
    /// relative path. The choice is stable for the same data and spreads over all mods and types,
    /// instead of taking only the Defs that discovery visits first. The result keeps discovery order.
    /// </summary>
    public static List<string> SelectSubset(IReadOnlyList<string> discovered, IReadOnlyList<string> roots, int maxFiles, long maxBytes)
    {
        long total = 0;
        foreach (string f in discovered) total += SafeLength(f);
        if (discovered.Count <= maxFiles && total <= maxBytes) return new List<string>(discovered);
        var keyed = discovered.Select((path, index) => (path, index, key: StableKey(RelativeTo(roots, path)))).ToList();
        keyed.Sort((a, b) => string.CompareOrdinal(a.key, b.key));
        var chosen = new List<(string path, int index)>();
        long bytes = 0;
        foreach (var item in keyed)
        {
            if (chosen.Count >= maxFiles) break;
            long len = SafeLength(item.path);
            if (bytes + len > maxBytes) continue;
            bytes += len;
            chosen.Add((item.path, item.index));
        }
        chosen.Sort((a, b) => a.index.CompareTo(b.index));
        return chosen.Select(c => c.path).ToList();
    }

    private static string RelativeTo(IReadOnlyList<string> roots, string path)
    {
        foreach (string root in roots)
        {
            string rel = System.IO.Path.GetRelativePath(root, path);
            if (!rel.StartsWith("..", StringComparison.Ordinal)) return rel.Replace('\\', '/');
        }
        return path;
    }

    private static string StableKey(string relative) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(relative)));

    public static IEnumerable<string> FindModRoots(string dataRoot)
    {
        if (!Directory.Exists(dataRoot)) yield break;
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((dataRoot, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            if (File.Exists(System.IO.Path.Combine(dir, "About", "About.xml")))
            {
                yield return System.IO.Path.GetFullPath(dir);
                continue;
            }
            if (depth >= 4) continue;
            string[] children;
            try { children = Directory.GetDirectories(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            Array.Sort(children, StringComparer.Ordinal);
            for (int i = children.Length - 1; i >= 0; i--)
            {
                string name = System.IO.Path.GetFileName(children[i]);
                if (name.StartsWith('.') || name is "bin" or "obj") continue;
                stack.Push((children[i], depth + 1));
            }
        }
    }

    /// <summary>RimWorld's v1.7 load folders: loadFolders.xml when it lists v1.7, otherwise 1.7, Common and the root.</summary>
    public static List<string> ResolveLoadFolders(string modRoot)
    {
        var result = new List<string>();
        string file = System.IO.Path.Combine(modRoot, "loadFolders.xml");
        if (File.Exists(file))
        {
            try
            {
                var doc = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    doc.Load(reader);
                XmlNode? section = doc.DocumentElement?.SelectSingleNode("v" + GameVersionFolder);
                if (section != null)
                {
                    foreach (XmlNode li in section.SelectNodes("li")!)
                    {
                        string rel = li.InnerText.Trim().Trim('/', '\\');
                        string path = rel.Length == 0 ? modRoot : System.IO.Path.GetFullPath(System.IO.Path.Combine(modRoot, rel));
                        if (Directory.Exists(path) && !result.Contains(path)) result.Add(path);
                    }
                    // RimWorld lists the folders in ascending priority; the mod captures them highest first.
                    result.Reverse();
                    if (result.Count > 0) return result;
                }
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException or System.Xml.XPath.XPathException) { }
        }
        foreach (string candidate in new[] { System.IO.Path.Combine(modRoot, GameVersionFolder), System.IO.Path.Combine(modRoot, "Common") })
            if (Directory.Exists(candidate)) result.Add(System.IO.Path.GetFullPath(candidate));
        result.Add(System.IO.Path.GetFullPath(modRoot));
        return result;
    }

    /// <summary>
    /// A mod with folders for older versions (1.5, v1.6, ...) but neither a 1.7 folder nor a v1.7 entry in
    /// loadFolders.xml. Here only its root and Common are used; whether RimWorld 1.7 falls back to the
    /// newest older folder has not been verified against the game code, so the count is reported.
    /// </summary>
    public static bool OlderVersionFoldersOnly(string modRoot)
    {
        if (Directory.Exists(System.IO.Path.Combine(modRoot, GameVersionFolder))) return false;
        string file = System.IO.Path.Combine(modRoot, "loadFolders.xml");
        try
        {
            if (File.Exists(file) && File.ReadAllText(file).Contains("<v" + GameVersionFolder + ">", StringComparison.Ordinal)) return false;
            return Directory.GetDirectories(modRoot).Select(System.IO.Path.GetFileName)
                .Any(n => n != null && System.Text.RegularExpressions.Regex.IsMatch(n, @"^v?1\.\d+$"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool IsOfficial(string root)
    {
        string? parent = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(root));
        return parent == "Data" && Array.IndexOf(OfficialOrder, System.IO.Path.GetFileName(root)) >= 0;
    }

    private static int OfficialIndex(string root)
    {
        int i = Array.IndexOf(OfficialOrder, System.IO.Path.GetFileName(root));
        return i < 0 ? 50 : i;
    }

    public static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static string Fingerprint(IReadOnlyList<string> roots, IEnumerable<string> files, bool content, CancellationToken token)
    {
        var rows = new List<string>();
        foreach (string f in files)
        {
            token.ThrowIfCancellationRequested();
            string row = RelativeTo(roots, f) + "|" + SafeLength(f);
            if (content)
            {
                try
                {
                    using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
                    row += "|" + Convert.ToHexStringLower(SHA256.HashData(s));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { row += "|unreadable"; }
            }
            rows.Add(row);
        }
        rows.Sort(StringComparer.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", rows))));
    }
}
