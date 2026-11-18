using System;
using System.Collections.Generic;
using System.Linq;

namespace MoreUseResources.Benchmarks.Analysis;

/// <summary>Distribution of one metric over the valid trials of one condition.</summary>
public sealed record MetricSummary(int N, double Min, double Max, double Mean, double StdDev, double CoefficientOfVariation,
    double Median, double P05, double P95, double Iqr)
{
    /// <summary>A 95th percentile of fewer than 20 values is close to the maximum and is flagged as such.</summary>
    public bool P95Reliable => N >= 20;
}

public static class Statistics
{
    public static MetricSummary? Summarize(IEnumerable<double> values)
    {
        double[] v = values.Where(double.IsFinite).ToArray();
        if (v.Length == 0) return null;
        Array.Sort(v);
        double mean = v.Average();
        double sd = v.Length > 1 ? Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / (v.Length - 1)) : 0;
        double cv = Math.Abs(mean) > 1e-12 ? sd / Math.Abs(mean) : 0;
        return new MetricSummary(v.Length, v[0], v[^1], mean, sd, cv, PercentileSorted(v, 0.5),
            PercentileSorted(v, 0.05), PercentileSorted(v, 0.95), PercentileSorted(v, 0.75) - PercentileSorted(v, 0.25));
    }

    /// <summary>Linear interpolation between closest ranks (Hyndman–Fan type 7, as in R and NumPy).</summary>
    public static double PercentileSorted(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return double.NaN;
        if (sorted.Count == 1) return sorted[0];
        double h = (sorted.Count - 1) * Math.Clamp(p, 0, 1);
        int lo = (int)Math.Floor(h);
        int hi = Math.Min(lo + 1, sorted.Count - 1);
        return sorted[lo] + (h - lo) * (sorted[hi] - sorted[lo]);
    }

    public static double Percentile(IEnumerable<double> values, double p)
    {
        double[] v = values.Where(double.IsFinite).ToArray();
        Array.Sort(v);
        return PercentileSorted(v, p);
    }

    /// <summary>
    /// Theil–Sen estimator: the median of all pairwise slopes. Robust against single outliers such
    /// as a collection or a page-cache flush in a long run. Inputs are reduced to at most
    /// <paramref name="maxPoints"/> bin medians first to bound the pair count.
    /// </summary>
    public static double TheilSenSlope(IReadOnlyList<(double X, double Y)> points, int maxPoints = 400)
    {
        IReadOnlyList<(double X, double Y)> p = points.Count > maxPoints ? Downsample(points, maxPoints) : points;
        var slopes = new List<double>(p.Count * (p.Count - 1) / 2);
        for (int i = 0; i < p.Count; i++)
        for (int j = i + 1; j < p.Count; j++)
        {
            double dx = p[j].X - p[i].X;
            if (Math.Abs(dx) > 1e-12) slopes.Add((p[j].Y - p[i].Y) / dx);
        }
        if (slopes.Count == 0) return 0;
        slopes.Sort();
        return PercentileSorted(slopes, 0.5);
    }

    /// <summary>Replaces consecutive runs by the median of each run (x and y separately).</summary>
    public static List<(double X, double Y)> Downsample(IReadOnlyList<(double X, double Y)> points, int bins)
    {
        var result = new List<(double X, double Y)>(bins);
        if (points.Count == 0 || bins <= 0) return result;
        double per = points.Count / (double)bins;
        for (int b = 0; b < bins; b++)
        {
            int start = (int)Math.Floor(b * per);
            int end = Math.Min(points.Count, (int)Math.Floor((b + 1) * per));
            if (end <= start) continue;
            var xs = new double[end - start];
            var ys = new double[end - start];
            for (int i = start; i < end; i++)
            {
                xs[i - start] = points[i].X;
                ys[i - start] = points[i].Y;
            }
            Array.Sort(xs);
            Array.Sort(ys);
            result.Add((PercentileSorted(xs, 0.5), PercentileSorted(ys, 0.5)));
        }
        return result;
    }
}
