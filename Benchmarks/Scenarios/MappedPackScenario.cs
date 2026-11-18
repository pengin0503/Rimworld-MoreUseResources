using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32.SafeHandles;
using MoreUseResources.Benchmarks.Measurement;
using MoreUseResources.Benchmarks.Running;
using MoreUseResources.Core;

namespace MoreUseResources.Benchmarks.Scenarios;

/// <summary>
/// Memory-mapped residency of Assets.zip packs (RamResidentPacks): the touch pass, the effect on a
/// reader doing random 64 KiB reads into the packs (as the engine reads entries), shrinking the
/// limit while views are open, and ReleaseAll. limit=none is the same reader without residency.
/// Mapped pages show as RssFile, not RssAnon: they are page cache that this process pins by mapping.
/// </summary>
public sealed class MappedPackScenario : Scenario
{
    private const int ReadBytes = 64 * 1024;

    public override string Id => "mapped-pack";
    public override string Title => "メモリマップによるパック常駐・解放";
    public override string ProductionCode => "MappedPackResidency.SetSources/Pump/MapAndTouch/ReleaseAll";
    public override string NotReproduced =>
        "Windows のワーキングセット管理（マップしたページがスタンバイへ移る条件）、エンジンが実際に読むエントリの順序と量";
    public override bool RequiresPacks => true;

    public override IReadOnlyList<Condition> Conditions(RunEnvironment env)
    {
        var list = new List<Condition>();
        foreach (string limit in new[] { "none", "half", "full" })
        foreach (CacheState c in Caches(env))
            list.Add(Condition.Of(Id, ("limit", limit), ("cache", CacheName(c))));
        return list;
    }

    public override Trial CreateTrial(Condition condition, RunEnvironment env) => new MappedTrial(condition, env);

    private sealed class MappedTrial : Trial
    {
        private readonly Condition _c;
        private readonly RunEnvironment _env;
        private readonly List<string> _packs;
        private WorkerPool? _pool;
        private MappedPackResidency? _residency;

        public MappedTrial(Condition c, RunEnvironment env)
        {
            _c = c;
            _env = env;
            _packs = env.Dataset.Packs.Where(p => string.Equals(Path.GetFileName(p), EnginePackFiles.AssetPackFileName, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        public override IReadOnlyList<string> CacheTargets => _packs;

        public override IReadOnlyList<Gauge> Gauges => _residency == null ? Array.Empty<Gauge>()
            : new[] { new Gauge("mapped", () => _residency.Snapshot.MappedBytes) };

        private long PackBytes => _packs.Sum(p => new FileInfo(p).Length);

        public override void Prepare(TrialContext ctx)
        {
            if (_packs.Count == 0) throw new InvalidOperationException("no Assets.zip among the packs");
            _pool = Common.Pool(1);
            _residency = new MappedPackResidency(_pool);
            _residency.SetSources(_packs);
        }

        public override void Execute(TrialContext ctx)
        {
            MappedPackResidency r = _residency!;
            string limitName = _c.Get("limit")!;
            long total = PackBytes;
            long limit = limitName switch { "full" => total, "half" => total / 2, _ => 0 };
            ctx.Metric("pack_mib", total / 1048576.0);
            ctx.Metric("packs", _packs.Count);
            ctx.Metric("limit_mib", limit / 1048576.0);
            if (limit > 0)
            {
                ctx.Measure("touch", () => ctx.WaitUntil(() =>
                {
                    r.Pump(true, limit, 0);
                    return r.Snapshot.Passes >= 1;
                }, _env.Options.PumpIntervalMs));
                PackResidencySnapshot s = r.Snapshot;
                ctx.Metric("mapped_files", s.MappedFiles);
                ctx.Metric("mapped_mib", s.MappedBytes / 1048576.0);
                ctx.Metric("deferred", s.Deferred);
                ctx.Metric("errors", s.Errors);
                if (s.LastError != null) ctx.Note("residency error: " + s.LastError);
                ctx.Metric("items", s.MappedFiles);
                ctx.Metric("bytes_mib", s.MappedBytes / 1048576.0);
                r.Pump(false, limit, 0); // stop new work, keep views (as when RAM work pauses)
            }
            Residency before = PageCache.Measure(_packs, ctx.Token);
            if (!double.IsNaN(before.Fraction)) ctx.Metric("consumer_resident_before", before.Fraction);
            long read = 0;
            ctx.Measure("consumer", () => read = RandomReads(ctx));
            ctx.Metric("consumer_reads", read / ReadBytes);
            if (ctx.Metrics.TryGetValue("consumer.wall_ms", out double ms)) ctx.Metric("consumer_ms", ms);
            if (limit > 0)
            {
                ResourceSnapshot held = ProcessProbe.Capture(includeThreads: false);
                // Shrinking the limit below what is mapped closes every view and starts over.
                long shrinkTo = r.Snapshot.MappedBytes / 2;
                int filesBeforeShrink = r.Snapshot.MappedFiles;
                if (shrinkTo > 0)
                {
                    ctx.Measure("shrink", () => r.Pump(false, shrinkTo, 0));
                    ctx.Metric("shrink_files_closed", filesBeforeShrink - r.Snapshot.MappedFiles);
                    // Map again up to the original limit for the release measurement.
                    ctx.WaitUntil(() => { r.Pump(true, limit, 0); return r.Snapshot.Passes >= 2; }, _env.Options.PumpIntervalMs);
                    r.Pump(false, limit, 0);
                    held = ProcessProbe.Capture(includeThreads: false);
                }
                ctx.Measure("release", () => r.ReleaseAll());
                ResourceSnapshot released = ProcessProbe.Capture(includeThreads: false);
                if (held.RssFileBytes.HasValue && released.RssFileBytes.HasValue)
                {
                    ctx.Metric("rss_file_held_mib", held.RssFileBytes.Value / 1048576.0);
                    ctx.Metric("rss_file_released_mib", (held.RssFileBytes.Value - released.RssFileBytes.Value) / 1048576.0);
                }
                ctx.Metric("mapped_after_release_mib", r.Snapshot.MappedBytes / 1048576.0);
            }
        }

        /// <summary>Seeded random 64 KiB reads, about one per 4 MiB of pack, at least 64 per pack.</summary>
        private long RandomReads(TrialContext ctx)
        {
            var random = new Random(_env.Options.Seed);
            byte[] buffer = new byte[ReadBytes];
            long total = 0;
            foreach (string pack in _packs)
            {
                using SafeFileHandle h = File.OpenHandle(pack, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
                long length = RandomAccess.GetLength(h);
                if (length <= ReadBytes) continue;
                int count = (int)Math.Max(64, length / (4L << 20));
                for (int i = 0; i < count; i++)
                {
                    ctx.Token.ThrowIfCancellationRequested();
                    long offset = random.NextInt64(0, length - ReadBytes) & ~4095L;
                    total += RandomAccess.Read(h, buffer, offset);
                }
            }
            return total;
        }

        public override void Dispose()
        {
            _residency?.Dispose();
            _pool?.Dispose();
        }
    }
}
