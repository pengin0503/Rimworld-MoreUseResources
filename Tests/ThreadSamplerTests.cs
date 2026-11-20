using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using MoreUseResources.Core;
using Xunit;

namespace MoreUseResources.Tests
{
    public sealed class ThreadSamplerTests
    {
        [Theory]
        [InlineData("WaylithJob 12", "WaylithJob")]
        [InlineData("MUR-Worker-3", "MUR-Worker")]
        [InlineData(".NET TP Worker", ".NET TP Worker")]
        [InlineData("RenderThread", "RenderThread")]
        [InlineData(" Thread #7 ", "Thread")]
        [InlineData("123", "123")]
        [InlineData(null, "(unnamed)")]
        [InlineData("   ", "(unnamed)")]
        public void Groups_threads_by_name_without_their_number(string? name, string group) =>
            Assert.Equal(group, ThreadSampler.GroupOf(name));

        [Fact]
        public void Keeps_group_names_short() =>
            Assert.Equal(40, ThreadSampler.GroupOf(new string('a', 100)).Length);

        [Fact]
        public void Delta_counts_new_threads_whole_and_a_reused_id_as_a_new_thread()
        {
            var start = new ThreadSnapshot(new[]
            {
                new ThreadTime(1, 10, 100, "main"),
                new ThreadTime(2, 10, 50, "WaylithJob"),
                new ThreadTime(3, 10, 10, "WaylithJob"),
                new ThreadTime(9, 10, 400, "gone"),
            }, 5);
            var end = new ThreadSnapshot(new[]
            {
                new ThreadTime(1, 10, 300, "main"),
                new ThreadTime(2, 10, 80, "WaylithJob"),
                new ThreadTime(3, 20, 5, "WaylithJob"),
                new ThreadTime(4, 20, 7, "RenderThread"),
            }, 9);

            Dictionary<string, GroupCpu> delta = ThreadSampler.Delta(start, end);

            Assert.Equal(new GroupCpu(200, 1), delta["main"]);
            Assert.Equal(new GroupCpu(35, 2), delta["WaylithJob"]);
            Assert.Equal(new GroupCpu(7, 1), delta["RenderThread"]);
            Assert.False(delta.ContainsKey("gone"));
            Assert.Equal(new GroupCpu(80 + 5, 2), ThreadSampler.Totals(end)["WaylithJob"]);
        }

        [Fact]
        public void Formats_the_busiest_groups_first_and_sums_the_rest()
        {
            var groups = new Dictionary<string, GroupCpu>
            {
                ["a"] = new GroupCpu(100, 1),
                ["b"] = new GroupCpu(3000, 4),
                ["c"] = new GroupCpu(500, 1),
                ["d"] = new GroupCpu(20, 2),
            };
            Assert.Equal("b (4) 3.00, c 0.50, 2 more groups 0.12", ThreadSampler.Format(groups, 1000, shown: 2));
            Assert.Equal("b (4) 3.00, c 0.50, a 0.10, d (2) 0.02", ThreadSampler.Format(groups, 1000));
            Assert.Equal("n/a", ThreadSampler.Format(groups, 0));
            Assert.Equal("n/a", ThreadSampler.Format(new Dictionary<string, GroupCpu>(), 1000));
        }

        [Fact]
        public void Parses_a_proc_stat_line_whose_name_holds_spaces_and_parentheses()
        {
            const string stat = "1234 (my (odd) name) S 1 1 1 0 -1 4194560 500 0 7 0 120 30 0 0 20 0 5 0 98765 4096 100 18446744073709551615";
            Assert.True(ThreadSampler.TryParseLinuxStat(stat, out long cpu, out long started, out long faults));
            Assert.Equal(150, cpu);
            Assert.Equal(98765, started);
            Assert.Equal(7, faults);
            Assert.False(ThreadSampler.TryParseLinuxStat("1234 (cut", out _, out _, out _));
            Assert.False(ThreadSampler.TryParseLinuxStat("1234 (short) S 1 2", out _, out _, out _));
        }

