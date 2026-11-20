using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using MoreUseResources.Core;
using Xunit;

namespace MoreUseResources.Tests;

public sealed class CompatibilityPolicyTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "mur-compat-" + Guid.NewGuid().ToString("N"));

    public CompatibilityPolicyTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    private static readonly ExternalParallelism AllBusy = new(
        ExternalFeatureState.Active, ExternalFeatureState.Active, ExternalFeatureState.Active,
        new ExternalWorkerSetting("parWorkers", 12, 12), new ExternalWorkerSetting("parallelThreads", 15, null),
        SharedPoolBusy: true, SharedPoolWorkers: 15);

    [Theory]
    [InlineData(ExternalFeatureState.Absent, false)]
    [InlineData(ExternalFeatureState.Inactive, false)]
    [InlineData(ExternalFeatureState.Active, true)]
    [InlineData(ExternalFeatureState.Unknown, true)]
    public void XmlReuseYieldsOnlyForActiveOrUnknown(ExternalFeatureState state, bool expected)
    {
        Assert.Equal(expected, CompatibilityPolicy.YieldXmlToWowgag(state));
        WorkUnitYields yields = CompatibilityPolicy.ResolveYields(null, state);
        Assert.Equal(expected, yields.IsYielded(VanillaWorkUnit.XmlLoadStages));
        Assert.Equal(expected, yields.IsYielded(VanillaWorkUnit.PatchedXmlCache));
        // XML ownership never spreads to unrelated work.
        Assert.False(yields.IsYielded(VanillaWorkUnit.FilePrefetch));
        Assert.False(yields.IsYielded(VanillaWorkUnit.MeshUpload));
        Assert.False(yields.IsYielded(VanillaWorkUnit.JitPrewarm));
    }

    [Theory]
    [InlineData(GamePhase.PlayingNormal)]
    [InlineData(GamePhase.PlayingFast)]
    [InlineData(GamePhase.Paused)]
    [InlineData(GamePhase.MainMenu)]
    public void ExternalParallelWorkNeverLowersTheWorkerLimit(GamePhase phase)
    {
        foreach (int requested in new[] { 0, 1, 2, 4, 14, 31 })
        {
            Assert.Equal(requested, CompatibilityPolicy.LimitWorkers(requested, phase, AllBusy));
            Assert.Equal(requested, CompatibilityPolicy.LimitWorkers(requested, phase, ExternalParallelism.None));
            var unknown = new ExternalParallelism(ExternalFeatureState.Unknown, ExternalFeatureState.Unknown,
                ExternalFeatureState.Unknown, SharedPoolBusy: true);
            Assert.Equal(requested, CompatibilityPolicy.LimitWorkers(requested, phase, unknown));
        }
    }

    [Fact]
    public void OnlyTheDuplicatedUnitIsLeftToItsOwner()
    {
        var patches = new[]
        {
            // A timer on the section draw observes; a transpiler on the section update replaces.
            new ForeignPatch(VanillaWorkUnit.MeshUpload, "wowgag.probe", Replaces: false),
            new ForeignPatch(VanillaWorkUnit.SectionRegeneration, "wowgag.mapsections", Replaces: true),
            new ForeignPatch(VanillaWorkUnit.SectionRegeneration, "other.mod", Replaces: true),
            new ForeignPatch(VanillaWorkUnit.GraphicPrepare, "", Replaces: true),
        };
        WorkUnitYields yields = CompatibilityPolicy.ResolveYields(patches, ExternalFeatureState.Absent);
        Assert.Equal(1, yields.Count);
        Assert.Equal("wowgag.mapsections", yields.OwnerOf(VanillaWorkUnit.SectionRegeneration));
        Assert.False(yields.IsYielded(VanillaWorkUnit.MeshUpload));
        Assert.False(yields.IsYielded(VanillaWorkUnit.GraphicPrepare));
        foreach (VanillaWorkUnit unit in Enum.GetValues<VanillaWorkUnit>())
            if (unit != VanillaWorkUnit.SectionRegeneration) Assert.False(yields.IsYielded(unit));

        Assert.True(yields.SameAs(CompatibilityPolicy.ResolveYields(patches, ExternalFeatureState.Inactive)));
        Assert.False(yields.SameAs(WorkUnitYields.None));
        Assert.True(CompatibilityPolicy.ResolveYields(null, ExternalFeatureState.Absent, wowgagCompilesJit: true)
            .IsYielded(VanillaWorkUnit.JitPrewarm));
    }

    [Theory]
    // installed, anyEnabled, blocked, attached -> state
    [InlineData(false, null, null, null, ExternalFeatureState.Absent)]
    [InlineData(true, null, null, null, ExternalFeatureState.Unknown)]      // settings not readable
    [InlineData(true, false, null, null, ExternalFeatureState.Inactive)]    // every switch off
    [InlineData(true, true, true, true, ExternalFeatureState.Inactive)]     // on, but stopped by its own guard
    [InlineData(true, true, false, false, ExternalFeatureState.Inactive)]   // on, never attached
    [InlineData(true, true, false, true, ExternalFeatureState.Active)]
    [InlineData(true, true, null, true, ExternalFeatureState.Unknown)]      // guard not decided yet
    [InlineData(true, true, false, null, ExternalFeatureState.Unknown)]
    public void ParallelStateSeparatesSettingFromWhatRuns(bool installed, bool? enabled, bool? blocked, bool? attached,
        ExternalFeatureState expected)
    {
        Assert.Equal(expected, CompatibilityPolicy.ParallelState(installed, enabled, blocked, attached));
    }

    [Fact]
    public void WorkerSettingKeepsConfiguredAndRunningApart()
    {
        var automatic = new ExternalWorkerSetting("parWorkers", 0, 7);
        Assert.True(automatic.Automatic);
        Assert.Equal(7, automatic.Running);
        var unread = new ExternalWorkerSetting("parallelThreads", null, null);
        Assert.False(unread.Automatic);
        Assert.Null(unread.Running);
    }

    [Fact]
    public void RotationGivesAYieldedUnitsTurnsToIndependentWork()
    {
        var rotation = new PreparationRotation(
            VanillaWorkUnit.MeshUpload, VanillaWorkUnit.GraphicPrepare, VanillaWorkUnit.SectionRegeneration);
        WorkUnitYields yields = CompatibilityPolicy.ResolveYields(
            new[] { new ForeignPatch(VanillaWorkUnit.SectionRegeneration, "wowgag", true) });
        var ran = new Dictionary<VanillaWorkUnit, int>();
        for (int i = 0; i < 12; i++)
        {
            VanillaWorkUnit? unit = rotation.Next(_ => true, yields);
            Assert.NotNull(unit);
            ran[unit!.Value] = ran.GetValueOrDefault(unit.Value) + 1;
        }
        // All twelve turns are used: six each for the two units nobody else performs.
        Assert.False(ran.ContainsKey(VanillaWorkUnit.SectionRegeneration));
        Assert.Equal(6, ran[VanillaWorkUnit.MeshUpload]);
        Assert.Equal(6, ran[VanillaWorkUnit.GraphicPrepare]);

        // Without the foreign patch the same rotation runs all three.
        var all = new HashSet<VanillaWorkUnit>();
        for (int i = 0; i < 3; i++) all.Add(rotation.Next(_ => true, WorkUnitYields.None)!.Value);
        Assert.Equal(3, all.Count);
        // A finished unit also passes its turn on, and nothing runs when nothing has work.
        Assert.Equal(VanillaWorkUnit.GraphicPrepare,
            rotation.Next(unit => unit == VanillaWorkUnit.GraphicPrepare, WorkUnitYields.None));
        Assert.Null(rotation.Next(_ => false, WorkUnitYields.None));
    }

    private static RawGameSignals Playing(bool paused = false, bool fast = false) => new()
    {
        PlayDataLoaded = true, Stage = ProgramStage.Playing, Focused = true, Paused = paused, FastSpeed = fast,
    };

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ControllerKeepsItsWorkersWhileOtherModsRunInParallel(bool paused, bool fast)
    {
        string file = Path.Combine(dir, "asset.bin");
        File.WriteAllBytes(file, new byte[4096]);
        var settings = new ResourceSettings { CpuMaxWorkers = 14 };
        settings.ApplyPresetIntensity(IntensityPreset.Aggressive);

        int Workers(ExternalParallelism? external)
        {
            using var controller = new ResourceController(new FakeProbe(), 16, null, settings.Clone());
            controller.SetFileSources(new[] { file });
            if (external != null) controller.SetExternalParallelism(external);
            controller.PublishSignals(Playing(paused, fast));
            controller.ControlTick(true, 0.25);
            ControllerStatus status = controller.GetStatus();
            Assert.Equal(14, status.WorkerCap);
            if (external != null) Assert.Same(external, status.External);
            return status.WorkersActive;
        }

        int alone = Workers(null);
        Assert.True(alone > 4, "the scenario must ask for more than the four workers of the dropped plan");
        Assert.Equal(alone, Workers(AllBusy));
        Assert.Equal(alone, Workers(new ExternalParallelism(ExternalFeatureState.Unknown,
            ExternalFeatureState.Unknown, ExternalFeatureState.Active, SharedPoolBusy: true, SharedPoolWorkers: 15)));
    }

    [Fact]
    public void OwnLimitsStillApplyWhileOtherModsRunInParallel()
    {
        string file = Path.Combine(dir, "asset.bin");
        File.WriteAllBytes(file, new byte[4096]);

        // The user's own worker limit.
        var userLimit = new ResourceSettings { CpuMaxWorkers = 3 };
        userLimit.ApplyPresetIntensity(IntensityPreset.Aggressive);
        using (var controller = new ResourceController(new FakeProbe(), 16, null, userLimit))
        {
            controller.SetFileSources(new[] { file });
            controller.SetExternalParallelism(AllBusy);
            controller.PublishSignals(Playing(paused: true));
            controller.ControlTick(true, 0.25);
            Assert.Equal(3, controller.GetStatus().WorkersActive);

            // The frame guard.
            for (int i = 0; i < 300; i++) controller.ReportFrame(200);
            controller.ControlTick(false, 0);
            Assert.True(controller.FrameThrottled);
            Assert.Equal(1, controller.GetStatus().WorkersActive);
        }

        // Saving and the manual stop.
        bool saving = false;
        using (var controller = new ResourceController(new FakeProbe(), 16, () => saving, new ResourceSettings { CpuMaxWorkers = 14 }))
        {
            controller.SetFileSources(new[] { file });
            controller.SetExternalParallelism(AllBusy);
            controller.PublishSignals(Playing(paused: true));
            controller.ControlTick(true, 0.25);
            Assert.True(controller.GetStatus().WorkersActive > 4);
            saving = true;
            controller.ControlTick(false, 0);
            Assert.Equal(0, controller.GetStatus().WorkersActive);
            saving = false;
            controller.Manual = ManualMode.Paused;
            controller.ControlTick(false, 0);
            Assert.Equal(0, controller.GetStatus().WorkersActive);
        }
    }

    [Fact]
    public void YieldingJitLeavesFilePrefetchAndWorkersRunning()
    {
        string file = Path.Combine(dir, "asset.bin");
        File.WriteAllBytes(file, new byte[200_000]);
        var settings = new ResourceSettings { CpuMaxWorkers = 6, CpuJitPrewarm = true, RamHoldCopies = false, CpuIndexAssets = false };

        (JitProgress Jit, ControllerStatus Status) Run(WorkUnitYields yields)
        {
            using var controller = new ResourceController(new FakeProbe(), 8, null, settings.Clone());
            controller.SetJitSources(new[] { typeof(CompatibilityPolicyTests).Assembly }, Array.Empty<MethodBase>());
            controller.SetFileSources(new[] { file });
            controller.SetExternalParallelism(AllBusy);
            controller.SetWorkUnitYields(yields);
            controller.PublishSignals(new RawGameSignals { PlayDataLoaded = true, Stage = ProgramStage.Entry, Focused = true });
            for (int i = 0; i < 200; i++)
            {
                controller.ControlTick(true, 0.25);
                ControllerStatus now = controller.GetStatus();
                bool jitSettled = yields.IsYielded(VanillaWorkUnit.JitPrewarm) || controller.Jit.Prepared > 0;
                if (now.FilesRead >= 1 && jitSettled) break;
                Thread.Sleep(10);
            }
            return (controller.Jit, controller.GetStatus());
        }

        var normal = Run(WorkUnitYields.None);
        Assert.Equal(GamePhase.MainMenu, normal.Status.Plan.Phase.Phase);
        Assert.True(normal.Jit.Prepared > 0, "JIT preparation must run when nobody else owns it");
        Assert.True(normal.Status.FilesRead >= 1);

        var yielded = Run(CompatibilityPolicy.ResolveYields(null, ExternalFeatureState.Absent, wowgagCompilesJit: true));
        Assert.Equal(0, yielded.Jit.Prepared);                       // only the duplicated unit stops
        Assert.True(yielded.Status.FilesRead >= 1);                  // independent vanilla work still runs
        Assert.Equal(normal.Status.WorkersActive, yielded.Status.WorkersActive);
        Assert.Equal(CompatibilityPolicy.WowgagOwner, yielded.Status.Yields.OwnerOf(VanillaWorkUnit.JitPrewarm));
    }

    [Fact]
    public void ShaderCacheRecordsAreDiscoveredByExactEngineNameOnly()
    {
        string cache = Path.Combine(dir, "cache");
        string shaders = Path.Combine(cache, EnginePackFiles.ShaderCacheDirectory);
        Directory.CreateDirectory(shaders);
        string good = Path.Combine(shaders, new string('a', 64) + ".spv-cache");
        string second = Path.Combine(shaders, new string('0', 63) + "f.spv-cache");
        File.WriteAllBytes(good, new byte[16]);
        File.WriteAllBytes(second, new byte[16]);
        File.WriteAllBytes(Path.Combine(shaders, new string('A', 64) + ".spv-cache"), new byte[16]);   // not lowercase hex
        File.WriteAllBytes(Path.Combine(shaders, "short.spv-cache"), new byte[16]);
        File.WriteAllBytes(Path.Combine(shaders, new string('b', 64) + ".spv-cache.tmp"), new byte[16]);
        Directory.CreateDirectory(Path.Combine(cache, "ShaderCache-editor"));
        File.WriteAllBytes(Path.Combine(cache, "ShaderCache-editor", new string('c', 64) + ".spv-cache"), new byte[16]);

        List<string> found = EnginePackFiles.DiscoverShaderCache(cache, CancellationToken.None);
        Assert.Equal(new[] { second, good }, found);
        Assert.Empty(EnginePackFiles.DiscoverShaderCache(null, CancellationToken.None));
        Assert.Empty(EnginePackFiles.DiscoverShaderCache(Path.Combine(dir, "missing"), CancellationToken.None));
        Assert.Empty(EnginePackFiles.DiscoverShaderCache(cache, new CancellationToken(canceled: true)));
    }

    [Fact]
    public void ShaderRecordsArePrefetchedWithoutCopiesAuditOrPackResidency()
    {
        string shader = Path.Combine(dir, new string('d', 64) + ".spv-cache");
        string pack = Path.Combine(dir, "Core-0123456789abcdef.zip");
        string loose = Path.Combine(dir, "thing.png");
        File.WriteAllBytes(shader, new byte[3000]);
        File.WriteAllBytes(pack, new byte[5000]);
        File.WriteAllBytes(loose, new byte[1000]);
        var settings = new ResourceSettings { CpuMaxWorkers = 2, RamHoldCopies = true, CpuIndexAssets = false };
        using var controller = new ResourceController(new FakeProbe(), 4, null, settings);
        controller.SetFileSources(new[] { loose }, new[] { pack, shader });
        controller.PublishSignals(Playing(paused: true));
        ControllerStatus status = controller.GetStatus();
        for (int i = 0; i < 300 && status.FilesRead < 3; i++)
        {
            controller.ControlTick(true, 0.25);
            Thread.Sleep(10);
            status = controller.GetStatus();
        }
        Assert.Equal(3, status.FilesRead);
        Assert.Equal(1, status.ShaderCacheFiles);
        Assert.Equal(1, status.EnginePackFiles);
        Assert.Equal(9000, status.FileBytesRead);
        Assert.Equal(1000, status.CacheHeldBytes);     // only the loose asset is copied
        Assert.Equal(0, status.ResidentPackSources);
    }

    [Fact]
    public void StartupManifestListsShaderRecordsSeparatelyFromPacks()
    {
        string game = Path.Combine(dir, "game");
        string cache = Path.Combine(dir, "TextureCache", "v47", "bc7");
        string shaders = Path.Combine(dir, "ShaderCache");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(shaders);
        string config = Path.Combine(dir, "ModsConfig.xml");
        File.WriteAllText(config, "<ModsConfigData />");
        string pack = Path.Combine(cache, "Core-0123456789abcdef.zip");
        File.WriteAllBytes(pack, new byte[64]);
        string record = Path.Combine(shaders, new string('e', 64) + ".spv-cache");
        File.WriteAllBytes(record, new byte[32]);
        string stray = Path.Combine(dir, new string('f', 64) + ".spv-cache");   // another directory
        File.WriteAllBytes(stray, new byte[32]);
        string manifestPath = Path.Combine(dir, StartupPackManifest.FileName);

        Assert.Equal(StartupPackWriteResult.Written, StartupPackManifest.Save(manifestPath, game, cache, config,
            new[] { pack }, CancellationToken.None, shaderCacheFiles: new[] { record, stray, Path.Combine(shaders, "x.spv-cache") }));
        var manifest = System.Text.Json.JsonSerializer.Deserialize<StartupPackManifest>(File.ReadAllText(manifestPath))!;
        Assert.Equal(1, manifest.Schema);
        Assert.Single(manifest.Packs);
        Assert.Equal(shaders, manifest.ShaderCacheDirectory);
        Assert.Equal(record, Assert.Single(manifest.ShaderCache!).Path);

        Assert.Equal(StartupPackWriteResult.Written, StartupPackManifest.Save(manifestPath, game, cache, config,
            new[] { pack }, CancellationToken.None));
        manifest = System.Text.Json.JsonSerializer.Deserialize<StartupPackManifest>(File.ReadAllText(manifestPath))!;
        Assert.Null(manifest.ShaderCache);
        Assert.Null(manifest.ShaderCacheDirectory);
    }
}
