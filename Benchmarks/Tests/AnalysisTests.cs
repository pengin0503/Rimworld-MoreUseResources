using System;
using System.Collections.Generic;
using System.Linq;
using MoreUseResources.Benchmarks.Analysis;
using MoreUseResources.Benchmarks.Running;
using Xunit;

namespace MoreUseResources.Benchmarks.Tests;

public class StatisticsTests
{
    [Fact]
    public void PercentilesMatchType7()
    {
        // numpy.percentile([1, 2, 3, 4, 10], [50, 95, 25]) -> 3.0, 8.8, 2.0
        double[] v = { 1, 2, 3, 4, 10 };
        Assert.Equal(3.0, Statistics.PercentileSorted(v, 0.5), 9);
        Assert.Equal(8.8, Statistics.PercentileSorted(v, 0.95), 9);
        Assert.Equal(2.0, Statistics.PercentileSorted(v, 0.25), 9);
        Assert.Equal(7.0, Statistics.Percentile(new double[] { 7 }, 0.95));
        Assert.True(double.IsNaN(Statistics.PercentileSorted(Array.Empty<double>(), 0.5)));
    }

    [Fact]
    public void SummaryIgnoresNonFiniteValuesAndFlagsSmallSamples()
    {
        MetricSummary s = Statistics.Summarize(new[] { 2.0, 4.0, double.NaN, 6.0, double.PositiveInfinity })!;
        Assert.Equal(3, s.N);
        Assert.Equal(4.0, s.Median);
        Assert.Equal(4.0, s.Mean);
        Assert.Equal(2.0, s.StdDev, 9);
        Assert.Equal(0.5, s.CoefficientOfVariation, 9);
        Assert.False(s.P95Reliable);
        Assert.Null(Statistics.Summarize(new[] { double.NaN }));
        Assert.True(Statistics.Summarize(Enumerable.Range(0, 20).Select(i => (double)i))!.P95Reliable);
    }

    [Fact]
    public void TheilSenIgnoresSingleOutliers()
    {
        var points = Enumerable.Range(0, 50).Select(i => ((double)i, 2.0 * i + 1)).ToList();
        points[25] = (25, 5000);
        Assert.Equal(2.0, Statistics.TheilSenSlope(points), 6);
        Assert.Equal(0, Statistics.TheilSenSlope(new List<(double, double)> { (1, 1) }));
    }

    [Fact]
    public void DownsampleKeepsTheTrend()
    {
        var points = Enumerable.Range(0, 10_000).Select(i => ((double)i, 3.0 * i)).ToList();
        List<(double X, double Y)> d = Statistics.Downsample(points, 100);
        Assert.Equal(100, d.Count);
        Assert.Equal(3.0, Statistics.TheilSenSlope(points, maxPoints: 200), 3);
    }
}

public class LeakDetectorTests
{
    private static List<(double, double)> Series(Func<int, double> f, int n = 40, double hours = 4) =>
        Enumerable.Range(0, n).Select(i => (i * hours / (n - 1), f(i))).ToList();

    [Fact]
    public void FlatSeriesIsOk()
    {
        var r = LeakDetector.Evaluate("managed_heap_mib", "MiB", Series(i => 100 + (i % 3)), LeakDetector.Defaults["managed_heap_mib"]);
        Assert.Equal("ok", r.Verdict);
    }

    [Fact]
    public void SteadyGrowthIsSuspect()
    {
        // 100 MiB -> 140 MiB over 4 hours: 10 MiB/h
        var r = LeakDetector.Evaluate("managed_heap_mib", "MiB", Series(i => 100 + i), LeakDetector.Defaults["managed_heap_mib"]);
        Assert.Equal("suspect", r.Verdict);
        Assert.InRange(r.SlopePerHour, 9, 11);
    }

    [Fact]
    public void LateSpikeAloneIsNotALeak()
    {
        var r = LeakDetector.Evaluate("managed_heap_mib", "MiB", Series(i => i == 39 ? 400 : 100), LeakDetector.Defaults["managed_heap_mib"]);
        Assert.Equal("ok", r.Verdict);
    }

    [Fact]
    public void TooFewPointsAreInsufficient()
    {
        var r = LeakDetector.Evaluate("threads", "count", Series(i => i, n: 4), LeakDetector.Defaults["threads"]);
        Assert.Equal("insufficient-data", r.Verdict);
        Assert.Equal("insufficient-data", LeakDetector.Evaluate("threads", "count", new List<(double, double)>(), LeakDetector.Defaults["threads"]).Verdict);
    }
}

public class SummaryAndComparisonTests
{
    private static TrialResult Trial(string condition, int round, bool warmup, double rate, double wall, bool valid = true,
        TrialStatus status = TrialStatus.Ok, bool truncated = false, int workers = 1, string variant = "saturated")
    {
        var r = new TrialResult
        {
            RunId = "t", Sequence = round + 100, Scenario = "audit", ConditionKey = condition,
            Parameters = new Dictionary<string, string> { ["workers"] = workers.ToString(), ["variant"] = variant, ["cache"] = "warm" },
            Round = round, Warmup = warmup, StartedUtc = DateTime.UtcNow, Status = status, Valid = valid, Truncated = truncated,
            InvalidReason = valid ? null : "cold page cache not reproduced",
        };
        r.Metrics["items_per_s"] = rate;
        r.Metrics["wall_ms"] = wall;
        return r;
    }

