using System;
using MoreUseResources.Core;
using Xunit;

namespace MoreUseResources.Tests
{
    public sealed class PerformanceMeterTests
    {
        [Fact]
        public void Measures_fps_tps_and_frame_interval_from_real_samples()
        {
            var meter = new PerformanceMeter();
            meter.Sample(0, 100, 16);
            meter.Sample(500, 130, 20);
            meter.Sample(1000, 160, 18);
            PerformanceSnapshot result = meter.Latest;
            Assert.Equal(3, result.FramesPerSecond, 3);
            Assert.Equal(60, result.TicksPerSecond, 3);
            Assert.Equal(18, result.AverageFrameIntervalMs, 3);
            Assert.Equal(20, result.WorstFrameIntervalMs, 3);
        }

        [Fact]
        public void Handles_a_new_save_or_title_screen_without_negative_tps()
        {
            var meter = new PerformanceMeter();
            meter.Sample(0, 4000, 16);
            meter.Sample(1000, 4060, 16);
            meter.Sample(2000, 10, 16); // loaded another save: tick counter restarted
            Assert.Equal(0, meter.Latest.TicksPerSecond);
            meter.Sample(3000, null, 16); // title screen
            Assert.Equal(0, meter.Latest.TicksPerSecond);
            meter.Sample(4000, 100, 16);
            Assert.Equal(0, meter.Latest.TicksPerSecond);
            meter.Sample(5000, 160, 16);
            Assert.Equal(60, meter.Latest.TicksPerSecond, 3);
        }

        [Fact]
        public void Ignores_invalid_delta_time_samples()
        {
            var meter = new PerformanceMeter();
            meter.Sample(0, null, double.NaN);
            meter.Sample(500, null, -5);
            meter.Sample(1000, null, 12);
            Assert.Equal(12, meter.Latest.AverageFrameIntervalMs, 3);
            Assert.Equal(12, meter.Latest.WorstFrameIntervalMs, 3);
            Assert.Equal(0, meter.Latest.TicksPerSecond);
        }
    }
}
