using System;

namespace MoreUseResources.Core
{
    /// <summary>One completed observation window; these are measured values, not game performance predictions.</summary>
    public readonly struct PerformanceSnapshot
    {
        public readonly double FramesPerSecond;
        public readonly double TicksPerSecond;
        public readonly double AverageFrameIntervalMs;
        public readonly double WorstFrameIntervalMs;

        internal PerformanceSnapshot(double fps, double tps, double averageMs, double worstMs)
        {
            FramesPerSecond = fps;
            TicksPerSecond = tps;
            AverageFrameIntervalMs = averageMs;
            WorstFrameIntervalMs = worstMs;
        }
    }

    /// <summary>
    /// Measures rendered frame cadence and game ticks over roughly one second. Call on the main
    /// thread from each frame; a null tick counter represents loading or the title screen.
    /// </summary>
    public sealed class PerformanceMeter
    {
        private long _windowStartMs = -1;
        private long? _firstTick;
        private int _frames;
        private int _validIntervals;
        private double _intervalTotal;
        private double _worstInterval;

        public PerformanceSnapshot Latest { get; private set; }

        public void Sample(long nowMs, long? totalGameTicks, double frameIntervalMs)
        {
            if (_windowStartMs < 0 || nowMs < _windowStartMs)
            {
                _windowStartMs = nowMs;
                _firstTick = totalGameTicks;
                _frames = 0;
                _validIntervals = 0;
                _intervalTotal = 0;
                _worstInterval = 0;
            }
            _frames++;
            if (double.IsFinite(frameIntervalMs) && frameIntervalMs > 0)
            {
                _intervalTotal += frameIntervalMs;
                _validIntervals++;
                _worstInterval = Math.Max(_worstInterval, frameIntervalMs);
            }
            long elapsedMs = nowMs - _windowStartMs;
            if (elapsedMs < 1000) return;

            double fps = _frames * 1000.0 / elapsedMs;
            double tps = totalGameTicks.HasValue && _firstTick.HasValue
                && totalGameTicks.Value >= _firstTick.Value
                ? (totalGameTicks.Value - _firstTick.Value) * 1000.0 / elapsedMs
                : 0;
            double averageMs = _validIntervals > 0 ? _intervalTotal / _validIntervals : 0;
            Latest = new PerformanceSnapshot(fps, tps, averageMs, _worstInterval);

            _windowStartMs = nowMs;
            _firstTick = totalGameTicks;
            _frames = 0;
            _validIntervals = 0;
            _intervalTotal = 0;
            _worstInterval = 0;
        }
    }
}
