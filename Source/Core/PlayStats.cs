using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MoreUseResources.Core;

/// <summary>What happened during one ten-second window besides garbage collection. Unknown values are negative or null.</summary>
/// <param name="Ticks">Game ticks that passed.</param>
/// <param name="Frames">Frames drawn.</param>
/// <param name="FramesOver50Ms">Frames that took longer than 50 ms.</param>
/// <param name="FramesOver100Ms">Frames that took longer than 100 ms.</param>
/// <param name="LongestFrameMs">The longest frame.</param>
/// <param name="Process">Process counters over the window (<see cref="ProcessCounters.Since"/>); the thread is the main thread.</param>
/// <param name="ModReadBytes">Bytes this mod read from files (<see cref="ModFileReads"/>), part of the process reads.</param>
public readonly record struct PlayWindow(long Ticks, long Frames, int FramesOver50Ms, int FramesOver100Ms, double LongestFrameMs,
    ProcessCounters Process, long ModReadBytes)
{
    /// <summary>Physical memory the OS reported as available at the end.</summary>
    public long AvailableBytes { get; init; } = -1;

    /// <summary>Textures the engine had moved from VRAM to system RAM at the end.</summary>
    public int OffloadedTextures { get; init; } = -1;

    /// <summary>CPU time the engine spent unpacking asset-pack entries.</summary>
    public double PackDecodeMs { get; init; } = -1;

    /// <summary>CPU time per thread group (<see cref="ThreadSampler.Delta"/>).</summary>
    public IReadOnlyDictionary<string, GroupCpu>? Threads { get; init; }

    /// <summary>Hard page faults of the process: reads it waited on storage for, including memory-mapped files.</summary>
    public long HardFaults { get; init; } = -1;

    /// <summary>Wall time the main thread spent in the game's tick loop, in the map update, and in the UI.</summary>
    public double TickMs { get; init; } = -1;

    public double MapMs { get; init; } = -1;

    public double UiMs { get; init; } = -1;

    /// <summary>Main-thread time taken to read this window's counters, so the cost of measuring is visible.</summary>
    public double SamplingMs { get; init; } = -1;

    /// <summary>Time a background thread took to read <see cref="Threads"/> at the end of the window.</summary>
    public double ThreadReadMs { get; init; } = -1;
}

/// <summary>
/// Adds up the ten-second windows measured while the game ran unpaused, per selected game speed and tick-rate
/// multiplier (speed 3 runs 6 or 12 ticks per frame depending on whether colonists are awake), so a copied status
/// gives an average over the whole play instead of the last window only.
/// Only windows that stayed in one phase, speed, multiplier and latency mode from start to end are added. Call from one thread.
/// </summary>
public sealed class PlayStats
{
    private readonly SortedDictionary<(int Speed, double Multiplier), Totals> _byRate = new();
    private readonly int _logicalProcessors;

    public PlayStats(int logicalProcessors) => _logicalProcessors = Math.Max(1, logicalProcessors);

    public bool HasData => _byRate.Count > 0;

