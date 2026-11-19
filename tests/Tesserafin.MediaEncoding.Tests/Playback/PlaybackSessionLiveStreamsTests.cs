using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Session;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Session;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Playback;

/// <summary>
/// A Live TV session holds one reference on the tuner stream it opened, and every way a session
/// ends releases it exactly once.
/// </summary>
public sealed class PlaybackSessionLiveStreamsTests : IDisposable
{
    private readonly ConcurrentQueue<string> _closed = new();
    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero));
    private readonly PlaybackSessionLiveStreams _liveStreams;
    private readonly PlaybackSessionManager _manager;
    private readonly PlaybackSessionRequest _request;

    public PlaybackSessionLiveStreamsTests()
    {
        var mediaSources = new Mock<IMediaSourceManager>();
        mediaSources.Setup(m => m.CloseLiveStream(It.IsAny<string>()))
            .Callback<string>(_closed.Enqueue)
            .Returns(Task.CompletedTask);
        _liveStreams = new PlaybackSessionLiveStreams(mediaSources.Object, NullLogger<PlaybackSessionLiveStreams>.Instance);

        var options = new MediaOptions { Profile = new DeviceProfile() };
        _request = new PlaybackSessionRequest(PlaybackMediaKind.Video, options);
        var planner = new Mock<IPlaybackSessionPlanner>();
        planner.Setup(p => p.PlanVideo(options)).Returns(new PlaybackPlan(PlayMethod.Transcode, default));
        _manager = new PlaybackSessionManager(
            planner.Object,
            new Mock<ITranscodeManager>().Object,
            new Mock<ISessionManager>().Object,
            logger: NullLogger<PlaybackSessionManager>.Instance,
            timeProvider: _clock,
            liveStreams: _liveStreams);
    }

    public void Dispose() => _manager.Dispose();

    [Fact]
    public async Task Deleting_the_session_closes_its_tuner_stream_once()
    {
        var session = _manager.Create(_request, "ps-delete")!;
        _liveStreams.Attach(session.Id, "live-1");

        Assert.True(_manager.Delete(session.Id));
        Assert.False(_manager.Delete(session.Id));

        await ClosedAsync("live-1");
        Assert.Null(_liveStreams.Get(session.Id));
    }

    [Fact]
    public async Task The_ttl_sweep_closes_the_tuner_stream_of_an_abandoned_session()
    {
        var session = _manager.Create(_request, "ps-sweep")!;
        _liveStreams.Attach(session.Id, "live-2");

        Assert.Equal(1, _manager.SweepExpired(_clock.GetUtcNow() + TimeSpan.FromDays(1)));

        await ClosedAsync("live-2");
    }

    [Fact]
    public async Task A_replacement_that_opened_another_stream_closes_the_previous_one_only()
    {
        var session = _manager.Create(_request, "ps-replace")!;
        _liveStreams.Attach(session.Id, "live-3");
        _liveStreams.Attach(session.Id, "live-3");
        _liveStreams.Attach(session.Id, "live-4");

        await ClosedAsync("live-3");
        Assert.Equal("live-4", _liveStreams.Get(session.Id));
    }

    [Fact]
    public async Task A_stream_opened_for_a_session_that_was_never_created_is_closed()
    {
        await _liveStreams.CloseUnattachedAsync("live-5");

        Assert.Equal(["live-5"], _closed.ToArray());
    }

    [Fact]
    public void A_session_without_a_tuner_stream_closes_nothing()
    {
        var session = _manager.Create(_request, "ps-plain")!;

        Assert.True(_manager.Delete(session.Id));
        Assert.Empty(_closed);
    }

    private async Task ClosedAsync(string liveStreamId)
    {
        for (var attempt = 0; attempt < 100 && !_closed.Contains(liveStreamId); attempt++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Single(_closed, id => id == liveStreamId);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
