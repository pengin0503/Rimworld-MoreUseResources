using System.Runtime;
using MoreUseResources.Core;
using Xunit;

namespace MoreUseResources.Tests
{
    public sealed class GcMonitorTests
    {
        [Fact]
        public void Reports_exclusive_generation_counts_pause_and_allocation_rate_per_window()
        {
            var counters = new GcCounters(100, 20, 5, 1000, 1L << 30, 0);
            var monitor = new GcMonitor(() => counters, serverGc: false, concurrent: true);
            monitor.Sample(0, GCLatencyMode.Interactive);
            Assert.False(monitor.HasData);

            counters = new GcCounters(130, 26, 7, 1250, 2L << 30, 200L << 20);
            monitor.Sample(GcMonitor.WindowMs - 1, GCLatencyMode.Interactive);
            Assert.False(monitor.HasData);
            monitor.Sample(GcMonitor.WindowMs, GCLatencyMode.SustainedLowLatency);

            GcSnapshot s = monitor.Latest;
            Assert.True(monitor.HasData);
            Assert.Equal(10, s.WindowSeconds);
            Assert.Equal(2, s.Gen2Collections);
            Assert.Equal(4, s.Gen1Collections);
            Assert.Equal(24, s.Gen0Collections);
            Assert.Equal(250, s.PauseMs);
            Assert.Equal(2.5, s.PausePercent);
            Assert.Equal(20, s.AllocatedMiBPerSecond);
            Assert.Equal(2L << 30, s.HeapBytes);
            Assert.False(s.ServerGc);
            Assert.True(s.Concurrent);
            Assert.Equal(GCLatencyMode.SustainedLowLatency, s.Latency);
        }

        [Fact]
        public void A_clock_that_goes_backwards_restarts_the_window()
        {
            var counters = new GcCounters(10, 0, 0, 0, 0, 0);
            var monitor = new GcMonitor(() => counters, false, true);
            monitor.Sample(50_000, GCLatencyMode.Interactive);
            counters = new GcCounters(20, 0, 0, 5, 0, 0);
            monitor.Sample(10, GCLatencyMode.Interactive);
            Assert.False(monitor.HasData);
            counters = new GcCounters(23, 0, 0, 6, 0, 0);
            monitor.Sample(10 + GcMonitor.WindowMs, GCLatencyMode.Interactive);
            Assert.Equal(3, monitor.Latest.Gen0Collections);
            Assert.Equal(1, monitor.Latest.PauseMs);
        }

        [Fact]
        public void Reads_the_real_process_counters()
        {
            var monitor = new GcMonitor();
            monitor.Sample(0, GCSettings.LatencyMode);
            System.GC.Collect(0);
            monitor.Sample(GcMonitor.WindowMs, GCSettings.LatencyMode);
            Assert.True(monitor.HasData);
            Assert.True(monitor.Latest.Gen0Collections + monitor.Latest.Gen1Collections + monitor.Latest.Gen2Collections >= 1);
            Assert.True(monitor.Latest.HeapBytes > 0);
        }
    }
}
