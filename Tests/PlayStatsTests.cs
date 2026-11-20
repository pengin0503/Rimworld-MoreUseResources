using System.Runtime;
using MoreUseResources.Core;
using Xunit;

namespace MoreUseResources.Tests
{
    public sealed class PlayStatsTests
    {
        private static GcSnapshot Window(int gen0, double pauseMs, double allocatedMiBPerSecond, long heapMiB) =>
            new(10, gen0, 1, 0, pauseMs, allocatedMiBPerSecond, heapMiB << 20, serverGc: false, concurrent: true,
                GCLatencyMode.Interactive, committedBytes: (heapMiB * 2) << 20);

        [Fact]
        public void Averages_windows_per_speed_and_keeps_the_worst_window()
        {
            var stats = new PlayStats();
            Assert.False(stats.HasData);
            Assert.Equal("none yet", stats.Report());

            stats.Add(3, Window(10, 20, 50, 1000), ticks: 1800, frames: 600);
            stats.Add(3, Window(14, 60, 70, 1200), ticks: 1700, frames: 580);
            stats.Add(1, Window(2, 5, 10, 900), ticks: 600, frames: 600);

            Assert.True(stats.HasData);
            Assert.Equal(
                "speed 1: 10 s (1 windows), TPS 60.0, FPS 60.0, GC gen0/1/2 2/1/0, stopped 5 ms (0.05%), worst 10 s window 5 ms, "
                + "allocating 10.0 MiB/s, max heap 900 MiB, max GC committed 1800 MiB"
                + " | speed 3: 20 s (2 windows), TPS 175.0, FPS 59.0, GC gen0/1/2 24/2/0, stopped 80 ms (0.40%), worst 10 s window 60 ms, "
                + "allocating 60.0 MiB/s, max heap 1200 MiB, max GC committed 2400 MiB",
                stats.Report());
        }

        [Fact]
        public void Ignores_empty_windows_and_negative_counts()
        {
            var stats = new PlayStats();
            stats.Add(2, new GcSnapshot(0, 1, 0, 0, 1, 1, 0, false, true, GCLatencyMode.Interactive), 100, 100);
            Assert.False(stats.HasData);

            stats.Add(2, Window(1, 0, 0, 1), ticks: -5, frames: -5);
            Assert.StartsWith("speed 2: 10 s (1 windows), TPS 0.0, FPS 0.0,", stats.Report());
        }

        [Fact]
        public void Settings_description_lists_every_setting_including_phase_arrays()
        {
            string text = new ResourceSettings().Describe();
            Assert.Contains("Enabled=True", text);
            Assert.Contains("RamHoldCopies=True", text);
            Assert.Contains("CpuMaxWorkers=0", text);
            Assert.Matches(@"CpuPhases=\[(True|False)(,(True|False))*\]", text);
            Assert.DoesNotContain("\n", text);
        }
    }
}
