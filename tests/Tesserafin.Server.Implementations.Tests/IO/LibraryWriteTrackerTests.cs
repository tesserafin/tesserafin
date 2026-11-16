using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Tesserafin.Server.Core.IO;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.IO;

public sealed class LibraryWriteTrackerTests
{
    [Fact]
    public async Task OverlappingWrites_StaySuppressedUntilBothQuietPeriodsEnd()
    {
        var timers = new List<Action>();
        var tracker = Tracker(timers);
        tracker.Begin("/media/item.nfo");
        var first = tracker.CompleteAsync("/media/item.nfo");
        tracker.Begin("/MEDIA/item.nfo");
        var second = tracker.CompleteAsync("/MEDIA/item.nfo");

        Assert.Single(tracker.Paths);
        timers[0]();
        await first;
        Assert.Single(tracker.Paths);

        timers[1]();
        await second;
        Assert.Empty(tracker.Paths);
    }

    [Fact]
    public async Task CompletionWithoutABegin_DoesNotReleaseALaterWrite()
    {
        var timers = new List<Action>();
        var tracker = Tracker(timers);
        var unmatched = tracker.CompleteAsync("/media/item.nfo");
        tracker.Begin("/media/item.nfo");
        var snapshot = tracker.Paths;

        timers[0]();
        await unmatched;
        Assert.Single(tracker.Paths);
        var completion = tracker.CompleteAsync("/media/item.nfo");
        timers[1]();
        await completion;
        Assert.Empty(tracker.Paths);
        Assert.Single(snapshot);
    }

    private static LibraryWriteTracker Tracker(List<Action> timers)
    {
        var time = new Mock<TimeProvider>();
        time.Setup(t => t.CreateTimer(It.IsAny<TimerCallback>(), It.IsAny<object?>(), It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>()))
            .Returns((TimerCallback callback, object? state, TimeSpan due, TimeSpan period) =>
            {
                Assert.Equal(TimeSpan.FromSeconds(45), due);
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                timers.Add(() => callback(state));
                return Mock.Of<ITimer>();
            });
        return new LibraryWriteTracker(time.Object);
    }
}
