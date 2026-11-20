using System;
using System.Collections.Generic;
using System.Linq;
using MoreUseResources.Benchmarks.Running;

namespace MoreUseResources.Benchmarks.Analysis;

/// <summary>
/// Per-condition distributions over the countable trials only. Warm-up trials, failed, interrupted
/// and skipped trials, and trials whose cache state could not be reproduced are listed, never
/// averaged in. A trial cut by the timeout keeps its rates but not its completion times.
/// </summary>
public static class SummaryBuilder
{
    public const int Schema = 1;

    /// <summary>Completion-time metrics that a truncated trial cannot report.</summary>
    public static bool IsCompletionTime(string metric) =>
        metric is "wall_ms" or "active_ms" or "cpu_ms" or "cpu_user_ms" or "cpu_sys_ms"
        || metric.StartsWith("time_to_", StringComparison.Ordinal)
        || (metric.StartsWith("consumer", StringComparison.Ordinal) && metric.EndsWith("_ms", StringComparison.Ordinal));

    public static Dictionary<string, object?> Build(string runId, Dictionary<string, object?> metadata, IReadOnlyList<Scenario> catalog,
        IReadOnlyList<TrialResult> results, bool interrupted, string? stopReason)
    {
        var scenarios = new List<object>();
        foreach (var group in results.GroupBy(r => r.Scenario).OrderBy(g => Array.FindIndex(catalog.ToArray(), s => s.Id == g.Key)))
        {
            Scenario? s = catalog.FirstOrDefault(x => x.Id == group.Key);
            var conditions = new List<object>();
            foreach (var cond in group.Where(r => r.Status != TrialStatus.Skipped).GroupBy(r => r.ConditionKey).OrderBy(c => c.Key, Analyzer.KeyOrder))
                conditions.Add(BuildCondition(cond.Key, cond.ToList()));
            scenarios.Add(new Dictionary<string, object?>
            {
                ["id"] = group.Key,
                ["title"] = s?.Title,
                ["production_code"] = s?.ProductionCode,
                ["not_reproduced"] = s?.NotReproduced,
                ["skipped"] = group.Where(r => r.Status == TrialStatus.Skipped).Select(r => r.Error).Distinct().ToList(),
                ["conditions"] = conditions,
            });
        }
        return new Dictionary<string, object?>
        {
            ["schema"] = Schema,
            ["run_id"] = runId,
            ["generated_utc"] = DateTime.UtcNow,
            ["interrupted"] = interrupted,
            ["stop_reason"] = stopReason,
            ["code"] = metadata.GetValueOrDefault("code"),
            ["machine"] = metadata.GetValueOrDefault("machine"),
            ["dataset"] = metadata.GetValueOrDefault("dataset"),
            ["options"] = metadata.GetValueOrDefault("options"),
            ["counts"] = Counts(results),
            ["scenarios"] = scenarios,
        };
    }

    public static Dictionary<string, object?> BuildCondition(string key, IReadOnlyList<TrialResult> trials)
    {
        var countable = trials.Where(t => t.Countable).ToList();
        var metrics = new SortedDictionary<string, MetricSummary>(StringComparer.Ordinal);
        foreach (string name in countable.SelectMany(t => t.Metrics.Keys).Distinct())
        {
            IEnumerable<double> values = countable
                .Where(t => !(t.Truncated && IsCompletionTime(name)))
                .Where(t => t.Metrics.ContainsKey(name))
                .Select(t => t.Metrics[name]);
            MetricSummary? m = Statistics.Summarize(values);
            if (m != null) metrics[name] = m;
        }
        return new Dictionary<string, object?>
        {
            ["key"] = key,
            ["parameters"] = trials[0].Parameters,
            ["countable"] = countable.Count,
            ["truncated"] = countable.Count(t => t.Truncated),
            ["warmup"] = trials.Count(t => t.Warmup),
            ["failed"] = trials.Count(t => t.Status == TrialStatus.Failed),
            ["interrupted"] = trials.Count(t => t.Status == TrialStatus.Interrupted),
            ["not_counted"] = trials.Where(t => t.Status == TrialStatus.Ok && !t.Valid && !t.Warmup)
                .GroupBy(t => t.InvalidReason ?? "").ToDictionary(g => g.Key, g => g.Count()),
            ["errors"] = trials.Where(t => t.Error != null).Select(t => t.Error).Distinct().Take(5).ToList(),
            ["notes"] = trials.SelectMany(t => t.Notes).Distinct().Take(10).ToList(),
            ["p95_reliable"] = countable.Count >= 20,
            ["metrics"] = metrics,
        };
    }

    public static Dictionary<string, int> Counts(IReadOnlyList<TrialResult> results) => new()
    {
        ["total"] = results.Count,
        ["countable"] = results.Count(r => r.Countable),
        ["warmup"] = results.Count(r => r.Warmup),
        ["failed"] = results.Count(r => r.Status == TrialStatus.Failed),
        ["interrupted"] = results.Count(r => r.Status == TrialStatus.Interrupted),
        ["skipped"] = results.Count(r => r.Status == TrialStatus.Skipped),
        ["not_counted"] = results.Count(r => r.Status == TrialStatus.Ok && !r.Valid && !r.Warmup),
        ["truncated"] = results.Count(r => r.Countable && r.Truncated),
    };
}
