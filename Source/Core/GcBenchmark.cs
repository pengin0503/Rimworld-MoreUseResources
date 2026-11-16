using System;
using System.IO;
using System.Runtime;
using System.Text.Json;

namespace MoreUseResources.Core;

public sealed record GcBenchmark(GamePhase Phase, double CollectionsPerMinute, double PauseMsPerMinute,
    long HeapBytes, double AllocatedMiBPerSecond, bool ServerGc, GCLatencyMode Latency, long CommittedBytes)
{
    public static GcBenchmark From(GcSnapshot sample, GamePhase phase)
    {
        double scale = 60 / sample.WindowSeconds;
        return new GcBenchmark(phase,
            (sample.Gen0Collections + (long)sample.Gen1Collections + sample.Gen2Collections) * scale,
            sample.PauseMs * scale, sample.HeapBytes, sample.AllocatedMiBPerSecond, sample.ServerGc, sample.Latency, sample.CommittedBytes);
    }

    public bool TryCompare(GcBenchmark baseline, out GcDifference difference)
    {
        difference = default;
        if (Phase != baseline.Phase) return false;
        difference = new GcDifference(CollectionsPerMinute - baseline.CollectionsPerMinute,
            PauseMsPerMinute - baseline.PauseMsPerMinute, HeapBytes - baseline.HeapBytes,
            AllocatedMiBPerSecond - baseline.AllocatedMiBPerSecond, CommittedBytes - baseline.CommittedBytes);
        return true;
    }

    public static GcBenchmark? Load(string path)
    {
        try
        {
            using var input = File.OpenRead(path);
            if (input.Length > 4096) return null;
            var sample = JsonSerializer.Deserialize<GcBenchmark>(input);
            return sample is { HeapBytes: >= 0, CommittedBytes: >= 0, CollectionsPerMinute: >= 0, PauseMsPerMinute: >= 0,
                AllocatedMiBPerSecond: >= 0 } && Enum.IsDefined(sample.Phase) && Enum.IsDefined(sample.Latency)
                ? sample : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Save(string path, GcBenchmark sample)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(output, sample);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}

public readonly record struct GcDifference(double CollectionsPerMinute, double PauseMsPerMinute,
    long HeapBytes, double AllocatedMiBPerSecond, long CommittedBytes);
