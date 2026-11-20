using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Threading;

namespace MoreUseResources.Benchmarks.Measurement;

/// <summary>One stop-the-world interval of the garbage collector.</summary>
public readonly record struct GcPause(DateTime EndUtc, double Milliseconds);

/// <summary>
/// Collects individual collector pauses from the runtime's own events (suspension begin to
/// restart end), so that the longest and the 95th percentile pause can be reported per trial.
/// <see cref="GC.GetTotalPauseDuration"/> only gives the sum. Events arrive on a dispatch thread
/// with a small delay; <see cref="Between"/> waits until the expected number of pauses arrived.
/// </summary>
public sealed class GcPauseListener : EventListener
{
    private const string RuntimeSource = "Microsoft-Windows-DotNETRuntime";
    private const EventKeywords GcKeyword = (EventKeywords)0x1;
    private readonly object _gate = new();
    private readonly List<GcPause> _pauses = new();
    private DateTime? _suspendStart;
    private long _eventsSeen;

    public long EventsSeen => Interlocked.Read(ref _eventsSeen);

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == RuntimeSource) EnableEvents(source, EventLevel.Informational, GcKeyword);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        Interlocked.Increment(ref _eventsSeen);
        // Event names carry a version suffix in some runtimes (GCSuspendEEBegin_V1).
        string? name = e.EventName;
        if (name == null) return;
        if (name.StartsWith("GCSuspendEEBegin", StringComparison.Ordinal))
        {
            lock (_gate) _suspendStart = e.TimeStamp.ToUniversalTime();
        }
        else if (name.StartsWith("GCRestartEEEnd", StringComparison.Ordinal))
        {
            lock (_gate)
            {
                if (_suspendStart is not { } start) return;
                DateTime end = e.TimeStamp.ToUniversalTime();
                double ms = (end - start).TotalMilliseconds;
                if (ms >= 0) _pauses.Add(new GcPause(end, ms));
                _suspendStart = null;
                if (_pauses.Count > 200_000) _pauses.RemoveRange(0, 100_000);
            }
        }
    }

    /// <summary>
    /// Pauses that ended within the window. Waits up to <paramref name="maxWaitMs"/> for at least
    /// <paramref name="expected"/> pauses, because each collection suspends the runtime at least once.
    /// </summary>
    public List<GcPause> Between(DateTime startUtc, DateTime endUtc, int expected, int maxWaitMs = 2000)
    {
        int waited = 0;
        while (true)
        {
            var found = new List<GcPause>();
            lock (_gate)
            {
                foreach (GcPause p in _pauses)
                    if (p.EndUtc >= startUtc && p.EndUtc <= endUtc.AddMilliseconds(50)) found.Add(p);
            }
            if (found.Count >= expected || waited >= maxWaitMs) return found;
            Thread.Sleep(25);
            waited += 25;
        }
    }
}
