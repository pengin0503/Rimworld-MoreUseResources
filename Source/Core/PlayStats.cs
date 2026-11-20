using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MoreUseResources.Core;

/// <summary>
/// Adds up the ten-second collector windows measured while the game ran unpaused, per selected game speed,
/// so a copied status gives an average over the whole play instead of the last window only.
/// Only windows that stayed in one phase, speed and latency mode from start to end are added. Call from one thread.
/// </summary>
public sealed class PlayStats
{
    private readonly SortedDictionary<int, Totals> _bySpeed = new();

    public bool HasData => _bySpeed.Count > 0;

    /// <param name="speed">The selected game speed (1 = normal).</param>
    /// <param name="ticks">Game ticks that passed during the window.</param>
    /// <param name="frames">Frames drawn during the window.</param>
    public void Add(int speed, GcSnapshot window, long ticks, long frames)
    {
        if (window.WindowSeconds <= 0) return;
        if (!_bySpeed.TryGetValue(speed, out Totals? totals)) _bySpeed[speed] = totals = new Totals();
        totals.Windows++;
        totals.Seconds += window.WindowSeconds;
        totals.Ticks += Math.Max(0, ticks);
        totals.Frames += Math.Max(0, frames);
        totals.Gen0 += window.Gen0Collections;
        totals.Gen1 += window.Gen1Collections;
        totals.Gen2 += window.Gen2Collections;
        totals.PauseMs += window.PauseMs;
        totals.WorstWindowPauseMs = Math.Max(totals.WorstWindowPauseMs, window.PauseMs);
        totals.AllocatedMiB += window.AllocatedMiBPerSecond * window.WindowSeconds;
        totals.MaxHeapBytes = Math.Max(totals.MaxHeapBytes, window.HeapBytes);
        totals.MaxCommittedBytes = Math.Max(totals.MaxCommittedBytes, window.CommittedBytes);
    }

    /// <summary>One line per speed, invariant culture, so a pasted report reads the same on every machine.</summary>
    public string Report()
    {
        if (_bySpeed.Count == 0) return "none yet";
        var text = new StringBuilder();
        foreach (KeyValuePair<int, Totals> pair in _bySpeed)
        {
            Totals t = pair.Value;
            if (text.Length > 0) text.Append(" | ");
            text.Append(CultureInfo.InvariantCulture, $"speed {pair.Key}: {t.Seconds:F0} s ({t.Windows} windows), ")
                .Append(CultureInfo.InvariantCulture, $"TPS {t.Ticks / t.Seconds:F1}, FPS {t.Frames / t.Seconds:F1}, ")
                .Append(CultureInfo.InvariantCulture, $"GC gen0/1/2 {t.Gen0}/{t.Gen1}/{t.Gen2}, ")
                .Append(CultureInfo.InvariantCulture, $"stopped {t.PauseMs:F0} ms ({t.PauseMs / (t.Seconds * 10):F2}%), ")
                .Append(CultureInfo.InvariantCulture, $"worst 10 s window {t.WorstWindowPauseMs:F0} ms, ")
                .Append(CultureInfo.InvariantCulture, $"allocating {t.AllocatedMiB / t.Seconds:F1} MiB/s, ")
                .Append(CultureInfo.InvariantCulture, $"max heap {t.MaxHeapBytes / (1024.0 * 1024.0):F0} MiB, ")
                .Append(CultureInfo.InvariantCulture, $"max GC committed {t.MaxCommittedBytes / (1024.0 * 1024.0):F0} MiB");
        }
        return text.ToString();
    }

    private sealed class Totals
    {
        public int Windows;
        public double Seconds;
        public long Ticks;
        public long Frames;
        public long Gen0;
        public long Gen1;
        public long Gen2;
        public double PauseMs;
        public double WorstWindowPauseMs;
        public double AllocatedMiB;
        public long MaxHeapBytes;
        public long MaxCommittedBytes;
    }
}
