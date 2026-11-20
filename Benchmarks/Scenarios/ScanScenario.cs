using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MoreUseResources.Benchmarks.Running;
using MoreUseResources.Core;

namespace MoreUseResources.Benchmarks.Scenarios;

/// <summary>
/// The parsers alone, without hashing: XmlManifestScanner over every XML file and
/// TextureAssetInspector over every PNG/DDS file, all queued at once on the worker pool. This
/// isolates parse cost from the audit's hashing and from the controller's cadence.
/// </summary>
public sealed class ScanScenario : Scenario
{
    public override string Id => "scan";
    public override string Title => "XML解析とテクスチャ検査の単体コスト";
    public override string ProductionCode => "XmlManifestScanner.Scan, TextureAssetInspector.Inspect, WorkerPool";
    public override string NotReproduced => "ゲーム本体の XML 読み込み（パッチ適用・継承解決）とテクスチャの GPU 転送は含まない";

    public override IReadOnlyList<Condition> Conditions(RunEnvironment env)
    {
        var list = new List<Condition>();
        bool xml = env.Dataset.FilesWithExtension(".xml").Any();
        bool tex = env.Dataset.FilesWithExtension(".png", ".dds").Any();
        foreach (string kind in new[] { "xml", "texture" })
        {
            if ((kind == "xml" && !xml) || (kind == "texture" && !tex)) continue;
            foreach (int w in env.Options.Workers.Distinct().OrderBy(x => x))
            foreach (CacheState c in Caches(env))
                list.Add(Condition.Of(Id, ("kind", kind), ("workers", w), ("cache", CacheName(c))));
        }
        return list;
    }

    public override Trial CreateTrial(Condition condition, RunEnvironment env) => new ScanTrial(condition, env);

    private sealed class ScanTrial : Trial
    {
        private readonly Condition _c;
        private readonly List<string> _files;
        private WorkerPool? _pool;

        public ScanTrial(Condition c, RunEnvironment env)
        {
            _c = c;
            _files = (c.Get("kind") == "xml" ? env.Dataset.FilesWithExtension(".xml") : env.Dataset.FilesWithExtension(".png", ".dds")).ToList();
        }

        public override IReadOnlyList<string> CacheTargets => _files;

        public override void Prepare(TrialContext ctx) => _pool = Common.Pool(_c.Workers);

        public override void Execute(TrialContext ctx)
        {
            WorkerPool pool = _pool!;
            long busy = pool.BusyTicks;
            bool xml = _c.Get("kind") == "xml";
            int done = 0, failed = 0, skipped = 0, definitions = 0, valid = 0, malformed = 0, errors = 0;
            long rgba = 0, bytes = 0;
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ctx.Token);
            // Set by the last job, so the end of a short pass is not rounded up to a polling interval.
            var allDone = new ManualResetEventSlim(_files.Count == 0);
            foreach (string f in _files)
            {
                string path = f;
                pool.Enqueue(token =>
                {
                    try
                    {
                        if (cancel.IsCancellationRequested) return;
                        Interlocked.Add(ref bytes, new System.IO.FileInfo(path).Length);
                        if (xml)
                        {
                            XmlManifestReport r = XmlManifestScanner.Scan(path, cancel.Token);
                            if (r.ParseFailed) Interlocked.Increment(ref failed);
                            else if (r.Skipped) Interlocked.Increment(ref skipped);
                            else Interlocked.Add(ref definitions, r.DefinitionElements);
                        }
                        else
                        {
                            TextureAssetReport r = TextureAssetInspector.Inspect(path, cancel.Token);
                            if (r.State == TextureAssetState.Valid)
                            {
                                Interlocked.Increment(ref valid);
                                Interlocked.Add(ref rgba, r.Rgba8EquivalentBytes);
                            }
                            else if (r.State == TextureAssetState.Malformed) Interlocked.Increment(ref malformed);
                            else Interlocked.Increment(ref skipped);
                        }
                    }
                    catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                    {
                        Interlocked.Increment(ref errors);
                    }
                    finally
                    {
                        if (Interlocked.Increment(ref done) == _files.Count) allDone.Set();
                    }
                });
            }
            TimeSpan left = ctx.Timeout - ctx.Elapsed;
            bool finished = allDone.Wait(left > TimeSpan.Zero ? left : TimeSpan.Zero, ctx.Token);
            if (!finished)
            {
                cancel.Cancel();
                ctx.WaitUntil(() => pool.RunningJobs == 0, 5, limit: TimeSpan.FromSeconds(5));
                ctx.Truncated = true;
            }
            ctx.Metric("items", Volatile.Read(ref done));
            ctx.Metric("bytes_mib", Interlocked.Read(ref bytes) / 1048576.0);
            ctx.Metric("errors", errors);
            ctx.Metric("skipped", skipped);
            if (xml)
            {
                ctx.Metric("xml_parse_failures", failed);
                ctx.Metric("xml_definitions", definitions);
                ctx.Metric("result_hash", Common.ResultHash(_files.Count, failed, skipped, definitions, errors));
            }
            else
            {
                ctx.Metric("textures_valid", valid);
                ctx.Metric("textures_malformed", malformed);
                ctx.Metric("texture_rgba8_mib", rgba / 1048576.0);
                ctx.Metric("result_hash", Common.ResultHash(_files.Count, valid, malformed, skipped, rgba, errors));
            }
            ctx.Metric("workers", _c.Workers);
            Common.WorkerBusy(ctx, pool, busy, _c.Workers);
            Common.NoteOversubscription(ctx, _c.Workers);
        }

        public override void Dispose() => _pool?.Dispose();
    }
}
