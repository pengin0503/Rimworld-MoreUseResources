using MoreUseResources.Core;
using Xunit;

namespace MoreUseResources.Tests
{
    public sealed class SaveTimesTests
    {
        private const long MiB = 1024 * 1024;

        [Fact]
        public void Reports_median_range_gc_allocation_and_each_save()
        {
            var saves = new SaveTimes();
            Assert.Equal("none yet", saves.Report());

            saves.Add(new SaveTime(2400, 30, 300 * MiB));
            saves.Add(new SaveTime(1200, 10, 100 * MiB));
            saves.Add(new SaveTime(1300, 20, 200 * MiB));

            Assert.Equal(3, saves.Count);
            Assert.Equal("3 saves: median 1300 ms, min 1200 ms, max 2400 ms; GC stopped 20 ms and 200 MiB allocated per save on average; "
                + "each (oldest first): 2400, 1200, 1300 ms", saves.Report());
        }

        [Fact]
        public void Median_of_an_even_count_is_the_mean_of_the_middle_two()
        {
            var saves = new SaveTimes();
            foreach (double ms in new[] { 400.0, 100, 300, 200 }) saves.Add(new SaveTime(ms, 0, 0));
            Assert.StartsWith("4 saves: median 250 ms, min 100 ms, max 400 ms;", saves.Report());
        }

        [Fact]
        public void Keeps_the_last_saves_and_lists_the_most_recent_ones()
        {
            var saves = new SaveTimes();
            for (int i = 1; i <= SaveTimes.Kept + 6; i++) saves.Add(new SaveTime(i * 2, 0, 0));

            string report = saves.Report();
            Assert.Equal(SaveTimes.Kept + 6, saves.Count);
            // Saves 7 to 70 (14..140 ms) are kept: the median is that of those, and the list holds the last 20 (saves 51..70).
            Assert.StartsWith("70 saves (figures over the last 64): median 77 ms, min 14 ms, max 140 ms;", report);
            Assert.EndsWith("the last 20 (oldest first): 102, 104, 106, 108, 110, 112, 114, 116, 118, 120, "
                + "122, 124, 126, 128, 130, 132, 134, 136, 138, 140 ms", report);
        }

        [Fact]
        public void Ignores_impossible_times_and_clamps_negative_counters()
        {
            var saves = new SaveTimes();
            saves.Add(new SaveTime(double.NaN, 0, 0));
            saves.Add(new SaveTime(-1, 0, 0));
            Assert.Equal(0, saves.Count);

            // A counter that went backwards (or a pause that is not a number) is not a negative cost.
            saves.Add(new SaveTime(500, double.NaN, -5));
            saves.Add(new SaveTime(700, -3, 2 * MiB));
            Assert.Equal("2 saves: median 600 ms, min 500 ms, max 700 ms; GC stopped 0 ms and 1 MiB allocated per save on average; "
                + "each (oldest first): 500, 700 ms", saves.Report());
        }
    }
}
