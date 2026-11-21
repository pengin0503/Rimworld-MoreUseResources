using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MoreUseResources.Core;

/// <summary>One game save as the main thread saw it, from the start of the game's save call to its end.</summary>
/// <param name="WallMs">Wall time the save held the main thread.</param>
/// <param name="GcPauseMs">Time the runtime stopped the threads for garbage collection during the save.</param>
/// <param name="AllocatedBytes">Bytes the process allocated during the save, on every thread (the save is most of it).</param>
public readonly record struct SaveTime(double WallMs, double GcPauseMs, long AllocatedBytes);

/// <summary>
/// The saves of this session. Manual saves and autosaves both run the game's save call on the main thread, so the
/// cost of saving can be compared between two launches, for example with and without a mod that adds to every save.
/// Keeps the last <see cref="Kept"/> saves for the median and the list. Call from one thread.
/// </summary>
public sealed class SaveTimes
{
    public const int Kept = 64;

    /// <summary>The most recent saves listed one by one; the first save of a session is usually the slowest (compilation).</summary>
    public const int Listed = 20;

    private readonly SaveTime[] _kept = new SaveTime[Kept];
    private int _count;

    /// <summary>Saves recorded since the process started, including those no longer kept.</summary>
    public int Count => _count;

    public void Add(SaveTime save)
    {
        if (!double.IsFinite(save.WallMs) || save.WallMs < 0) return;
        double pause = double.IsFinite(save.GcPauseMs) ? Math.Max(0, save.GcPauseMs) : 0;
        _kept[_count % Kept] = new SaveTime(save.WallMs, pause, Math.Max(0, save.AllocatedBytes));
        _count++;
    }

    /// <summary>One line, invariant culture, so a pasted report reads the same on every machine.</summary>
    public string Report()
    {
        if (_count == 0) return "none yet";
        int kept = Math.Min(_count, Kept);
        var saves = new SaveTime[kept];
        for (int i = 0; i < kept; i++) saves[i] = _kept[(_count - kept + i) % Kept];
        double[] sorted = saves.Select(s => s.WallMs).Order().ToArray();
        double median = kept % 2 == 1 ? sorted[kept / 2] : (sorted[kept / 2 - 1] + sorted[kept / 2]) / 2;
        var c = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.Append(c, $"{_count} saves");
        if (kept < _count) text.Append(c, $" (figures over the last {kept})");
        text.Append(c, $": median {median:F0} ms, min {sorted[0]:F0} ms, max {sorted[^1]:F0} ms; ")
            .Append(c, $"GC stopped {saves.Average(s => s.GcPauseMs):F0} ms and {saves.Average(s => (double)s.AllocatedBytes) / (1024.0 * 1024.0):F0} MiB ")
            .Append("allocated per save on average; ");
        int listed = Math.Min(kept, Listed);
        text.Append(listed < kept ? string.Create(c, $"the last {listed}") : "each").Append(" (oldest first): ");
        for (int i = kept - listed; i < kept; i++)
        {
            if (i > kept - listed) text.Append(", ");
            text.Append(saves[i].WallMs.ToString("F0", c));
        }
        return text.Append(" ms").ToString();
    }
}
