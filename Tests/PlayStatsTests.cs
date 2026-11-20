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

        private static ProcessCounters Counters(double cpuMs, double? mainMs = 5000, double? busyMs = 40_000, long? readBytes = 10L << 20) =>
            new(10_000, cpuMs, mainMs, busyMs, busyMs is null ? null : 160_000, JitMs: 30, JitMethods: 200, readBytes);

        [Fact]
        public void Averages_windows_per_speed_and_keeps_the_worst_values()
        {
            var stats = new PlayStats(16);
            Assert.False(stats.HasData);
            Assert.Equal("none yet", stats.Report());

            stats.Add(3, Window(10, 20, 50, 1000), new PlayWindow(1800, 600, 3, 1, 140, Counters(32_000), 2L << 20, 8L << 30, 5, 12));
            stats.Add(3, Window(14, 60, 70, 1200), new PlayWindow(1700, 580, 1, 0, 80, Counters(48_000, mainMs: 7000), 0, 6L << 30, 9, 8));
            stats.Add(1, Window(2, 5, 10, 900), new PlayWindow(600, 600, 0, 0, 20, Counters(16_000, mainMs: null, busyMs: null, readBytes: null), 0));

            Assert.True(stats.HasData);
            Assert.Equal(
                "speed 1: 10 s (1 windows), TPS 60.0, FPS 60.0, GC gen0/1/2 2/1/0, stopped 5 ms (0.05%), worst 10 s window 5 ms, "
                + "allocating 10.0 MiB/s, max heap 900 MiB, max GC committed 1800 MiB\n"
                + "  process CPU 10.0% of 16 logical CPUs, main thread n/a, whole PC n/a, JIT 30 ms (200 methods), I/O read n/a, "
                + "frames over 50 ms 0, over 100 ms 0, longest 20 ms, free RAM n/a, offloaded n/a, pack unpacking n/a\n"
                + "speed 3: 20 s (2 windows), TPS 175.0, FPS 59.0, GC gen0/1/2 24/2/0, stopped 80 ms (0.40%), worst 10 s window 60 ms, "
                + "allocating 60.0 MiB/s, max heap 1200 MiB, max GC committed 2400 MiB\n"
                + "  process CPU 25.0% of 16 logical CPUs, main thread busy 60%, whole PC busy 25%, JIT 60 ms (400 methods), "
                + "I/O read 1.00 MiB/s (this mod 0.10), frames over 50 ms 4, over 100 ms 1, longest 140 ms, free RAM min 6144 MiB, "
                + "textures offloaded to RAM max 9, pack unpacking 20 ms CPU",
                stats.Report());
        }

        [Fact]
        public void Ignores_empty_windows_and_negative_counts()
        {
            var stats = new PlayStats(4);
            stats.Add(2, new GcSnapshot(0, 1, 0, 0, 1, 1, 0, false, true, GCLatencyMode.Interactive), new PlayWindow(100, 100, 0, 0, 0, Counters(1), 0));
            Assert.False(stats.HasData);

            stats.Add(2, Window(1, 0, 0, 1), new PlayWindow(-5, -5, 0, 0, 0, Counters(1, readBytes: -3), 0));
            Assert.StartsWith("speed 2: 10 s (1 windows), TPS 0.0, FPS 0.0,", stats.Report());
            Assert.Contains("I/O read 0.00 MiB/s", stats.Report());
        }

        [Fact]
        public void Process_counters_subtract_and_keep_missing_values_missing()
        {
            var start = new ProcessCounters(1000, 500, null, 100, 400, 10, 5, 1000);
            var end = new ProcessCounters(3000, 2500, 900, 300, 800, 25, 9, 5000);
            ProcessCounters delta = end.Since(start);
            Assert.Equal(new ProcessCounters(2000, 2000, null, 200, 400, 15, 4, 4000), delta);
        }

        [Fact]
        public void Startup_line_reports_cores_main_thread_jit_gc_and_reads()
        {
            var totals = new ProcessCounters(0, 300_000, 150_000, null, null, 40_000, 120_000, 3L << 30);
            var gc = new GcCounters(500, 60, 8, 12_500, 0, 50L << 30);
            Assert.Equal(
                "200.0 s, process CPU 300.0 s (1.5 cores on average), main thread busy 150.0 s (75%), JIT 40.0 s (120000 methods), "
                + "GC stopped 12.5 s (gen0/1/2 440/52/8), allocated 51200 MiB, I/O read 3072 MiB (this mod 1024 MiB)",
                ProcessCounters.FormatStartup(200_000, totals, gc, 1L << 30));
        }

        [Fact]
        public void Reading_the_current_process_never_goes_backwards()
        {
            ProcessCounters first = ProcessCounters.ReadCurrent();
            ProcessCounters second = ProcessCounters.ReadCurrent();
            ProcessCounters delta = second.Since(first);
            Assert.True(delta.WallMs >= 0);
            Assert.True(delta.CpuMs >= 0);
            Assert.True(delta.JitMethods >= 0);
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
