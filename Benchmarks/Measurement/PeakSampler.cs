using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace MoreUseResources.Benchmarks.Measurement;

/// <summary>A named value read periodically during a trial, for example the mod's native copy bytes.</summary>
public sealed record Gauge(string Name, Func<long> Read);

/// <summary>
/// Samples resident memory and caller gauges on a background thread and keeps the peaks.
/// <c>/proc/self/status</c> is read through one open handle into a reused buffer, so sampling does
/// not allocate per sample and adds next to nothing to the allocation figures of the trial.
/// </summary>
public sealed class PeakSampler : IDisposable
{
    private static readonly byte[] RssAnonKey = "RssAnon:"u8.ToArray();
    private static readonly byte[] RssFileKey = "RssFile:"u8.ToArray();
    private static readonly byte[] RssKey = "VmRSS:"u8.ToArray();

    private readonly int _intervalMs;
    private readonly IReadOnlyList<Gauge> _gauges;
    private readonly long[] _gaugePeaks;
    private readonly byte[] _buffer = new byte[8192];
    private readonly SafeFileHandle? _status;
    private Thread? _thread;
    private volatile bool _stop;

    public PeakSampler(int intervalMs, IReadOnlyList<Gauge>? gauges = null)
    {
        _intervalMs = Math.Max(5, intervalMs);
        _gauges = gauges ?? Array.Empty<Gauge>();
        _gaugePeaks = new long[_gauges.Count];
        if (OperatingSystem.IsLinux())
        {
            try { _status = File.OpenHandle("/proc/self/status", FileMode.Open, FileAccess.Read); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status = null; }
        }
    }

    public long PeakRssBytes { get; private set; }
    public long PeakRssAnonBytes { get; private set; }
    public long PeakRssFileBytes { get; private set; }
    public int Samples { get; private set; }

    public long PeakOf(string gauge)
    {
        for (int i = 0; i < _gauges.Count; i++)
            if (_gauges[i].Name == gauge) return _gaugePeaks[i];
        return 0;
    }

    public IReadOnlyList<Gauge> Gauges => _gauges;

    public void Start()
    {
        SampleOnce();
        _thread = new Thread(Loop) { IsBackground = true, Name = "Bench-Sampler", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join();
        _thread = null;
        SampleOnce();
    }

    private void Loop()
    {
        while (!_stop)
        {
            Thread.Sleep(_intervalMs);
            SampleOnce();
        }
    }

    private void SampleOnce()
    {
        if (_status != null)
        {
            try
            {
                int read = RandomAccess.Read(_status, _buffer, 0);
                ReadOnlySpan<byte> text = _buffer.AsSpan(0, read);
                if (TryFindKib(text, RssKey, out long rss) && rss > PeakRssBytes) PeakRssBytes = rss;
                if (TryFindKib(text, RssAnonKey, out long anon) && anon > PeakRssAnonBytes) PeakRssAnonBytes = anon;
                if (TryFindKib(text, RssFileKey, out long file) && file > PeakRssFileBytes) PeakRssFileBytes = file;
            }
            catch (IOException) { }
        }
        for (int i = 0; i < _gauges.Count; i++)
        {
            long value;
            try { value = _gauges[i].Read(); }
            catch (Exception) { continue; }
            if (value > _gaugePeaks[i]) _gaugePeaks[i] = value;
        }
        Samples++;
    }

    /// <summary>Finds "Key:   123 kB" and returns bytes. Allocation free.</summary>
    internal static bool TryFindKib(ReadOnlySpan<byte> text, ReadOnlySpan<byte> key, out long bytes)
    {
        bytes = 0;
        int at = text.IndexOf(key);
        if (at < 0) return false;
        int i = at + key.Length;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
        long value = 0;
        int digits = 0;
        while (i < text.Length && text[i] >= '0' && text[i] <= '9')
        {
            value = value * 10 + (text[i] - '0');
            i++;
            digits++;
        }
        if (digits == 0) return false;
        bytes = value * 1024;
        return true;
    }

    public void Dispose()
    {
        if (_thread != null) Stop();
        _status?.Dispose();
    }
}