    /// <param name="speed">The selected game speed (1 = normal).</param>
    /// <param name="tickRateMultiplier">The game's ticks per frame at 60 frames per second for that speed.</param>
    public void Add(int speed, double tickRateMultiplier, GcSnapshot gc, PlayWindow window)
    {
        if (gc.WindowSeconds <= 0 || !double.IsFinite(tickRateMultiplier)) return;
        var key = (speed, Math.Max(0, tickRateMultiplier));
        if (!_byRate.TryGetValue(key, out Totals? t)) _byRate[key] = t = new Totals();
        t.Windows++;
        t.Seconds += gc.WindowSeconds;
        t.Ticks += Math.Max(0, window.Ticks);
        t.Frames += Math.Max(0, window.Frames);
        t.Gen0 += gc.Gen0Collections;
        t.Gen1 += gc.Gen1Collections;
        t.Gen2 += gc.Gen2Collections;
        t.PauseMs += gc.PauseMs;
        t.WorstWindowPauseMs = Math.Max(t.WorstWindowPauseMs, gc.PauseMs);
        t.AllocatedMiB += gc.AllocatedMiBPerSecond * gc.WindowSeconds;
        t.MaxHeapBytes = Math.Max(t.MaxHeapBytes, gc.HeapBytes);
        t.MaxCommittedBytes = Math.Max(t.MaxCommittedBytes, gc.CommittedBytes);

        t.Over50 += window.FramesOver50Ms;
        t.Over100 += window.FramesOver100Ms;
        t.LongestFrameMs = Math.Max(t.LongestFrameMs, window.LongestFrameMs);
        ProcessCounters p = window.Process;
        t.WallMs += p.WallMs;
        t.CpuMs += p.CpuMs;
        t.JitMs += p.JitMs;
        t.JitMethods += p.JitMethods;
        t.ModReadBytes += Math.Max(0, window.ModReadBytes);
        Accumulate(ref t.MainThreadMs, ref t.MainThreadWallMs, p.ThreadCpuMs, p.WallMs);
        Accumulate(ref t.SystemBusyMs, ref t.SystemTotalMs, p.SystemBusyMs, p.SystemTotalMs);
        if (p.ReadBytes is { } read)
        {
            t.ReadBytes += Math.Max(0, read);
            t.ReadSeconds += gc.WindowSeconds;
        }
        if (window.AvailableBytes >= 0)
            t.MinAvailableBytes = t.MinAvailableBytes < 0 ? window.AvailableBytes : Math.Min(t.MinAvailableBytes, window.AvailableBytes);
        t.MaxOffloaded = Math.Max(t.MaxOffloaded, window.OffloadedTextures);
        if (window.PackDecodeMs >= 0) t.PackDecodeMs = Math.Max(0, t.PackDecodeMs) + window.PackDecodeMs;

        if (window.Threads != null && p.WallMs > 0)
        {
            t.ThreadWallMs += p.WallMs;
            foreach (KeyValuePair<string, GroupCpu> pair in window.Threads)
            {
                t.ThreadGroups.TryGetValue(pair.Key, out GroupCpu sum);
                t.ThreadGroups[pair.Key] = new GroupCpu(sum.CpuMs + Math.Max(0, pair.Value.CpuMs), Math.Max(sum.Threads, pair.Value.Threads));
            }
        }
        if (window.HardFaults >= 0)
        {
            t.HardFaults += window.HardFaults;
            t.HardFaultSeconds += gc.WindowSeconds;
        }
        if (window.TickMs >= 0 && window.MapMs >= 0 && window.UiMs >= 0 && p.WallMs > 0)
        {
            t.TickMs += window.TickMs;
            t.MapMs += window.MapMs;
            t.UiMs += window.UiMs;
            t.SplitWallMs += p.WallMs;
        }
        if (window.SamplingMs >= 0)
        {
            t.SamplingMs += window.SamplingMs;
            t.SampledWindows++;
            t.MaxSamplingMs = Math.Max(t.MaxSamplingMs, window.SamplingMs);
        }
        if (window.ThreadReadMs >= 0)
        {
            t.ThreadReadMs += window.ThreadReadMs;
            t.ThreadReadWindows++;
            t.MaxThreadReadMs = Math.Max(t.MaxThreadReadMs, window.ThreadReadMs);
        }
    }

    private static void Accumulate(ref double sum, ref double basis, double? value, double? over)
    {
        if (value is not { } v || over is not { } o || o <= 0) return;
        sum += Math.Max(0, v);
        basis += o;
    }

