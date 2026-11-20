using System.Collections.Generic;
using MoreUseResources.Benchmarks.Measurement;
using MoreUseResources.Benchmarks.Running;
using Xunit;

namespace MoreUseResources.Benchmarks.Tests;

public class ProcFsTests
{
    [Fact]
    public void StatusConvertsKilobytesAndKeepsCounts()
    {
        var m = ProcFs.ParseStatus("Name:\tdotnet\nVmRSS:\t  2048 kB\nThreads:\t17\nvoluntary_ctxt_switches:\t42\nRssAnon:\t1 kB\n");
        Assert.Equal(2048L * 1024, m["VmRSS"]);
        Assert.Equal(1024, m["RssAnon"]);
        Assert.Equal(17, m["Threads"]);
        Assert.Equal(42, m["voluntary_ctxt_switches"]);
        Assert.False(m.ContainsKey("Name"));
    }

    [Fact]
    public void IoCountersSeparateStorageFromCharacters()
    {
        IoCounters io = ProcFs.ParseIo("rchar: 1000\nwchar: 20\nsyscr: 7\nsyscw: 2\nread_bytes: 4096\nwrite_bytes: 8192\ncancelled_write_bytes: 0\n")!.Value;
        Assert.Equal(1000, io.ReadChars);
        Assert.Equal(4096, io.StorageReadBytes);
        Assert.Equal(7, io.ReadSyscalls);
        Assert.Equal(8192, io.StorageWriteBytes);
    }

    [Fact]
    public void SystemCpuSumsAllFields()
    {
        SystemCpuTicks? t = ProcFs.ParseSystemCpu("cpu  100 5 50 1000 20 1 2 7 0 0\ncpu0 1 2 3 4 5 6 7 8 0 0\n");
        Assert.NotNull(t);
        Assert.Equal(1000, t!.Value.Idle);
        Assert.Equal(20, t.Value.IoWait);
        Assert.Equal(7, t.Value.Steal);
        Assert.Equal(100 + 5 + 50 + 1000 + 20 + 1 + 2 + 7, t.Value.Total);
    }

    [Fact]
    public void PressureReadsTotals()
    {
        PressureTotals? p = ProcFs.ParsePressure("some avg10=0.00 avg60=0.00 avg300=0.00 total=12345\nfull avg10=0.00 avg60=0.00 avg300=0.00 total=678\n");
        Assert.Equal(12345, p!.Value.SomeMicros);
        Assert.Equal(678, p.Value.FullMicros);
        Assert.Equal(55, ProcFs.ParsePressure("some avg10=0.00 avg60=0.00 avg300=0.00 total=55\n")!.Value.SomeMicros);
    }

    [Fact]
    public void TaskStatSurvivesSpacesAndParenthesesInTheName()
    {
        // Fields after the name: state ppid ... utime(14) stime(15) ... priority(18) nice(19) ... delayacct_blkio_ticks(42)
        var fields = new List<string> { "S" };
        for (int n = 4; n <= 52; n++) fields.Add(n switch { 14 => "150", 15 => "30", 18 => "39", 19 => "19", 42 => "77", _ => "0" });
        string line = "1234 (MUR-Worker (x) 1) " + string.Join(" ", fields);
        TaskStat? t = ProcFs.ParseTaskStat(line);
        Assert.NotNull(t);
        Assert.Equal(150, t!.Value.UserTicks);
        Assert.Equal(30, t.Value.SystemTicks);
        Assert.Equal(19, t.Value.Nice);
        Assert.Equal(77, t.Value.BlockIoDelayTicks);
    }

    [Fact]
    public void SchedStatHasRunAndWaitNanoseconds()
    {
        SchedStat? s = ProcFs.ParseSchedStat("123456789 987654 42\n");
        Assert.Equal(123456789, s!.Value.RunNanoseconds);
        Assert.Equal(987654, s.Value.WaitNanoseconds);
    }
}

public class PeakSamplerParsingTests
{
    [Fact]
    public void FindsKilobyteFieldWithoutAllocating()
    {
        byte[] text = System.Text.Encoding.ASCII.GetBytes("VmPeak:\t 100 kB\nVmRSS:\t   2500 kB\nRssAnon:\t 1200 kB\nRssFile:\t 1300 kB\n");
        Assert.True(PeakSampler.TryFindKib(text, "VmRSS:"u8, out long rss));
        Assert.Equal(2500L * 1024, rss);
        Assert.True(PeakSampler.TryFindKib(text, "RssFile:"u8, out long file));
        Assert.Equal(1300L * 1024, file);
        Assert.False(PeakSampler.TryFindKib(text, "Missing:"u8, out _));
    }
}

public class OptionsTests
{
    [Fact]
    public void ModePresetAppliesBeforeExplicitOptionsWhateverTheOrder()
    {
        BenchOptions o = BenchOptions.Parse(new[] { "run", "--trials", "9", "--mode", "standard", "--max-rss-mib", "100000" });
        Assert.Equal(RunMode.Standard, o.Mode);
        Assert.Equal(9, o.Trials);
        Assert.Equal(new[] { 1, 2, 4, 8, 16 }, o.Workers);
        Assert.Equal(20, o.RateWindowSeconds);
    }

    [Fact]
    public void WorkersAreSortedAndDeduplicated()
    {
        BenchOptions o = BenchOptions.Parse(new[] { "run", "--workers", "8,1,4,4", "--max-rss-mib", "100000" });
        Assert.Equal(new[] { 1, 4, 8 }, o.Workers);
        Assert.Contains("workers", o.Explicit);
    }

    [Theory]
    [InlineData("--workers", "0")]
    [InlineData("--cache", "lukewarm")]
    [InlineData("--scenarios", "nope")]
    [InlineData("--cold-method", "sync")]
    [InlineData("--unknown", "1")]
    public void InvalidValuesAreRejected(string key, string value) =>
        Assert.Throws<System.ArgumentException>(() => BenchOptions.Parse(new[] { "run", key, value, "--max-rss-mib", "100000" }));

    [Fact]
    public void RamBudgetMustLeaveRoomBelowTheRssLimit() =>
        Assert.Throws<System.ArgumentException>(() => BenchOptions.Parse(new[] { "run", "--ram-budget-mib", "4096", "--max-rss-mib", "4000" }));

    [Fact]
    public void PositionalArgumentsAndFlags()
    {
        BenchOptions o = BenchOptions.Parse(new[] { "compare", "a", "b", "--no-gc-events", "--max-rss-mib", "100000" });
        Assert.Equal("compare", o.Command);
        Assert.Equal(new[] { "a", "b" }, o.Positional);
        Assert.False(o.GcPauseEvents);
    }
}

public class PathRedactorTests
{
    [Fact]
    public void ReplacesDataScratchAndOutputDirectories()
    {
        string data = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mur-redact-data");
        string output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mur-redact-out");
        PathRedactor.Configure(new[] { data }, output, System.IO.Path.Combine(output, "run", "scratch"));
        string text = "Could not find file '" + System.IO.Path.Combine(data, "Mods", "A.xml") + "' or '" + System.IO.Path.Combine(output, "run", "scratch", "f") + "'";
        string redacted = PathRedactor.Redact(text)!;
        Assert.DoesNotContain(data, redacted);
        Assert.Contains("<data0>", redacted);
        Assert.Contains("<scratch>", redacted);
    }
}
