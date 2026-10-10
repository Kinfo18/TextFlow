using Microsoft.Extensions.Time.Testing;
using TextFlow.Core.Library;

namespace TextFlow.Core.Tests.Library;

public sealed class UsageTrackerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Record_CountsPerSnippet_WithTheLastTime()
    {
        var tracker = new UsageTracker(_time);
        tracker.Record("cc");
        _time.Advance(TimeSpan.FromMinutes(5));
        tracker.Record("cc");
        tracker.Record("nc");

        var pending = tracker.TakePending();

        Assert.Equal(new SnippetUsage(2, _time.GetUtcNow()), pending["cc"]);
        Assert.Equal(1, pending["nc"].Uses);
    }

    [Fact]
    public void TakePending_EmptiesTheBuffer()
    {
        var tracker = new UsageTracker(_time);
        tracker.Record("cc");

        tracker.TakePending();

        Assert.Empty(tracker.TakePending());
    }

    [Fact]
    public void Restore_PutsAFailedFlushBack_AddingToNewUses()
    {
        var tracker = new UsageTracker(_time);
        tracker.Record("cc");
        var failed = tracker.TakePending();
        tracker.Record("cc");

        tracker.Restore(failed);

        Assert.Equal(2, tracker.TakePending()["cc"].Uses);
    }

    [Fact]
    public void Merge_AddsPendingUsesToTheStoredOnes()
    {
        var tracker = new UsageTracker(_time);
        tracker.Record("cc");
        var stored = new Dictionary<string, SnippetUsage> { ["cc"] = new(4, _time.GetUtcNow().AddDays(-1)), ["nc"] = new(1, _time.GetUtcNow().AddDays(-2)) };

        var merged = tracker.Merge(stored);

        Assert.Equal(new SnippetUsage(5, _time.GetUtcNow()), merged["cc"]);
        Assert.Equal(1, merged["nc"].Uses);
        Assert.Equal(1, tracker.TakePending()["cc"].Uses); // merging does not consume
    }
}