    /// <summary>Three lines per speed, invariant culture, so a pasted report reads the same on every machine.</summary>
    public string Report()
    {
        if (_byRate.Count == 0) return "none yet";
        var c = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        foreach (KeyValuePair<(int Speed, double Multiplier), Totals> pair in _byRate)
        {
            Totals t = pair.Value;
            double target = 60 * pair.Key.Multiplier;
            double tps = t.Ticks / t.Seconds;
            if (text.Length > 0) text.Append('\n');
            text.Append(c, $"speed {pair.Key.Speed} x{pair.Key.Multiplier:0.##}: {t.Seconds:F0} s ({t.Windows} windows), ")
                .Append(c, $"TPS {tps:F1}")
                .Append(target > 0 ? string.Create(c, $" ({tps / target * 100:F0}% of {target:0.#})") : string.Empty)
                .Append(c, $", FPS {t.Frames / t.Seconds:F1}, ")
                .Append(c, $"GC gen0/1/2 {t.Gen0}/{t.Gen1}/{t.Gen2}, ")
                .Append(c, $"stopped {t.PauseMs:F0} ms ({t.PauseMs / (t.Seconds * 10):F2}%), ")
                .Append(c, $"worst 10 s window {t.WorstWindowPauseMs:F0} ms, ")
                .Append(c, $"allocating {t.AllocatedMiB / t.Seconds:F1} MiB/s, ")
                .Append(c, $"max heap {t.MaxHeapBytes / (1024.0 * 1024.0):F0} MiB, ")
                .Append(c, $"max GC committed {t.MaxCommittedBytes / (1024.0 * 1024.0):F0} MiB");
            text.Append("\n  ");
            text.Append(c, $"process CPU {(t.WallMs > 0 ? t.CpuMs / (t.WallMs * _logicalProcessors) * 100 : 0):F1}% of {_logicalProcessors} logical CPUs");
            text.Append(t.MainThreadWallMs > 0 ? string.Create(c, $", main thread busy {t.MainThreadMs / t.MainThreadWallMs * 100:F0}%") : ", main thread n/a");
            text.Append(t.SystemTotalMs > 0 ? string.Create(c, $", whole PC busy {t.SystemBusyMs / t.SystemTotalMs * 100:F0}%") : ", whole PC n/a");
            text.Append(c, $", JIT {t.JitMs:F0} ms ({t.JitMethods} methods)");
            text.Append(t.ReadSeconds > 0
                ? string.Create(c, $", I/O read {t.ReadBytes / (1024.0 * 1024.0) / t.ReadSeconds:F2} MiB/s (this mod {t.ModReadBytes / (1024.0 * 1024.0) / t.Seconds:F2})")
                : ", I/O read n/a");
            text.Append(t.HardFaultSeconds > 0 ? string.Create(c, $", hard page faults {t.HardFaults / t.HardFaultSeconds:F1}/s") : ", hard page faults n/a");
            text.Append(c, $", frames over 50 ms {t.Over50}, over 100 ms {t.Over100}, longest {t.LongestFrameMs:F0} ms");
            text.Append(t.MinAvailableBytes >= 0 ? string.Create(c, $", free RAM min {t.MinAvailableBytes / (1024.0 * 1024.0):F0} MiB") : ", free RAM n/a");
            text.Append(t.MaxOffloaded >= 0 ? string.Create(c, $", textures offloaded to RAM max {t.MaxOffloaded}") : ", offloaded n/a");
            text.Append(t.PackDecodeMs >= 0 ? string.Create(c, $", pack unpacking {t.PackDecodeMs:F0} ms CPU") : ", pack unpacking n/a");
            text.Append("\n  ");
            text.Append(t.SplitWallMs > 0
                ? string.Create(c, $"main thread in ticks {t.TickMs / t.SplitWallMs * 100:F0}%, map update {t.MapMs / t.SplitWallMs * 100:F0}%, UI {t.UiMs / t.SplitWallMs * 100:F0}% of wall time")
                : "main thread split n/a");
            text.Append("; CPU cores by thread: ").Append(ThreadSampler.Format(t.ThreadGroups, t.ThreadWallMs));
            text.Append(t.SampledWindows > 0
                ? string.Create(c, $"; measuring took {t.SamplingMs / t.SampledWindows:F1} ms per window on the main thread (max {t.MaxSamplingMs:F1})")
                : "; measuring cost n/a");
            if (t.ThreadReadWindows > 0)
                text.Append(c, $", reading threads {t.ThreadReadMs / t.ThreadReadWindows:F1} ms on a background thread (max {t.MaxThreadReadMs:F1})");
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
        public long Over50;
        public long Over100;
        public double LongestFrameMs;
        public double WallMs;
        public double CpuMs;
        public double MainThreadMs;
        public double MainThreadWallMs;
        public double SystemBusyMs;
        public double SystemTotalMs;
        public double JitMs;
        public long JitMethods;
        public long ReadBytes;
        public double ReadSeconds;
        public long ModReadBytes;
        public long MinAvailableBytes = -1;
        public int MaxOffloaded = -1;
        public double PackDecodeMs = -1;
        public readonly Dictionary<string, GroupCpu> ThreadGroups = new(StringComparer.Ordinal);
        public double ThreadWallMs;
        public long HardFaults;
        public double HardFaultSeconds;
        public double TickMs;
        public double MapMs;
        public double UiMs;
        public double SplitWallMs;
        public double SamplingMs;
        public int SampledWindows;
        public double MaxSamplingMs;
        public double ThreadReadMs;
        public int ThreadReadWindows;
        public double MaxThreadReadMs;
    }
}
