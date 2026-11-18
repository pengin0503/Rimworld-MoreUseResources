using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MoreUseResources.Benchmarks.Running;
using MoreUseResources.Core;

namespace MoreUseResources.Benchmarks.Scenarios;

/// <summary>
/// Noise floor: the harness alone (sampler, probes) and an idle worker pool, which costs CPU only
/// through its 100 ms wake-ups. Other scenarios' small CPU numbers are read against these.
/// </summary>
public sealed class BaselineScenario : Scenario
{
    private const int IdleMs = 2000;

    public override string Id => "baseline";
    public override string Title => "計測基盤とアイドル時ワーカーの基準値";
    public override string ProductionCode => "WorkerPool (idle workers, no jobs)";

    public override IReadOnlyList<Condition> Conditions(RunEnvironment env)
    {
        var list = new List<Condition> { Condition.Of(Id, ("mode", "harness")) };
        foreach (int w in env.Options.Workers.Distinct().OrderBy(x => x))
            list.Add(Condition.Of(Id, ("mode", "idle-pool"), ("workers", w)));
        return list;
    }

    public override Trial CreateTrial(Condition condition, RunEnvironment env) => new BaselineTrial(condition);

    private sealed class BaselineTrial : Trial
    {
        private readonly Condition _c;
        private WorkerPool? _pool;

        public BaselineTrial(Condition c) => _c = c;

        public override void Prepare(TrialContext ctx)
        {
            if (_c.Get("mode") == "idle-pool")
            {
                _pool = Common.Pool(_c.Workers);
                Thread.Sleep(200); // let the threads start outside the measured window
            }
        }

        public override void Execute(TrialContext ctx)
        {
            ctx.WaitUntil(() => false, 50, limit: TimeSpan.FromMilliseconds(IdleMs));
            ctx.Truncated = false; // the fixed idle period is the measurement, not a timeout
            ctx.Metric("idle_ms", IdleMs);
            if (_pool != null)
            {
                ctx.Metric("workers", _c.Workers);
                Common.NoteOversubscription(ctx, _c.Workers);
            }
        }

        public override void Dispose() => _pool?.Dispose();
    }
}
