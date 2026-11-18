using System;
using System.Collections.Generic;

namespace MoreUseResources.Benchmarks.Running;

public enum TrialStatus { Ok, Failed, Interrupted, Skipped }

/// <summary>Everything recorded about one trial; written to trials.jsonl, trials.csv and metrics.csv.</summary>
public sealed class TrialResult
{
    public required string RunId { get; init; }
    public required int Sequence { get; init; }
    public required string Scenario { get; init; }
    public required string ConditionKey { get; init; }
    public required IReadOnlyDictionary<string, string> Parameters { get; init; }
    public required int Round { get; init; }
    public required bool Warmup { get; init; }
    public required DateTime StartedUtc { get; init; }
    public TrialStatus Status { get; set; } = TrialStatus.Ok;
    public bool Valid { get; set; } = true;
    public string? InvalidReason { get; set; }
    public bool Truncated { get; set; }
    public string CacheRequested { get; set; } = "unmanaged";
    public bool? CacheVerified { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, double> Metrics { get; init; } = new(StringComparer.Ordinal);
    public List<string> Notes { get; init; } = new();

    /// <summary>Counted in summaries: finished, measured, not a warm-up, cache state reproduced.</summary>
    public bool Countable => Status == TrialStatus.Ok && Valid && !Warmup;
}
