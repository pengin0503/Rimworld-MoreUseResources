using System;
using System.Collections.Generic;
using System.Linq;
using MoreUseResources.Benchmarks.Measurement;
using MoreUseResources.Benchmarks.Running;
using MoreUseResources.Core;

namespace MoreUseResources.Benchmarks.Scenarios;

/// <summary>
/// File discovery as the controller's scan job runs it: GameAssetFiles.Discover over the load
/// folders and EnginePackFiles.Discover for the packs. Both repeat on every rescan
/// (AssetDiscoveryRescanMinutes), so their cost is paid periodically during play.
/// Directory metadata can only be dropped from the kernel cache as root (drop_caches); without
/// root only the warm case is measured.
/// </summary>
public sealed class DiscoveryScenario : Scenario
{
    public override string Id => "discovery";
    public override string Title => "ファイル探索（定期再探索1回分のコスト）";
    public override string ProductionCode => "GameAssetFiles.Discover, EnginePackFiles.Discover";
    public override string NotReproduced => "NTFS のディレクトリ列挙コスト、Steam Workshop の実フォルダ構成（R2 取得分のみ）、ゲームのテクスチャキャッシュ（Windows のキャッシュ場所）";

    public override IReadOnlyList<Condition> Conditions(RunEnvironment env)
    {
        var list = new List<Condition>();
        var metas = new List<string> { "warm" };
        if (env.Options.ColdMethod == "drop-caches") metas.Add("cold");
        foreach (string target in new[] { "assets", "packs" })
        foreach (string meta in metas)
            list.Add(Condition.Of(Id, ("target", target), ("meta", meta)));
        return list;
    }

    public override Trial CreateTrial(Condition condition, RunEnvironment env) => new DiscoveryTrial(condition, env);

    private sealed class DiscoveryTrial : Trial
    {
        private readonly Condition _c;
        private readonly RunEnvironment _env;

        public DiscoveryTrial(Condition c, RunEnvironment env)
        {
            _c = c;
            _env = env;
        }

        private List<string> Run(System.Threading.CancellationToken token) => _c.Get("target") == "assets"
            ? GameAssetFiles.Discover(_env.Dataset.LoadFolders, token, () => true)
            : EnginePackFiles.Discover(null, _env.Dataset.Mods.Select(m => m.Path).ToList(), _env.Dataset.LoadFolders, _env.Dataset.Packs, token);

        public override void Prepare(TrialContext ctx)
        {
            if (_c.Get("meta") == "cold")
            {
                if (!PageCache.DropAll(includeMetadata: true, out string? error))
                    ctx.Invalidate("cold directory metadata needs root (drop_caches failed: " + error + ")");
            }
            else Run(ctx.Token); // warm the dentry and inode caches
        }

        public override void Execute(TrialContext ctx)
        {
            List<string> found = Run(ctx.Token);
            ctx.Metric("items", found.Count);
            ctx.Metric("load_folders", _env.Dataset.LoadFolders.Count);
            ctx.Metric("result_hash", Common.ResultHash(found.Count, found.Sum(f => (long)f.Length)));
            if (_c.Get("target") == "assets" && found.Count != _env.Dataset.DiscoveredFiles.Count)
                ctx.Note("discovery returned " + found.Count + " files, the dataset load found " + _env.Dataset.DiscoveredFiles.Count);
        }
    }
}
