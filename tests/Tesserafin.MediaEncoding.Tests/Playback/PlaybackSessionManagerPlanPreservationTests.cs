using System;
using Moq;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Session;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Session;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Playback;

/// <summary>
/// What <see cref="PlaybackSessionManager.Track"/> may change on a session a client created. The
/// session routes call it as a keepalive (<c>PlaybackSessionDeliveryGuard</c>) with no request of
/// its own: it refreshes the stamp the expiry sweep reads, and leaves the plan, the kind and the
/// request - which carries the session's owner - as they are (issues #70 and #71). A
/// <c>Create</c> on the same play session does carry a request, and installs its new plan.
/// </summary>
public class PlaybackSessionManagerPlanPreservationTests
{
    private const string PlaySessionA = "play-session-a";

    private static readonly DateTimeOffset T0 = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The keepalive never replaces what the client planned, even when it is handed another plan
    /// and kind, and never drops the request the owner check reads.
    /// </summary>
    [Fact]
    public void Track_OnClientOwnedSession_PreservesPlanKindAndRequest()
    {
        var fixture = new Fixture();
        var session = fixture.CreateClientSession(PlaySessionA);
        Assert.Equal(PlayMethod.Transcode, session.Plan.PlayMethod);

        var tracked = fixture.Manager.Track(PlaybackMediaKind.Audio, new PlaybackPlan(PlayMethod.DirectStream, default), PlaySessionA);

        Assert.Equal(session.Id, tracked.Id);
        var after = fixture.Manager.Get(session.Id);
        Assert.NotNull(after);
        Assert.Equal(session.Plan, after.Plan);
        Assert.Equal(PlaybackMediaKind.Video, after.Kind);
        Assert.Same(session.Request, after.Request);
    }

    /// <summary>
    /// The keepalive's one effect: <c>UpdatedAt</c>, the only stamp <c>SweepExpired</c> reads,
    /// moves, so a session being played is not reaped.
    /// </summary>
    [Fact]
    public void Track_OnClientOwnedSession_RefreshesUpdatedAt()
    {
        var fixture = new Fixture();
        var session = fixture.CreateClientSession(PlaySessionA);
        fixture.Clock.Now = T0.AddHours(1);

        fixture.Manager.Track(session.Kind, session.Plan, PlaySessionA);

        var after = fixture.Manager.Get(session.Id);
        Assert.NotNull(after);
        Assert.Equal(T0.AddHours(1), after.UpdatedAt);
        Assert.Equal(T0, after.CreatedAt);
    }

    /// <summary>
    /// Second scope boundary. The exemption keys on "the caller contributed no request", not on
    /// client ownership alone — a re-<c>Create</c> (<c>POST</c> reusing a live play session id) does
    /// carry a freshly planned plan and must install it, exactly as before the fix.
    /// </summary>
    [Fact]
    public void Create_TwiceOnSamePlaySessionId_StillInstallsTheNewPlan()
    {
        var fixture = new Fixture();
        var first = fixture.CreateClientSession(PlaySessionA);

        fixture.PlannedPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);
        var second = fixture.CreateClientSession(PlaySessionA);

        Assert.Equal(first.Id, second.Id);
        var after = fixture.Manager.Get(first.Id);
        Assert.NotNull(after);
        Assert.Equal(PlayMethod.DirectPlay, after.Plan.PlayMethod);
    }

    private sealed class Fixture
    {
        public const string MediaSourceId = "source-1";

        public Fixture()
        {
            var options = new MediaOptions();
            Request = new PlaybackSessionRequest(PlaybackMediaKind.Video, options);
            PlannedPlan = new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.ContainerNotSupported);
            var planner = new Mock<IPlaybackSessionPlanner>();
            planner.Setup(p => p.PlanVideo(options)).Returns(() => PlannedPlan);
            Manager = new PlaybackSessionManager(
                planner.Object,
                new Mock<ITranscodeManager>().Object,
                new Mock<ISessionManager>().Object,
                timeProvider: Clock);
        }

        public MutableTimeProvider Clock { get; } = new() { Now = T0 };

        public PlaybackSessionManager Manager { get; }

        public PlaybackSessionRequest Request { get; }

        public PlaybackPlan PlannedPlan { get; set; }

        public PlaybackSession CreateClientSession(string playSessionId)
        {
            var session = Manager.Create(Request, playSessionId);
            Assert.NotNull(session);
            return session;
        }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
