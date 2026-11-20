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
        public readonly int P95FrameIntervalMs;

        internal PerformanceSnapshot(double fps, double tps, double averageMs, double worstMs, int p95Ms)
        {
            FramesPerSecond = fps;
            TicksPerSecond = tps;
            AverageFrameIntervalMs = averageMs;
            WorstFrameIntervalMs = worstMs;
            P95FrameIntervalMs = p95Ms;
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
        private readonly int[] _frameBuckets = new int[256];

        public PerformanceSnapshot Latest { get; private set; }

        public void Reset() => _windowStartMs = -1;

        public bool Sample(long nowMs, long? totalGameTicks, double frameIntervalMs)
        {
            if (_windowStartMs < 0 || nowMs < _windowStartMs)
            {
                _windowStartMs = nowMs;
                _firstTick = totalGameTicks;
                _frames = 0;
                _validIntervals = 0;
                _intervalTotal = 0;
                _worstInterval = 0;
                Array.Clear(_frameBuckets);
            }
            _frames++;
            if (double.IsFinite(frameIntervalMs) && frameIntervalMs > 0)
            {
                _intervalTotal += frameIntervalMs;
                _validIntervals++;
                _frameBuckets[(int)Math.Min(Math.Ceiling(frameIntervalMs), _frameBuckets.Length - 1)]++;
                _worstInterval = Math.Max(_worstInterval, frameIntervalMs);
            }
            long elapsedMs = nowMs - _windowStartMs;
            if (elapsedMs < 1000) return false;

            double fps = _frames * 1000.0 / elapsedMs;
            double tps = totalGameTicks.HasValue && _firstTick.HasValue
                && totalGameTicks.Value >= _firstTick.Value
                ? (totalGameTicks.Value - _firstTick.Value) * 1000.0 / elapsedMs
                : 0;
            double averageMs = _validIntervals > 0 ? _intervalTotal / _validIntervals : 0;
            int rank = (int)Math.Ceiling(_validIntervals * 0.95);
            int p95 = 0;
            while (p95 < _frameBuckets.Length - 1 && rank > _frameBuckets[p95])
                rank -= _frameBuckets[p95++];
            Latest = new PerformanceSnapshot(fps, tps, averageMs, _worstInterval, p95);

            _windowStartMs = nowMs;
            _firstTick = totalGameTicks;
            _frames = 0;
            _validIntervals = 0;
            _intervalTotal = 0;
            _worstInterval = 0;
            Array.Clear(_frameBuckets);
            return true;
        }
    }
}
