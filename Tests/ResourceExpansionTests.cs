using System;
using System.IO;
using MoreUseResources.Core;
using Xunit;

namespace MoreUseResources.Tests;

public sealed class ResourceExpansionTests
{
    [Fact]
    public void ResourcePresetUsesRealAssetsAndKeepsSafetyMargins()
    {
        var settings = new ResourceSettings { CpuSyntheticLoad = true, RamReserve = true };
        settings.ApplyAssetBoost();
        Assert.True(settings.RamResidentPacks);
        Assert.True(settings.GpuDemandPriority);
        Assert.True(settings.AdaptiveWorkers);
        Assert.False(settings.CpuSyntheticLoad);
        Assert.False(settings.RamReserve);
        Assert.False(settings.RamHoldCopies);
        Assert.False(settings.CpuPhases[(int)GamePhase.Saving]);
        Assert.Equal(14, settings.EffectiveMaxWorkers(16));
        Assert.True(settings.RamMinFreeMiB >= 6144);
    }

    [Fact]
    public void PrioritiesKeepOnlyTheHighestRealDemandAndIgnoreSelfUploads()
    {
        var demand = new TextureDemand<string>(2);
        demand.Observe("all-mine", total: 150, own: 150);
        demand.Observe("one", total: 2, own: 1);
        demand.Observe("four", total: 10, own: 6);
        demand.Observe("nine", total: 14, own: 5);
        Assert.Equal(new[] { "nine", "four" }, demand.TakeRanked());
        Assert.Empty(demand.TakeRanked());
    }

    [Fact]
    public void AdaptiveWorkersReduceOnSustainedPressureAndRecoverGradually()
    {
        var control = new AdaptiveWorkerBudget();
        for (int i = 0; i < 3; i++) control.Observe(7, 500);
        Assert.Equal(12, control.Limit(12));
        control.Observe(16, 400);
        Assert.Equal(12, control.Limit(12));
        control.Observe(15, 400);
        Assert.Equal(6, control.Limit(12));
        for (int i = 0; i < 3; i++) control.Observe(7, 500);
        Assert.Equal(9, control.Limit(12));
        control.Reset();
        Assert.Equal(12, control.Limit(12));
    }

    [Fact]
    public void ExactPackSelectionUsesBothRootHashAndActiveVariant()
    {
        string temp = Path.Combine(Path.GetTempPath(), "mur-" + Guid.NewGuid().ToString("N"));
        try
        {
            string active = Path.Combine(temp, "TextureCache", "v47", "bc7-gpu");
            string root = Path.Combine(temp, "mods", "Same Name");
            string otherRoot = Path.Combine(temp, "workshop", "Same Name");
            Directory.CreateDirectory(active);
            string expected = Path.Combine(active, EnginePackFiles.CacheFileName(root));
            string foreign = Path.Combine(active, EnginePackFiles.CacheFileName(otherRoot));
            string atlas = Path.Combine(active, "pages.0011223344556677.zip");
            File.WriteAllText(expected, "");
            File.WriteAllText(foreign, "");
            File.WriteAllText(atlas, "");
            File.WriteAllText(Path.Combine(active, "pages.0123456789abcdef.zip"), "");
            Assert.Equal(new[] { atlas, expected }, EnginePackFiles.Discover(temp, new[] { root },
                Array.Empty<string>(), null, default, active, "0011223344556677"));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public void StartupManifestRecordsFilesAndSelectionAndDoesNotReplaceItOnCancellation()
    {
        string temp = Path.Combine(Path.GetTempPath(), "mur-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string path = Path.Combine(temp, "manifest.json");
            string config = Path.Combine(temp, "ModsConfig.xml");
            string pack = Path.Combine(temp, "Assets.zip");
            File.WriteAllText(config, "<ModsConfig/>");
            File.WriteAllText(pack, "actual pack data");
            StartupPackManifest.Save(path, temp, Path.Combine(temp, "bc7-gpu"), config,
                new[] { pack, Path.Combine(temp, "Missing.zip") }, default);
            string json = File.ReadAllText(path);
            var result = System.Text.Json.JsonSerializer.Deserialize<StartupPackManifest>(json)!;
            Assert.True(result.GpuEncode);
            var entry = Assert.Single(result.Packs);
            Assert.Equal(pack, entry.Path);
            Assert.True(entry.MountedAssetPack);
            Assert.Equal(64, result.ModsConfigSha256.Length);
            StartupPackManifest.Save(path, temp, temp, config, new[] { pack }, new System.Threading.CancellationToken(true));
            Assert.Equal(json, File.ReadAllText(path));
            File.WriteAllText(config, "<Changed/>");
            StartupPackManifest.Save(path, temp, temp, config, new[] { pack }, default,
                expectedConfigHash: result.ModsConfigSha256);
            Assert.Equal(json, File.ReadAllText(path));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, null, true)]
    public void JitYieldsToActiveOrUnrecognizedWowgag(bool loaded, bool? compiles, bool expected) =>
        Assert.Equal(expected, HardwareWork.YieldJitToWowgag(loaded, compiles));
}