        [Fact]
        public void Parses_the_windows_process_list_layout()
        {
            // Two SYSTEM_PROCESS_INFORMATION entries as 64-bit Windows lays them out, each followed by its threads.
            static void Thread(byte[] data, int at, long kernel, long user, long created, long id)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(at), kernel);
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(at + 8), user);
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(at + 16), created);
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(at + 48), id);
            }
            static void Process(byte[] data, int at, uint next, uint threads, uint hardFaults, long id)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), next);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at + 4), threads);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at + 16), hardFaults);
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(at + 80), id);
            }
            var data = new byte[256 + 80 + 256 + 2 * 80];
            Process(data, 0, 336, 1, 999, 4);
            Thread(data, 256, 5, 5, 1, 8);
            Process(data, 336, 0, 2, 42, 1234);
            Thread(data, 336 + 256, 1_000_000, 2_000_000, 111, 77);
            Thread(data, 336 + 256 + 80, 0, 50_000, 222, 88);

            ThreadSnapshot? snapshot = ThreadSampler.ParseWindowsProcesses(data, 1234, 77, (id, started) => "G" + id + "@" + started);

            Assert.NotNull(snapshot);
            Assert.Equal(42, snapshot!.HardFaults);
            Assert.Equal(new[] { new ThreadTime(77, 111, 300, "main"), new ThreadTime(88, 222, 5, "G88@222") }, snapshot.Threads);
            Assert.Null(ThreadSampler.ParseWindowsProcesses(data, 5678, 77, (_, _) => "x"));
            Assert.Null(ThreadSampler.ParseWindowsProcesses(data.AsSpan(0, data.Length - 1), 1234, 77, (_, _) => "x"));
        }

        [Fact]
        public void Reads_a_busy_named_thread_of_this_process()
        {
            using var sampler = new ThreadSampler();
            ThreadSnapshot? before = sampler.Read();
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            {
                Assert.Null(before);
                return;
            }
            Assert.NotNull(before);
            using var spun = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 300) { }
                spun.Set();
                release.Wait();
            }) { Name = "MUR-Spin", IsBackground = true };
            thread.Start();
            try
            {
                Assert.True(spun.Wait(TimeSpan.FromSeconds(30)));
                ThreadSnapshot? after = sampler.Read();
                Assert.NotNull(after);
                Dictionary<string, GroupCpu> delta = ThreadSampler.Delta(before!, after!);
                Assert.True(delta.TryGetValue("MUR-Spin", out GroupCpu spin), string.Join(", ", delta.Keys));
                Assert.Equal(1, spin.Threads);
                // Linux counts thread time in 10 ms steps and a busy test host can preempt the loop.
                Assert.True(spin.CpuMs >= 50, spin.CpuMs.ToString());
                Assert.Equal(1, delta[ThreadSampler.MainGroup].Threads);
                Assert.NotNull(after!.HardFaults);
            }
            finally
            {
                release.Set();
                thread.Join();
            }
        }

        [Fact]
        public void A_read_on_another_thread_reports_the_named_thread_as_main()
        {
            using var sampler = new ThreadSampler();
            long caller = ThreadSampler.CurrentThreadId();
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            {
                Assert.Equal(-1, caller);
                return;
            }
            Assert.True(caller > 0);
            ThreadSnapshot? snapshot = System.Threading.Tasks.Task.Run(() => sampler.Read(caller)).GetAwaiter().GetResult();
            Assert.NotNull(snapshot);
            ThreadTime main = Assert.Single(snapshot!.Threads, t => t.Group == ThreadSampler.MainGroup);
            Assert.Equal(caller, main.Id);
        }

        [Fact]
        public void Reads_from_several_threads_take_turns()
        {
            using var sampler = new ThreadSampler();
            long caller = ThreadSampler.CurrentThreadId();
            var reads = new System.Threading.Tasks.Task<ThreadSnapshot?>[8];
            for (int i = 0; i < reads.Length; i++) reads[i] = System.Threading.Tasks.Task.Run(() => sampler.Read(caller));
            System.Threading.Tasks.Task.WaitAll(reads);
            foreach (var read in reads)
            {
                if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) Assert.NotNull(read.Result);
                else Assert.Null(read.Result);
            }
        }

        [Fact]
        public void Private_bytes_are_read_without_the_process_list() =>
            Assert.True(ProcessCounters.PrivateBytes() > 0);

        [Theory]
        [InlineData(1, 7, true)]
        [InlineData(1, 6, false)]
        [InlineData(1, 8, false)]
        [InlineData(2, 7, false)]
        public void Every_1_7_build_runs_the_engine_facing_code(int major, int minor, bool supported) =>
            Assert.Equal(supported, SupportedGame.Contains(major, minor));

        [Theory]
        [InlineData(0, 100, 64, 0)]
        [InlineData(33, 100_000, 65_536, 65_536)]
        [InlineData(70_000, 100_000, 65_536, 100_000)]
        [InlineData(10, 5, 64, 5)]
        [InlineData(100, 1000, 1, 100)]
        public void Estimates_what_a_buffered_stream_read(long position, long length, int buffer, long expected) =>
            Assert.Equal(expected, ModFileReads.Buffered(position, length, buffer));

        [Fact]
        public void The_xml_check_counts_its_reads_as_this_mods()
        {
            string path = Path.Combine(Path.GetTempPath(), "mur-reads-" + Guid.NewGuid().ToString("N") + ".xml");
            var xml = new StringBuilder("<Defs>");
            for (int i = 0; i < 4000; i++) xml.Append("<ThingDef><defName>Thing").Append(i).Append("</defName><label>x</label></ThingDef>");
            xml.Append("</Defs>");
            File.WriteAllText(path, xml.ToString());
            try
            {
                long length = new FileInfo(path).Length;
                long before = ModFileReads.Bytes;
                XmlManifestReport report = XmlManifestScanner.Scan(path, CancellationToken.None);
                Assert.Equal(4000, report.DefinitionElements);
                // Other tests read at the same time, so only a lower bound holds.
                Assert.True(ModFileReads.Bytes - before >= length);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