    [Fact]
    public void OnlyCountableTrialsAreSummarizedAndTruncatedTimesAreDropped()
    {
        var trials = new List<TrialResult>
        {
            Trial("c", -1, warmup: true, rate: 1000, wall: 1),
            Trial("c", 0, false, 10, 100),
            Trial("c", 1, false, 20, 200),
            Trial("c", 2, false, 30, 300, truncated: true),
            Trial("c", 3, false, 999, 1, valid: false),
            Trial("c", 4, false, 999, 1, status: TrialStatus.Failed),
        };
        var cond = SummaryBuilder.BuildCondition("c", trials);
        var metrics = (SortedDictionary<string, MetricSummary>)cond["metrics"]!;
        Assert.Equal(3, cond["countable"]);
        Assert.Equal(1, cond["truncated"]);
        Assert.Equal(3, metrics["items_per_s"].N);
        Assert.Equal(20, metrics["items_per_s"].Median);
        Assert.Equal(2, metrics["wall_ms"].N); // the truncated trial has no completion time
        Assert.Equal(150, metrics["wall_ms"].Median);
        var notCounted = (Dictionary<string, int>)cond["not_counted"]!;
        Assert.Equal(1, notCounted["cold page cache not reproduced"]);
        Assert.Equal(1, cond["failed"]);
    }

    [Fact]
    public void PlateauIsTheFirstCountNoLargerCountClearlyBeats()
    {
        ScalingPoint P(int w, double med, double min, double max) => new(w, 5, med, min, max, min, max, 0, 0, 0, 0, 0, "");
        var points = new List<ScalingPoint> { P(1, 100, 95, 105), P(2, 190, 185, 195), P(4, 200, 150, 260), P(8, 205, 180, 230) };
        Assert.Equal(2, Analyzer.Plateau(points));
        var growing = new List<ScalingPoint> { P(1, 100, 95, 105), P(2, 190, 185, 195), P(4, 380, 370, 390) };
        Assert.Equal(4, Analyzer.Plateau(growing));
    }

    [Fact]
    public void ComparisonNeedsSeparatedRangesAndThreePercent()
    {
        MetricSummary S(double min, double med, double max, int n = 5) => new(n, min, max, med, 0, 0, med, min, max, 0);
        Assert.Equal("改善", Comparer.Row("a", "c", "items_per_s", S(90, 100, 110), S(120, 130, 140), higherBetter: true).Verdict);
        Assert.Equal("悪化", Comparer.Row("a", "c", "wall_ms", S(90, 100, 110), S(120, 130, 140), higherBetter: false).Verdict);
        Assert.Equal("差なし", Comparer.Row("a", "c", "items_per_s", S(90, 100, 125), S(120, 130, 140), higherBetter: true).Verdict);
        Assert.Equal("差なし", Comparer.Row("a", "c", "items_per_s", S(100, 100, 100), S(101, 101, 101), higherBetter: true).Verdict);
        Assert.Equal("試行不足", Comparer.Row("a", "c", "items_per_s", S(1, 1, 1, n: 2), S(5, 5, 5), higherBetter: true).Verdict);
    }

    [Fact]
    public void ConditionsAreOrderedReproduciblyPerRound()
    {
        var conditions = Enumerable.Range(0, 8).Select(i => Condition.Of("audit", ("workers", i))).ToList();
        var a = BenchmarkRunner.Order(conditions, "audit", 0, 7);
        var b = BenchmarkRunner.Order(conditions, "audit", 0, 7);
        var c = BenchmarkRunner.Order(conditions, "audit", 1, 7);
        Assert.Equal(a.Select(x => x.Key), b.Select(x => x.Key));
        Assert.Equal(conditions.Select(x => x.Key).OrderBy(x => x), a.Select(x => x.Key).OrderBy(x => x));
        Assert.NotEqual(a.Select(x => x.Key), c.Select(x => x.Key));
        Assert.Equal(BenchmarkRunner.StableHash("audit"), BenchmarkRunner.StableHash("audit"));
    }

    [Fact]
    public void ConditionKeysAreSortedAndParsed()
    {
        Condition c = Condition.Of("audit", ("workers", 4), ("cache", "cold"), ("variant", "cadence"));
        Assert.Equal("cache=cold;variant=cadence;workers=4", c.Key);
        Assert.Equal(4, c.Workers);
        Assert.Equal(CacheState.Cold, c.Cache);
        Assert.Equal(CacheState.Unmanaged, Condition.Of("x", ("meta", "cold")).Cache);
    }

    [Fact]
    public void RatesUseActiveTimeWhenGiven()
    {
        var m = new Dictionary<string, double> { ["wall_ms"] = 2000, ["active_ms"] = 1000, ["items"] = 50, ["bytes_mib"] = 10 };
        BenchmarkRunner.AddRates(m);
        Assert.Equal(50, m["items_per_s"]);
        Assert.Equal(10, m["mib_per_s"]);
    }
}
