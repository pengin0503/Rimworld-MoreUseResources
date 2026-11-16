using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace MoreUseResources.Core
{
    internal static class GameAssetFiles
    {
        private const int MaxFiles = 50_000;
        private static readonly string[] AssetDirectories = { "Defs", "Patches", "Languages", "Textures", "Sounds" };
        private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".xml", ".txt", ".png", ".dds", ".jpg", ".jpeg", ".tga", ".psd", ".bmp", ".wav", ".ogg", ".mp3",
        };

        public static List<string> Discover(IReadOnlyList<string> loadFolders, CancellationToken token, Func<bool> active)
        {
            var paths = new List<string>();
            var existing = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
                MaxRecursionDepth = 10,
            };

            // Defs and patches are used early in startup; visit them before bulk asset files.
            foreach (string subdir in AssetDirectories)
            foreach (string root in loadFolders)
            {
                if (token.IsCancellationRequested || !active()) return paths;
                try
                {
                    string directory = Path.Combine(root, subdir);
                    if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    foreach (string file in Directory.EnumerateFiles(directory, "*", options))
                    {
                        if (token.IsCancellationRequested || !active()) return paths;
                        if (!Extensions.Contains(Path.GetExtension(file)) || !existing.Add(file)) continue;
                        paths.Add(file);
                        if (paths.Count >= MaxFiles) return paths;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return paths;
        }
    }
}
