using System;
using Moq;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Session;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Session;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Playback;

public sealed class PlaybackSessionDiagnosticsBoundaryTests
{
    [Fact]
    public void SuccessfulPlanning_AttachesObservationBeforeDisposingCapture_ThenEvictsOnDelete()
    {
        var options = new MediaOptions { Profile = new DeviceProfile() };
        var planner = new Mock<IPlaybackSessionPlanner>();
        var diagnostics = new Mock<IPlaybackSessionDiagnostics>(MockBehavior.Strict);
        var capture = new Mock<IPlaybackPlanningDiagnostics>(MockBehavior.Strict);
        var sequence = new MockSequence();
        PlaybackSessionId? attached = null;
        diagnostics.InSequence(sequence).Setup(x => x.BeginPlanning()).Returns(capture.Object);
        planner.InSequence(sequence).Setup(x => x.PlanVideo(options)).Returns(new PlaybackPlan(PlayMethod.DirectPlay, default));
        capture.InSequence(sequence).Setup(x => x.AttachTo(It.IsAny<PlaybackSessionId>())).Callback<PlaybackSessionId>(id => attached = id);
        capture.InSequence(sequence).Setup(x => x.Dispose());
        diagnostics.InSequence(sequence).Setup(x => x.Remove(It.IsAny<PlaybackSessionId>()));
        using var manager = new PlaybackSessionManager(planner.Object, Mock.Of<ITranscodeManager>(), Mock.Of<ISessionManager>(), diagnostics.Object);

        var session = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, options));

        Assert.NotNull(session);
        Assert.Equal(session.Id, attached);
        Assert.True(manager.Delete(session.Id));
        diagnostics.Verify(x => x.Remove(session.Id), Times.Once);
        diagnostics.VerifyAll();
        capture.VerifyAll();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedPlanning_DiscardsCaptureWithoutAttaching(bool throws)
    {
        var planner = new Mock<IPlaybackSessionPlanner>();
        var capture = new Mock<IPlaybackPlanningDiagnostics>(MockBehavior.Strict);
        var diagnostics = new Mock<IPlaybackSessionDiagnostics>(MockBehavior.Strict);
        diagnostics.Setup(x => x.BeginPlanning()).Returns(capture.Object);
        capture.Setup(x => x.Dispose());
        var setup = planner.Setup(x => x.PlanVideo(It.IsAny<MediaOptions>()));
        if (throws)
        {
            setup.Throws(new InvalidOperationException("planning failed"));
        }
        else
        {
            setup.Returns((PlaybackPlan?)null);
        }

        using var manager = new PlaybackSessionManager(planner.Object, Mock.Of<ITranscodeManager>(), Mock.Of<ISessionManager>(), diagnostics.Object);
        var request = new PlaybackSessionRequest(PlaybackMediaKind.Video, new MediaOptions { Profile = new DeviceProfile() });
        if (throws)
        {
            Assert.Throws<InvalidOperationException>(() => manager.Create(request));
        }
        else
        {
            Assert.Null(manager.Create(request));
        }

        capture.Verify(x => x.Dispose(), Times.Once);
        capture.VerifyNoOtherCalls();
        diagnostics.Verify(x => x.BeginPlanning(), Times.Once);
        diagnostics.VerifyNoOtherCalls();
    }
}
