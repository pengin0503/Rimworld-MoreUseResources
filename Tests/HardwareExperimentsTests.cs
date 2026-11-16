using System;
using System.IO;
using System.Reflection;
using System.Runtime;
using System.Xml;
using MoreUseResources.Core;
using Xunit;

namespace MoreUseResources.Tests;

public sealed class HardwareExperimentsTests
{
    [Fact]
    public void GcBaselineSurvivesRestartAndNormalizesUnequalWindows()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var baseline = GcBenchmark.From(new GcSnapshot(10, 12, 4, 2, 40, 9, 1000, false, true,
                GCLatencyMode.Interactive), GamePhase.Paused);
            var current = GcBenchmark.From(new GcSnapshot(20, 12, 4, 2, 40, 9, 2000, true, true,
                GCLatencyMode.Interactive), GamePhase.Paused);
            GcBenchmark.Save(path, baseline);
            Assert.Equal(baseline, GcBenchmark.Load(path));
            Assert.True(current.TryCompare(baseline, out var delta));
            Assert.Equal(-54, delta.CollectionsPerMinute);
            Assert.Equal(-120, delta.PauseMsPerMinute);
            Assert.Equal(1000, delta.HeapBytes);
            Assert.False((current with { Phase = GamePhase.MainMenu }).TryCompare(baseline, out _));
            File.WriteAllText(path, "{broken");
            Assert.Null(GcBenchmark.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void JitOnlyPreparesOrdinaryClosedManagedMethods()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        Assert.True(JitPrewarm.IsEligible(typeof(HardwareExperimentsTests).GetMethod(nameof(Ordinary), flags)!));
        Assert.False(JitPrewarm.IsEligible(typeof(HardwareExperimentsTests).GetMethod(nameof(Open), flags)!));
        Assert.False(JitPrewarm.IsEligible(typeof(object).GetConstructor(Type.EmptyTypes)!));
        Assert.False(JitPrewarm.IsEligible(typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose))!));
    }

    [Theory]
    [InlineData(GamePhase.MainMenu, true)]
    [InlineData(GamePhase.Paused, true)]
    [InlineData(GamePhase.PlayingNormal, false)]
    [InlineData(GamePhase.Saving, false)]
    [InlineData(GamePhase.Loading, false)]
    public void ExtraPreparationOnlyRunsInMenuOrPause(GamePhase phase, bool expected)
    {
        Assert.Equal(expected, HardwareWork.MayPrepare(phase, active: true, throttled: false, memoryOk: true));
        Assert.False(HardwareWork.MayPrepare(phase, active: true, throttled: true, memoryOk: true));
        Assert.False(HardwareWork.MayPrepare(phase, active: true, throttled: false, memoryOk: false));
        Assert.False(HardwareWork.MayPrepare(phase, active: false, throttled: false, memoryOk: true));
    }

    [Fact]
    public void FrameWorkStopsAtTheSharedBudget()
    {
        Assert.True(HardwareWork.HasFrameBudget(elapsedMs: 0.49, budgetTenthsMs: 10, intensity: 0.5f));
        Assert.False(HardwareWork.HasFrameBudget(elapsedMs: 0.50, budgetTenthsMs: 10, intensity: 0.5f));
        Assert.False(HardwareWork.HasFrameBudget(elapsedMs: 0, budgetTenthsMs: 10, intensity: 0));
    }

    [Theory]
    [InlineData(2, 1, true, true, true)]
    [InlineData(1, 1, true, true, false)]
    [InlineData(2, 2, true, true, true)]
    [InlineData(2, 0, true, true, false)]
    [InlineData(2, 3, true, true, false)]
    [InlineData(2, 1, false, true, false)]
    [InlineData(2, 1, true, false, false)]
    public void MeshQualityOnlyChangesDefaultFilteringOnRepeatingMipmappedTextures(
        int mipCount, int anisotropy, bool repeat, bool filtered, bool expected)
    {
        Assert.Equal(expected, HardwareWork.CanImproveMeshFiltering(mipCount, anisotropy, repeat, filtered));
    }

    [Fact]
    public void JitDoesNotScanOrPrepareWhilePausedByTheGovernor()
    {
        var jit = new JitPrewarm(new[] { typeof(HardwareExperimentsTests).Assembly }, Array.Empty<MethodBase>());
        jit.RunSlice(() => false, default);
        Assert.Equal(default, jit.Progress);
        jit.RunSlice(() => true, new System.Threading.CancellationToken(canceled: true));
        Assert.Equal(default, jit.Progress);
    }

    [Fact]
    public void OwnedSettingRestoresOriginalAndYieldsToAnotherWriter()
    {
        int value = 1;
        var setting = new OwnedSetting<int>(() => value, next => value = next);
        setting.Apply(4);
        Assert.Equal(4, value);
        setting.Restore();
        Assert.Equal(1, value);
        setting.Apply(4);
        value = 7;
        setting.Apply(4);
        setting.Restore();
        Assert.Equal(7, value);
    }

    [Fact]
    public void ResidencyBoostNeverExceedsTheDriverOrUserReserve()
    {
        var memory = new VramInfo(6_000, 10_000, true);
        Assert.Equal(75, HardwareWork.TextureTarget(memory, 60, 2000, boost: true, offloadUnderPressure: true));
        Assert.Equal(60, HardwareWork.TextureTarget(memory, 60, 2000, boost: true, offloadUnderPressure: false));
        Assert.Equal(60, HardwareWork.TextureTarget(new VramInfo(8_000, 10_000, true), 60, 2000, true, true));
        Assert.Equal(60, HardwareWork.TextureTarget(default, 60, 2000, true, true));
    }

    [Fact]
    public void CachedXmlPreservesOriginsAndRejectsCorruptionBeforeChangingTheDocument()
    {
        var before = new XmlDocument();
        before.LoadXml("<Defs><A/><B/></Defs>");
        var after = new XmlDocument();
        after.LoadXml("<Defs><B/><Injected/><A/></Defs>");
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml");
        try
        {
            PatchedXmlStore.Save(path, "key", after, new[] { 1, -1, 0 });
            Assert.Null(PatchedXmlStore.Load(path, "other", 2));
            Assert.Null(PatchedXmlStore.Load(path, "key", 1));
            var cached = PatchedXmlStore.Load(path, "key", 2)!;
            Assert.Equal(after.OuterXml, cached.Document.OuterXml);
            Assert.Equal(new[] { 1, -1, 0 }, cached.Origins);
            byte[] damaged = File.ReadAllBytes(path);
            damaged[damaged.Length / 2] ^= 1;
            File.WriteAllBytes(path, damaged);
            Assert.Null(PatchedXmlStore.Load(path, "key", 2));
            Assert.Equal("<Defs><A /><B /></Defs>", before.OuterXml);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CachedXmlPreservesSeparateTextNodesAndCarriageReturns()
    {
        var document = new XmlDocument();
        document.LoadXml("<Defs><A><label>first&#13;line</label></A></Defs>");
        XmlElement label = (XmlElement)document.SelectSingleNode("/Defs/A/label")!;
        label.AppendChild(document.CreateTextNode(" second"));
        label.AppendChild(document.CreateCDataSection("raw"));
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".bin");
        try
        {
            PatchedXmlStore.Save(path, "key", document, new[] { 0 });
            XmlNode cached = PatchedXmlStore.Load(path, "key", 1)!.Document.SelectSingleNode("/Defs/A/label")!;
            Assert.Equal(3, cached.ChildNodes.Count);
            Assert.Equal("first\rline", cached.ChildNodes[0]!.Value);
            Assert.Equal(" second", cached.ChildNodes[1]!.Value);
            Assert.Equal(XmlNodeType.CDATA, cached.ChildNodes[2]!.NodeType);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GcBaselineRequiresMatchingSelectedAndEffectivePlaySpeed()
    {
        var baseline = new GcBenchmark(GamePhase.PlayingFast, 60, 15, 1000, 3, false,
            GCLatencyMode.Interactive, 2000, SelectedSpeed: 2, EffectiveSpeed: 3);
        Assert.True(baseline.TryCompare(baseline, out _));
        Assert.False((baseline with { EffectiveSpeed = 1 }).TryCompare(baseline, out _));
        Assert.False((baseline with { SelectedSpeed = 3 }).TryCompare(baseline, out _));
        Assert.False((baseline with { SelectedSpeed = null, EffectiveSpeed = null }).TryCompare(baseline, out _));
    }

    [Fact]
    public void AmbiguousTextureHeapDoesNotPublishPartialBudget()
    {
        var selector = new TextureHeapSelection(1024);
        selector.Add(false, 1024, 900, 200, 300);
        selector.Add(true, 1024, 900, 200, 300);
        Assert.True(selector.TryGet(out var budget));
        Assert.Equal(300, budget.UsageBytes);
        selector.Add(true, 1024, 900, 400, 400);
        Assert.False(selector.TryGet(out budget));
        Assert.False(budget.DriverReported);
    }

    [Fact]
    public void ObservingDemandAtCapacityDoesNotConsumeUploadCandidates()
    {
        using var upload = new TextureScanCursor<string>(() => new[] { "first", "second" });
        using var demand = new TextureScanCursor<string>(() => new[] { "first", "second" });
        Assert.Null(upload.Take(false));
        Assert.Equal("first", demand.Take(true));
        Assert.Equal("second", demand.Take(true));
        Assert.Null(demand.Take(true));
        Assert.False(upload.Completed);
        Assert.Equal("first", upload.Take(true));
        Assert.Equal("second", upload.Take(true));
    }

    [Fact]
    public void ManifestRetryIsDelayedAndStopsAfterSuccess()
    {
        var schedule = new StartupManifestRetry();
        Assert.True(schedule.TryStart(0));
        Assert.False(schedule.TryStart(5000));
        schedule.Complete(retry: true);
        Assert.False(schedule.TryStart(4999));
        Assert.True(schedule.TryStart(5000));
        schedule.Complete(retry: false);
        Assert.False(schedule.TryStart(10000));
    }

    private static int Ordinary() => 1;
    private static T Open<T>(T value) => value;
}
