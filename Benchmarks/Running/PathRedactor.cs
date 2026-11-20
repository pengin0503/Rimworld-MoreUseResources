using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MoreUseResources.Benchmarks.Running;

/// <summary>
/// Replaces local directories in messages (exception texts often contain full paths) with
/// placeholders, so result files carry no user names or home paths.
/// </summary>
public static class PathRedactor
{
    private static volatile (string Path, string Name)[] _map = Array.Empty<(string, string)>();

    public static void Configure(IEnumerable<string> dataRoots, string output, string scratch)
    {
        var map = new List<(string, string)>();
        int i = 0;
        foreach (string r in dataRoots) map.Add((Full(r), "<data" + i++ + ">"));
        map.Add((Full(scratch), "<scratch>"));
        map.Add((Full(output), "<out>"));
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) && home != "/") map.Add((Full(home), "~"));
        // Longest first, so a data root inside the home directory keeps its own name.
        _map = map.Where(m => m.Item1.Length > 1).OrderByDescending(m => m.Item1.Length).ToArray();
    }

    public static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var (path, name) in _map)
            text = text.Replace(path, name, StringComparison.Ordinal);
        return text;
    }

    private static string Full(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar);
}
