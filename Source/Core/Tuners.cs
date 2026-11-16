using System;
using System.Diagnostics;
using System.Runtime;

namespace MoreUseResources.Core
{
    /// <summary>
    /// Optional: asks the .NET garbage collector to avoid blocking full collections while memory is
    /// plentiful (SustainedLowLatency). The managed heap may grow larger in exchange. Always restores
    /// the mode it found, and never touches a mode it did not set itself.
    /// </summary>
    public sealed class GcTuner
    {
        private GCLatencyMode? _original;
        public string? LastError { get; private set; }
        public bool Applied => _original.HasValue;

        public void Apply(bool want)
        {
            try
            {
                GCLatencyMode current = GCSettings.LatencyMode;
                if (want)
                {
                    if (_original.HasValue) return;
                    if (current != GCLatencyMode.Interactive && current != GCLatencyMode.Batch) return;
                    GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                    if (GCSettings.LatencyMode == GCLatencyMode.SustainedLowLatency)
                    {
                        _original = current;
                    }
                }
                else if (_original.HasValue)
                {
                    if (current == GCLatencyMode.SustainedLowLatency)
                    {
                        GCSettings.LatencyMode = _original.Value;
                    }
                    _original = null;
                }
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                _original = null;
            }
        }
    }

    /// <summary>Optional: raises the priority class of the game process. Restores the original on request.</summary>
    public sealed class ProcessPriorityTuner
    {
        private ProcessPriorityClass? _original;
        private ProcessPriorityChoice _applied = ProcessPriorityChoice.Unchanged;
        public string? LastError { get; private set; }
        public ProcessPriorityChoice Applied => _applied;

        public void Apply(ProcessPriorityChoice choice)
        {
            if (choice == _applied) return;
            try
            {
                using Process p = Process.GetCurrentProcess();
                if (choice == ProcessPriorityChoice.Unchanged)
                {
                    if (_original.HasValue) p.PriorityClass = _original.Value;
                    _original = null;
                }
                else
                {
                    if (!_original.HasValue) _original = p.PriorityClass;
                    p.PriorityClass = choice == ProcessPriorityChoice.High
                        ? ProcessPriorityClass.High
                        : ProcessPriorityClass.AboveNormal;
                }
                _applied = choice;
                LastError = null;
            }
            catch (Exception ex)
            {
                // Typically missing permission. Remember the choice so we do not retry every tick.
                LastError = ex.GetType().Name + ": " + ex.Message;
                _applied = choice;
            }
        }
    }

    /// <summary>Smoothed frame time with hysteresis. Main thread only.</summary>
    public sealed class FrameGuard
    {
        private double _emaMs;
        private bool _hasSample;
        private double _goodSeconds;

        public bool Throttled { get; private set; }
        public double AverageMs => _emaMs;

        public void Reset()
        {
            _hasSample = false;
            _goodSeconds = 0;
            Throttled = false;
        }

        public void Sample(double frameMs, int limitMs)
        {
            if (frameMs <= 0 || double.IsNaN(frameMs)) return;
            // A single huge frame (alt-tab, autosave) should not dominate the average.
            if (frameMs > 1000) frameMs = 1000;
            _emaMs = _hasSample ? _emaMs + (frameMs - _emaMs) * 0.05 : frameMs;
            _hasSample = true;
            if (limitMs <= 0)
            {
                Throttled = false;
                return;
            }
            if (!Throttled)
            {
                if (_emaMs > limitMs)
                {
                    Throttled = true;
                    _goodSeconds = 0;
                }
            }
            else if (_emaMs < limitMs * 0.8)
            {
                _goodSeconds += frameMs / 1000.0;
                if (_goodSeconds >= 2.0) Throttled = false;
            }
            else
            {
                _goodSeconds = 0;
            }
        }
    }
}
