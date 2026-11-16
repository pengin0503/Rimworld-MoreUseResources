using System;
using System.IO;
using System.Runtime;
using System.Text.Json;

namespace MoreUseResources.Core;

public sealed record GcBenchmark(GamePhase Phase, double CollectionsPerMinute, double PauseMsPerMinute,
    long HeapBytes, double AllocatedMiBPerSecond, bool ServerGc, GCLatencyMode Latency, long CommittedBytes,
    int? SelectedSpeed = null, float? EffectiveSpeed = null)
{
    public static GcBenchmark From(GcSnapshot sample, GamePhase phase, int? selectedSpeed = null, float? effectiveSpeed = null)
    {
        double scale = 60 / sample.WindowSeconds;
        return new GcBenchmark(phase,
            (sample.Gen0Collections + (long)sample.Gen1Collections + sample.Gen2Collections) * scale,
            sample.PauseMs * scale, sample.HeapBytes, sample.AllocatedMiBPerSecond, sample.ServerGc, sample.Latency, sample.CommittedBytes,
            selectedSpeed, effectiveSpeed);
    }

    public bool TryCompare(GcBenchmark baseline, out GcDifference difference)
    {
        difference = default;
        if (!CanCompareWith(baseline)) return false;
        difference = new GcDifference(CollectionsPerMinute - baseline.CollectionsPerMinute,
            PauseMsPerMinute - baseline.PauseMsPerMinute, HeapBytes - baseline.HeapBytes,
            AllocatedMiBPerSecond - baseline.AllocatedMiBPerSecond, CommittedBytes - baseline.CommittedBytes);
        return true;
    }

    public bool CanCompareWith(GcBenchmark baseline) =>
        Phase == baseline.Phase && (Phase is not (GamePhase.PlayingNormal or GamePhase.PlayingFast)
            || (SelectedSpeed.HasValue && SelectedSpeed == baseline.SelectedSpeed
                && EffectiveSpeed is > 0 && EffectiveSpeed == baseline.EffectiveSpeed));

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
