using System;
using System.Collections.Generic;
using System.Linq;

namespace MoreUseResources.Benchmarks.Analysis;

/// <summary>Verdict about one resource over a long run.</summary>
public sealed record LeakVerdict(string Resource, string Unit, int Points, double SpanHours, double SlopePerHour, double First, double Last,
    double Growth, double RelativeGrowth, string Verdict, string Reason);

/// <summary>
/// Decides whether a series sampled after full collections grows over time. The first part of the
/// run (warm-up: caches fill, JIT, pools) is excluded. A resource is "suspect" only when both the
/// robust slope and the total growth exceed their thresholds, so one late spike or a slow drift
/// within noise does not count. With less than an hour of data the verdict is at most "suspect":
/// slow leaks need longer runs to separate from noise.
/// </summary>
public static class LeakDetector
{
    public sealed record Threshold(double SlopePerHour, double MinGrowth, double MinRelativeGrowth);

    public static readonly IReadOnlyDictionary<string, Threshold> Defaults = new Dictionary<string, Threshold>
    {
        ["managed_heap_mib"] = new(2, 4, 0.05),
        ["rss_anon_minus_native_mib"] = new(16, 32, 0.05),
        ["open_files"] = new(1, 8, 0),
        ["mod_threads"] = new(0.5, 2, 0),
        ["threads"] = new(1, 4, 0),
        ["gc_committed_mib"] = new(8, 16, 0.05),
    };

    public static LeakVerdict Evaluate(string resource, string unit, IReadOnlyList<(double Hours, double Value)> series, Threshold t,
        double warmupFraction = 0.1)
    {
        if (series.Count == 0)
            return new LeakVerdict(resource, unit, 0, 0, 0, double.NaN, double.NaN, 0, 0, "insufficient-data", "no samples");
        double start = series[0].Hours, end = series[^1].Hours;
        double cut = start + (end - start) * warmupFraction;
        var points = series.Where(p => p.Hours >= cut).ToList();
        double span = points.Count > 0 ? points[^1].Hours - points[0].Hours : 0;
        if (points.Count < 5 || span <= 0)
            return new LeakVerdict(resource, unit, points.Count, span, 0, points.FirstOrDefault().Value, points.LastOrDefault().Value, 0, 0,
                "insufficient-data", "fewer than 5 checkpoints after warm-up");
        double slope = Statistics.TheilSenSlope(points);
        // Growth between the medians of the first and last fifth, robust to single samples.
        int fifth = Math.Max(1, points.Count / 5);
        double first = Statistics.Percentile(points.Take(fifth).Select(p => p.Value), 0.5);
        double last = Statistics.Percentile(points.Skip(points.Count - fifth).Select(p => p.Value), 0.5);
        double growth = last - first;
        double relative = Math.Abs(first) > 1e-9 ? growth / Math.Abs(first) : 0;
        bool grows = slope > t.SlopePerHour && growth > t.MinGrowth && relative >= t.MinRelativeGrowth;
        string verdict = grows ? "suspect" : "ok";
        string reason = grows
            ? $"slope {slope:F2} {unit}/h > {t.SlopePerHour} and growth {growth:F2} {unit} > {t.MinGrowth}"
            : $"slope {slope:F2} {unit}/h, growth {growth:F2} {unit} within limits";
        if (grows && span < 1) reason += "; run shorter than 1 h, confirm with a longer soak";
        return new LeakVerdict(resource, unit, points.Count, span, slope, first, last, growth, relative, verdict, reason);
    }
}
