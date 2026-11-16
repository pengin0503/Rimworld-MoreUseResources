using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MoreUseResources.Core
{
    public readonly struct LoadStageTiming
    {
        public LoadStageTiming(string name, double milliseconds, int calls)
        {
            Name = name;
            Milliseconds = milliseconds;
            Calls = calls;
        }

        public string Name { get; }
        public double Milliseconds { get; }
        public int Calls { get; }
    }

    /// <summary>
    /// Accumulates how long named stages of the game's data load took. It only measures: the
    /// caller wraps existing game methods and reports elapsed time here. Safe from any thread.
    /// Nested stages are reported as measured, so a parent's time includes its children.
    /// </summary>
    public sealed class LoadStageRecorder
    {
        public const int MaxStages = 64;

        private sealed class Entry
        {
            public double Milliseconds;
            public int Calls;
        }

        private readonly object _gate = new object();
        private readonly List<string> _order = new List<string>();
        private readonly Dictionary<string, Entry> _stages = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private double? _startupMs;
        private double? _loadMs;
        private long _peakWorkingSet;

        /// <summary>true once <see cref="Complete"/> was called; later stage reports are ignored.</summary>
        public bool Completed { get { lock (_gate) { return _loadMs.HasValue; } } }

        public void Add(string stage, double milliseconds)
        {
            if (string.IsNullOrEmpty(stage) || double.IsNaN(milliseconds) || milliseconds < 0) return;
            lock (_gate)
            {
                if (_loadMs.HasValue) return;
                if (!_stages.TryGetValue(stage, out Entry? entry))
                {
                    if (_order.Count >= MaxStages) return;
                    entry = new Entry();
                    _stages[stage] = entry;
                    _order.Add(stage);
                }
                entry.Milliseconds += milliseconds;
                entry.Calls++;
            }
        }

        /// <summary>Time from process start until this mod began observing; negative values are ignored.</summary>
        public void SetStartup(double milliseconds)
        {
            if (double.IsNaN(milliseconds) || milliseconds < 0) return;
            lock (_gate) { _startupMs ??= milliseconds; }
        }

        /// <summary>Closes the record: time from the mod's start until play data was loaded.</summary>
        public void Complete(double millisecondsSinceModStart, long peakWorkingSetBytes)
        {
            lock (_gate)
            {
                if (_loadMs.HasValue) return;
                _loadMs = Math.Max(0, millisecondsSinceModStart);
                _peakWorkingSet = Math.Max(0, peakWorkingSetBytes);
            }
        }

        public List<LoadStageTiming> Stages
        {
            get
            {
                lock (_gate)
                {
                    var list = new List<LoadStageTiming>(_order.Count);
                    foreach (string name in _order)
                    {
                        Entry entry = _stages[name];
                        list.Add(new LoadStageTiming(name, entry.Milliseconds, entry.Calls));
                    }
                    return list;
                }
            }
        }

        /// <summary>One line for the log and the status panel. Culture-invariant numbers, seconds with one decimal.</summary>
        public string Report()
        {
            lock (_gate)
            {
                var text = new StringBuilder();
                if (_startupMs.HasValue) text.Append("process start to mod ").Append(Seconds(_startupMs.Value));
                if (_loadMs.HasValue)
                {
                    if (text.Length > 0) text.Append("; ");
                    text.Append("mod to loaded ").Append(Seconds(_loadMs.Value));
                }
                foreach (string name in _order)
                {
                    Entry entry = _stages[name];
                    if (text.Length > 0) text.Append("; ");
                    text.Append(name).Append(' ').Append(Seconds(entry.Milliseconds));
                    if (entry.Calls > 1) text.Append(" (").Append(entry.Calls.ToString(CultureInfo.InvariantCulture)).Append(" calls)");
                }
                if (_loadMs.HasValue && _peakWorkingSet > 0)
                {
                    text.Append("; peak working set ")
                        .Append((_peakWorkingSet / (1024.0 * 1024.0)).ToString("F0", CultureInfo.InvariantCulture)).Append(" MiB");
                }
                return text.Length == 0 ? "no data" : text.ToString();
            }
        }

        private static string Seconds(double milliseconds)
        {
            return (milliseconds / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + " s";
        }
    }
}
