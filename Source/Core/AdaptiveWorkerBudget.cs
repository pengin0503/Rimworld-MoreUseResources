using System;

namespace MoreUseResources.Core;

/// <summary>Reduces optional worker admission on sustained degradation within a single play speed.</summary>
public sealed class AdaptiveWorkerBudget
{
    private double baselineFrame;
    private double baselineTps;
    private int baselineSamples;
    private int bad;
    private int good;
    private int limitedWindows;

    public int Percent { get; private set; } = 100;

    public int Limit(int workers) => Scale(workers, Percent);

    public static int Scale(int workers, int percent) =>
        workers == 0 ? 0 : Math.Max(1, (int)Math.Ceiling(workers * (percent / 100d)));

    public void Reset()
    {
        baselineFrame = baselineTps = 0;
        baselineSamples = bad = good = limitedWindows = 0;
        Percent = 100;
    }

    public void Observe(double p95, double tps)
    {
        if (!double.IsFinite(p95) || !double.IsFinite(tps) || p95 <= 0 || tps < 0) return;
        if (baselineSamples < 3)
        {
            baselineSamples++;
            baselineFrame += (p95 - baselineFrame) / baselineSamples;
            baselineTps += (tps - baselineTps) / baselineSamples;
            return;
        }

        bool pressure = p95 > Math.Max(baselineFrame * 1.35, baselineFrame + 3)
            || (baselineTps >= 10 && tps < baselineTps * 0.88);
        if (pressure)
        {
            good = 0;
            if (++bad >= 2)
            {
                Percent = Math.Max(25, Percent / 2);
                bad = 0;
            }
            // Relearn a lasting change in colony load while remaining throttled.
            if (Percent == 25 && ++limitedWindows >= 12)
            {
                baselineFrame = p95;
                baselineTps = tps;
                limitedWindows = 0;
            }
            return;
        }

        bad = limitedWindows = 0;
        if (++good >= 3)
        {
            Percent = Math.Min(100, Percent + 25);
            good = 0;
        }
        if (Percent == 100)
        {
            baselineFrame += (p95 - baselineFrame) * 0.1;
            baselineTps += (tps - baselineTps) * 0.1;
        }
    }
}
