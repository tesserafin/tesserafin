using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Diagnostics;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Session;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Session;
using Tesserafin.Playback.Shadow;
using Xunit;

namespace Tesserafin.Playback.Shadow.Tests.Runtime;

/// <summary>
/// Issue #42: the request correlation id is stamped onto each individual
/// <see cref="PlaybackLifecycleEvent"/>, ALONGSIDE the session-scoped
/// <see cref="PlaybackSessionId"/> that groups them — never instead of it, and never shared between
/// events that came from different requests.
/// </summary>
public class PlaybackSessionManagerRequestCorrelationTests
{
    [Fact]
    public void RecordLifecycleEvent_StampsTheCurrentRequestIdOnTheEvent()
    {
        var correlation = new MutableRequestCorrelationAccessor { CurrentRequestId = "request-a" };
        var store = new InMemoryShadowDiagnosticsStore();
        var mockSessionManager = new Mock<ISessionManager>();
        var manager = BuildManager(mockSessionManager, store, correlation);
        var session = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.DirectPlay, default), "play-session-1");

        mockSessionManager.Raise(
            m => m.PlaybackStart += null,
            mockSessionManager.Object,
            new PlaybackProgressEventArgs { PlaySessionId = "play-session-1" });

        var recorded = Assert.Single(store.GetEvents(session.Id));
        Assert.Equal("PlaybackStarted", recorded.Stage);
        Assert.Equal("request-a", recorded.RequestId);
    }

    /// <summary>
    /// Two events on ONE session, observed under two different requests, keep two different request
    /// ids while sharing the same grouping session id. This is the scope hierarchy of issue #42
    /// expressed at the storage layer.
    /// </summary>
    [Fact]
    public void EventsOfOneSession_ObservedUnderDifferentRequests_KeepDistinctRequestIds()
    {
        var correlation = new MutableRequestCorrelationAccessor();
        var store = new InMemoryShadowDiagnosticsStore();
        var mockSessionManager = new Mock<ISessionManager>();
        var mockTranscodeManager = new Mock<ITranscodeManager>();
        var manager = BuildManager(mockSessionManager, store, correlation, mockTranscodeManager);
        var session = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.Transcode, default), "play-session-1");

        correlation.CurrentRequestId = "request-a";
        mockTranscodeManager.Raise(
            m => m.TranscodingJobStarted += null,
            mockTranscodeManager.Object,
            new TranscodingJob(NullLogger<TranscodingJob>.Instance) { PlaySessionId = "play-session-1" });

        correlation.CurrentRequestId = "request-b";
        mockSessionManager.Raise(
            m => m.PlaybackStart += null,
            mockSessionManager.Object,
            new PlaybackProgressEventArgs { PlaySessionId = "play-session-1" });

        var events = store.GetEvents(session.Id);
        Assert.Equal(2, events.Count);
        Assert.Equal(new[] { "request-a", "request-b" }, new[] { events[0].RequestId, events[1].RequestId });

        // Both were filed under the SAME session id — the grouping key is untouched by #42.
        Assert.Equal(2, store.GetEvents(session.Id).Count);
    }

    /// <summary>
    /// No ambient request — a timer sweep, a background callback — records a null request id rather
    /// than borrowing an unrelated one. Diagnostics must not invent correlation.
    /// </summary>
    [Fact]
    public void RecordLifecycleEvent_WithNoAmbientRequest_RecordsNullRequestId()
    {
        var store = new InMemoryShadowDiagnosticsStore();
        var mockSessionManager = new Mock<ISessionManager>();
        var manager = BuildManager(mockSessionManager, store, new MutableRequestCorrelationAccessor());
        var session = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.DirectPlay, default), "play-session-1");

        mockSessionManager.Raise(
            m => m.PlaybackStart += null,
            mockSessionManager.Object,
            new PlaybackProgressEventArgs { PlaySessionId = "play-session-1" });

        Assert.Null(Assert.Single(store.GetEvents(session.Id)).RequestId);
    }

    /// <summary>
    /// The pre-#42 constructor arity still compiles and still behaves identically: no accessor, no
    /// request id. This is what makes the change additive rather than a breaking signature change.
    /// </summary>
    [Fact]
    public void PreIssue42Constructor_StillRecordsEventsWithoutARequestId()
    {
        var store = new InMemoryShadowDiagnosticsStore();
        var mockSessionManager = new Mock<ISessionManager>();
        var manager = new PlaybackSessionManager(
            new Mock<IPlaybackSessionPlanner>().Object,
            new Mock<ITranscodeManager>().Object,
            mockSessionManager.Object,
            store);
        var session = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.DirectPlay, default), "play-session-1");

        mockSessionManager.Raise(
            m => m.PlaybackStart += null,
            mockSessionManager.Object,
            new PlaybackProgressEventArgs { PlaySessionId = "play-session-1" });

        Assert.Null(Assert.Single(store.GetEvents(session.Id)).RequestId);
    }

    private static PlaybackSessionManager BuildManager(
        Mock<ISessionManager> sessionManager,
        IShadowDiagnosticsStore store,
        IRequestCorrelationAccessor correlation,
        Mock<ITranscodeManager>? transcodeManager = null) =>
        new(
            new Mock<IPlaybackSessionPlanner>().Object,
            (transcodeManager ?? new Mock<ITranscodeManager>()).Object,
            sessionManager.Object,
            store,
            null,
            null,
            null,
            correlation);

    private sealed class MutableRequestCorrelationAccessor : IRequestCorrelationAccessor
    {
        public string? CurrentRequestId { get; set; }
    }
}
