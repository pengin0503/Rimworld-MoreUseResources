using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace MoreUseResources.Core
{
    /// <summary>
    /// Locates the container files RimWorld 1.7 (Waylith) really reads textures from.
    /// Once a source PNG was converted, the engine keeps the compressed result in
    /// <c>&lt;cache&gt;/TextureCache/v&lt;N&gt;/&lt;tag&gt;/&lt;mod folder&gt;-&lt;16 hex&gt;.zip</c> and later
    /// launches only compare the PNG's length and timestamp. Official content ships in an
    /// <c>Assets.zip</c> next to each load folder. Warming loose PNG files therefore does not
    /// touch what the game reads after its first launch; these packs do.
    /// Discovery is read-only and never opens the packs.
    /// </summary>
    public static class EnginePackFiles
    {
        public const string AssetPackFileName = "Assets.zip";
        public const string TextureCacheDirectory = "TextureCache";
        public const int MaxPacks = 4096;
        public const string ShaderCacheDirectory = "ShaderCache";
        public const string ShaderCacheExtension = ".spv-cache";
        public const int MaxShaderCacheFiles = 4096;
        private const int HashHexLength = 16;
        private const string KeyedPrefix = "pages.";

        /// <summary>Mirrors the engine's pack naming: invalid file-name characters and spaces become '_'.</summary>
        public static string SanitizeRootName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "root";
            char[] chars = name.ToCharArray();
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < chars.Length; i++)
            {
                if (chars[i] == ' ' || Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
            }
            return new string(chars);
        }

        /// <summary>
        /// Splits "&lt;root&gt;-&lt;16 lowercase hex&gt;.zip" into its root part. Returns false for other names.
        /// </summary>
        public static bool TryGetPackRootName(string fileName, out string rootName)
        {
            rootName = string.Empty;
            const string extension = ".zip";
            int tail = 1 + HashHexLength + extension.Length;
            if (fileName.Length <= tail || !fileName.EndsWith(extension, StringComparison.Ordinal)) return false;
            int dash = fileName.Length - tail;
            if (fileName[dash] != '-') return false;
            for (int i = dash + 1; i < dash + 1 + HashHexLength; i++)
            {
                char c = fileName[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            rootName = fileName.Substring(0, dash);
            return true;
        }

        /// <summary>The engine's atlas page pack: "pages.&lt;16 hex&gt;.zip".</summary>
        public static bool IsKeyedPagesPack(string fileName)
        {
            if (fileName.Length != KeyedPrefix.Length + HashHexLength + 4
                || !fileName.StartsWith(KeyedPrefix, StringComparison.Ordinal)
                || !fileName.EndsWith(".zip", StringComparison.Ordinal)) return false;
            for (int i = KeyedPrefix.Length; i < KeyedPrefix.Length + HashHexLength; i++)
            {
                char c = fileName[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        public static string CacheFileName(string modRoot)
        {
            string root = Path.TrimEndingDirectorySeparator(modRoot);
            string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root)).AsSpan(0, 8));
            return SanitizeRootName(Path.GetFileName(root)) + "-" + hash + ".zip";
        }

        /// <summary>
        /// Returns existing pack files, most valuable first: texture cache packs of the given mod
        /// roots, then asset packs of the load folders and <paramref name="extraFiles"/>.
        /// Candidate packs from all cache generations and compression settings are included,
        /// newest first. File timestamps alone cannot identify the engine's active cache;
        /// dropping older candidates could omit the pack currently opened by the engine.
        /// </summary>
        public static List<string> Discover(string? cacheDataPath, IReadOnlyList<string> modRoots,
            IReadOnlyList<string> loadFolders, IReadOnlyList<string>? extraFiles, CancellationToken token, string? activeCacheRoot = null, string? installId = null,
            IReadOnlyList<string>? mountedPacks = null)
        {
            var result = new List<string>();
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var seen = new HashSet<string>(comparer);

            if (!string.IsNullOrWhiteSpace(activeCacheRoot))
            {
                string atlas = "pages." + installId + ".zip";
                if (!token.IsCancellationRequested && IsKeyedPagesPack(atlas))
                    AddIfFile(Path.Combine(activeCacheRoot, atlas), result, seen);
                foreach (string root in modRoots)
                {
                    if (token.IsCancellationRequested || result.Count >= MaxPacks) return result;
                    if (!string.IsNullOrWhiteSpace(root))
                        AddIfFile(Path.Combine(activeCacheRoot, CacheFileName(root)), result, seen);
                }
            }
            else
                AddTextureCachePacks(cacheDataPath, modRoots, comparer, result, seen, token);

            if (mountedPacks != null)
            {
                foreach (string path in mountedPacks)
                {
                    if (token.IsCancellationRequested || result.Count >= MaxPacks) return result;
                    AddIfFile(path, result, seen);
                }
                return result;
            }

            foreach (string folder in loadFolders)
            {
                if (token.IsCancellationRequested || result.Count >= MaxPacks) return result;
                AddIfFile(Path.Combine(folder, AssetPackFileName), result, seen);
            }
            if (extraFiles != null)
            {
                foreach (string file in extraFiles)
                {
                    if (token.IsCancellationRequested || result.Count >= MaxPacks) return result;
                    AddIfFile(file, result, seen);
                }
            }
            return result;
        }

        /// <summary>
        /// The engine stores each compiled shader as
        /// <c>&lt;cache&gt;/ShaderCache/&lt;64 lowercase hex&gt;.spv-cache</c> and reads the whole record
        /// with an ordinary file read the next time that shader is requested. Only names of that
        /// exact shape are returned; the directory is never written to.
        /// </summary>
        public static List<string> DiscoverShaderCache(string? cacheDataPath, CancellationToken token)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(cacheDataPath)) return result;
            try
            {
                string directory = Path.Combine(cacheDataPath!, ShaderCacheDirectory);
                if (!Directory.Exists(directory) || IsReparsePoint(directory)) return result;
                foreach (string file in Directory.EnumerateFiles(directory, "*" + ShaderCacheExtension))
                {
                    if (token.IsCancellationRequested || result.Count >= MaxShaderCacheFiles) break;
                    if (IsShaderCacheRecord(Path.GetFileName(file)) && !IsReparsePoint(file)) result.Add(file);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>"&lt;SHA-256 as 64 lowercase hex&gt;.spv-cache", the only name the engine writes.</summary>
        public static bool IsShaderCacheRecord(string fileName)
        {
            const int hex = 64;
            if (fileName.Length != hex + ShaderCacheExtension.Length
                || !fileName.EndsWith(ShaderCacheExtension, StringComparison.Ordinal)) return false;
            for (int i = 0; i < hex; i++)
            {
                char c = fileName[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        private static void AddTextureCachePacks(string? cacheDataPath, IReadOnlyList<string> modRoots,
            StringComparer comparer, List<string> result, HashSet<string> seen, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(cacheDataPath) || modRoots.Count == 0) return;
            var wanted = new HashSet<string>(comparer);
            foreach (string root in modRoots)
            {
                string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
                if (!string.IsNullOrEmpty(name)) wanted.Add(SanitizeRootName(name));
            }

            // Keep distinct full paths, not only the newest basename: the active cache
            // generation is not reliably identifiable from file modification timestamps.
            var candidates = new List<(string Path, DateTime Written)>();
            try
            {
                string cacheRoot = Path.Combine(cacheDataPath!, TextureCacheDirectory);
                if (!Directory.Exists(cacheRoot)) return;
                foreach (string version in Directory.EnumerateDirectories(cacheRoot))
                {
                    if (IsReparsePoint(version)) continue;
                    foreach (string variant in SafeDirectories(version))
                    {
                        if (IsReparsePoint(variant)) continue;
                        foreach (string file in SafeFiles(variant))
                        {
                            if (token.IsCancellationRequested) return;
                            string fileName = Path.GetFileName(file);
                            bool match = IsKeyedPagesPack(fileName)
                                || (TryGetPackRootName(fileName, out string rootName) && wanted.Contains(rootName));
                            if (!match) continue;
                            DateTime written;
                            try { written = File.GetLastWriteTimeUtc(file); }
                            catch (IOException) { continue; }
                            catch (UnauthorizedAccessException) { continue; }
                            candidates.Add((file, written));
                        }
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            candidates.Sort((a, b) =>
            {
                int byDate = b.Written.CompareTo(a.Written);
                return byDate != 0 ? byDate : StringComparer.Ordinal.Compare(a.Path, b.Path);
            });
            foreach (var candidate in candidates)
            {
                if (result.Count >= MaxPacks) return;
                if (seen.Add(candidate.Path)) result.Add(candidate.Path);
            }
        }

        private static void AddIfFile(string path, List<string> result, HashSet<string> seen)
        {
            try
            {
                if (File.Exists(path) && !IsReparsePoint(path) && seen.Add(path)) result.Add(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static bool IsReparsePoint(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        private static IEnumerable<string> SafeDirectories(string directory)
        {
            try { return Directory.GetDirectories(directory); }
            catch (IOException) { return Array.Empty<string>(); }
            catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        }

        private static IEnumerable<string> SafeFiles(string directory)
        {
            try { return Directory.GetFiles(directory, "*.zip"); }
            catch (IOException) { return Array.Empty<string>(); }
            catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        }
    }
}
