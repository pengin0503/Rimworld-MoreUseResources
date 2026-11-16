using System.Linq;
using MoreUseResources.Core;
using Xunit;

namespace MoreUseResources.Tests
{
    public sealed class LoadStageRecorderTests
    {
        [Fact]
        public void Accumulates_repeated_stages_in_first_seen_order()
        {
            var recorder = new LoadStageRecorder();
            recorder.Add("ApplyPatches", 1500);
            recorder.Add("Resolve", 200);
            recorder.Add("Resolve", 300);

            var stages = recorder.Stages;
            Assert.Equal(new[] { "ApplyPatches", "Resolve" }, stages.Select(s => s.Name));
            Assert.Equal(500, stages[1].Milliseconds);
            Assert.Equal(2, stages[1].Calls);
        }

        [Fact]
        public void Report_is_culture_invariant_and_complete()
        {
            var recorder = new LoadStageRecorder();
            recorder.SetStartup(41234);
            recorder.SetStartup(1);
            recorder.Add("ApplyPatches", 1500);
            recorder.Add("Resolve", 200);
            recorder.Add("Resolve", 300);
            recorder.Complete(95000, 3L << 30);

            Assert.Equal("process start to mod 41.2 s; mod to loaded 95.0 s; ApplyPatches 1.5 s; Resolve 0.5 s (2 calls); peak working set 3072 MiB",
                recorder.Report());
        }

        [Fact]
        public void Ignores_invalid_input_and_anything_after_completion()
        {
            var recorder = new LoadStageRecorder();
            Assert.Equal("no data", recorder.Report());
            recorder.Add("", 5);
            recorder.Add("Bad", -1);
            recorder.Add("Bad", double.NaN);
            recorder.SetStartup(-5);
            Assert.Empty(recorder.Stages);
            Assert.False(recorder.Completed);

            recorder.Complete(10, 0);
            recorder.Complete(99999, 5);
            recorder.Add("Late", 5);
            Assert.True(recorder.Completed);
            Assert.Empty(recorder.Stages);
            Assert.Equal("mod to loaded 0.0 s", recorder.Report());
        }

        [Fact]
        public void Caps_the_number_of_distinct_stages()
        {
            var recorder = new LoadStageRecorder();
            for (int i = 0; i < LoadStageRecorder.MaxStages + 10; i++) recorder.Add("s" + i, 1);
            recorder.Add("s0", 1);
            Assert.Equal(LoadStageRecorder.MaxStages, recorder.Stages.Count);
            Assert.Equal(2, recorder.Stages[0].Calls);
        }
    }
}
