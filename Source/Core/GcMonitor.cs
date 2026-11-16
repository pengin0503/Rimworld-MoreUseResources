using System;
using System.Runtime;

namespace MoreUseResources.Core
{
    /// <summary>Raw cumulative garbage-collector counters of this process.</summary>
    public readonly struct GcCounters
    {
        public GcCounters(int gen0, int gen1, int gen2, double totalPauseMs, long heapBytes, long allocatedBytes)
        {
            Gen0 = gen0;
            Gen1 = gen1;
            Gen2 = gen2;
            TotalPauseMs = totalPauseMs;
            HeapBytes = heapBytes;
            AllocatedBytes = allocatedBytes;
        }

        public int Gen0 { get; }
        public int Gen1 { get; }
        public int Gen2 { get; }
        public double TotalPauseMs { get; }
        public long HeapBytes { get; }
        public long AllocatedBytes { get; }

        public static GcCounters ReadProcess()
        {
            return new GcCounters(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                GC.GetTotalPauseDuration().TotalMilliseconds, GC.GetTotalMemory(false), GC.GetTotalAllocatedBytes(false));
        }
    }

    /// <summary>Garbage-collector activity over the last completed window. Measured values only.</summary>
    public readonly struct GcSnapshot
    {
        public GcSnapshot(double windowSeconds, int gen0, int gen1, int gen2, double pauseMs, double allocatedMiBPerSecond,
            long heapBytes, bool serverGc, bool concurrent, GCLatencyMode latency)
        {
            WindowSeconds = windowSeconds;
            Gen0Collections = gen0;
            Gen1Collections = gen1;
            Gen2Collections = gen2;
            PauseMs = pauseMs;
            AllocatedMiBPerSecond = allocatedMiBPerSecond;
            HeapBytes = heapBytes;
            ServerGc = serverGc;
            Concurrent = concurrent;
            Latency = latency;
        }

        public double WindowSeconds { get; }
        /// <summary>Collections whose highest generation was exactly this one.</summary>
        public int Gen0Collections { get; }
        public int Gen1Collections { get; }
        public int Gen2Collections { get; }
        /// <summary>Time all managed threads were stopped by the collector in the window.</summary>
        public double PauseMs { get; }
        public double AllocatedMiBPerSecond { get; }
        public long HeapBytes { get; }
        public bool ServerGc { get; }
        public bool Concurrent { get; }
        public GCLatencyMode Latency { get; }
        public double PausePercent => WindowSeconds > 0 ? PauseMs / (WindowSeconds * 10.0) : 0;
    }

    /// <summary>
    /// Turns cumulative collector counters into per-window numbers, so that the effect of
    /// collector settings (generation 0 budget, server GC, latency mode) can be seen in game.
    /// Call from one thread.
    /// </summary>
    public sealed class GcMonitor
    {
        public const int WindowMs = 10_000;
        private readonly Func<GcCounters> _read;
        private readonly bool _serverGc;
        private readonly bool _concurrent;
        private long _windowStartMs = -1;
        private GcCounters _start;

        public GcMonitor(Func<GcCounters>? read = null, bool? serverGc = null, bool? concurrent = null)
        {
            _read = read ?? GcCounters.ReadProcess;
            _serverGc = serverGc ?? GCSettings.IsServerGC;
            _concurrent = concurrent ?? (AppContext.TryGetSwitch("System.GC.Concurrent", out bool on) ? on : true);
        }

        public GcSnapshot Latest { get; private set; }
        public bool HasData { get; private set; }

        public void Sample(long nowMs, GCLatencyMode latency)
        {
            if (_windowStartMs < 0 || nowMs < _windowStartMs)
            {
                _windowStartMs = nowMs;
                _start = _read();
                return;
            }
            long elapsed = nowMs - _windowStartMs;
            if (elapsed < WindowMs) return;
            GcCounters now = _read();
            // CollectionCount(n) includes every collection of a higher generation.
            int gen2 = Math.Max(0, now.Gen2 - _start.Gen2);
            int gen1 = Math.Max(0, now.Gen1 - _start.Gen1 - gen2);
            int gen0 = Math.Max(0, now.Gen0 - _start.Gen0 - gen1 - gen2);
            double seconds = elapsed / 1000.0;
            double allocated = Math.Max(0, now.AllocatedBytes - _start.AllocatedBytes) / (1024.0 * 1024.0) / seconds;
            Latest = new GcSnapshot(seconds, gen0, gen1, gen2, Math.Max(0, now.TotalPauseMs - _start.TotalPauseMs),
                allocated, now.HeapBytes, _serverGc, _concurrent, latency);
            HasData = true;
            _windowStartMs = nowMs;
            _start = now;
        }
    }
}
