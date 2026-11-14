using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Session;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Session;
using Xunit;

namespace Tesserafin.Playback.Shadow.Tests.Runtime;

public sealed class PlaybackSessionLifecycleRetentionTests
{
    [Fact]
    public void TranscodingJobStarted_RecordsFfmpegStartedEventForMatchingSession()
    {
        var mockTranscodeManager = new Mock<ITranscodeManager>();
        var store = new InMemoryShadowDiagnosticsStore();
        var manager = new PlaybackSessionManager(new Mock<IPlaybackSessionPlanner>().Object, mockTranscodeManager.Object, new Mock<ISessionManager>().Object, store);
        var session = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.Transcode, default), "play-session-1");

        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance) { PlaySessionId = "play-session-1" };
        mockTranscodeManager.Raise(m => m.TranscodingJobStarted += null, mockTranscodeManager.Object, job);

        var recorded = Assert.Single(store.GetEvents(session.Id));
        Assert.Equal("FfmpegStarted", recorded.Stage);
    }

    [Fact]
    public void TranscodingJobStarted_UnknownPlaySessionId_RecordsNothing()
    {
        var mockTranscodeManager = new Mock<ITranscodeManager>();
        var store = new InMemoryShadowDiagnosticsStore();
        var manager = new PlaybackSessionManager(new Mock<IPlaybackSessionPlanner>().Object, mockTranscodeManager.Object, new Mock<ISessionManager>().Object, store);
        var session = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.Transcode, default), "play-session-1");

        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance) { PlaySessionId = "some-other-play-session" };
        mockTranscodeManager.Raise(m => m.TranscodingJobStarted += null, mockTranscodeManager.Object, job);

        Assert.Empty(store.GetEvents(session.Id));
    }

    [Fact]
    public void PlaybackStart_RecordsPlaybackStartedEventForMatchingSession()
    {
        var mockSessionManager = new Mock<ISessionManager>();
        var store = new InMemoryShadowDiagnosticsStore();
        var manager = new PlaybackSessionManager(new Mock<IPlaybackSessionPlanner>().Object, new Mock<ITranscodeManager>().Object, mockSessionManager.Object, store);
        var session = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.DirectPlay, default), "play-session-1");

        var args = new PlaybackProgressEventArgs { PlaySessionId = "play-session-1" };
        mockSessionManager.Raise(m => m.PlaybackStart += null, mockSessionManager.Object, args);

        var recorded = Assert.Single(store.GetEvents(session.Id));
        Assert.Equal("PlaybackStarted", recorded.Stage);
    }
}
