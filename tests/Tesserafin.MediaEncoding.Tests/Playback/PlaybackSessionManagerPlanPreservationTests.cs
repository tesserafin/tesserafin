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
/// Issue #70, the plan-overwrite vector — the half issue #71's lifetime fix left standing.
/// <para>
/// <see cref="PlaybackSessionManager.StoreOrReplace"/> reuses an existing session when a call
/// carries a <c>PlaySessionId</c> already bound to one. Issue #71 taught that replacement to keep
/// the stored <c>Request</c> when the caller has none to contribute — <c>Track</c>, the legacy HLS
/// segment path (<c>DynamicHlsController</c> → <c>TrackTranscodeOutput</c>), always passes
/// <c>request: null</c> — but left <c>Plan</c> and <c>Kind</c> overwriting unconditionally.
/// </para>
/// <para>
/// <c>TrackTranscodeOutput</c> builds <c>new PlaybackPlan(playMethod, transcodeReasons)</c>, whose
/// <c>PlaybackPlan.StreamInfo</c> (since removed, tesserafin#129 step 7) defaults to <c>null</c>
/// (<c>IPlaybackSessionPlanner.cs</c>). So the ordinary client sequence — <c>POST
/// Playback/Sessions</c>, then fetch an HLS segment — replaced the planned session's
/// <c>Plan.StreamInfo</c> with <c>null</c>. <c>GetPlaybackSessionStream</c> reads exactly that
/// (<c>PlaybackSessionsController.cs</c>: <c>legacyStreamInfo = session.Plan.StreamInfo</c>,
/// <c>mediaSource = legacyStreamInfo?.MediaSource</c>) and 422s when either is null, so the session
/// survived (issue #71) but became UNSERVABLE.
/// </para>
/// <para>
/// The fix mirrors #71's discipline exactly: the exemption is scoped, never blanket. It applies
/// only when the caller contributes no request AND the session is client-owned. A legacy-tracked
/// session's plan must still be overwritten by <c>Track</c> — recording the plan actually being
/// executed is that path's whole purpose — and a re-<c>Create</c> on the same play session id must
/// still install its freshly planned plan.
/// </para>
/// </summary>
public class PlaybackSessionManagerPlanPreservationTests
{
    private const string PlaySessionA = "play-session-a";

    private static readonly DateTimeOffset T0 = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The other two locals the same 422 reads. <c>Request</c> is issue #71's guard, already green;
    /// asserted here so a regression on either half fails in the file that explains the endpoint's
    /// three-way null check.
    /// </summary>
    [Fact]
    public void TrackTranscodeOutput_OnClientOwnedSession_PreservesRequestOptionsAndKind()
    {
        var fixture = new Fixture();
        var session = fixture.CreateClientSession(PlaySessionA);

        fixture.Manager.TrackTranscodeOutput("h264", "aac", TranscodeReason.ContainerNotSupported, PlaySessionA);

        var after = fixture.Manager.Get(session.Id);
        Assert.NotNull(after);
        Assert.NotNull(after.Request?.Options);
        Assert.Equal(PlaybackMediaKind.Video, after.Kind);
    }

    /// <summary>
    /// Issue #70, the plan itself, not just its <c>StreamInfo</c>: the transcode decision the v2
    /// planner made must survive, method included. <c>TrackTranscodeOutput</c> would otherwise
    /// re-derive the method from the ffmpeg output codecs alone.
    /// </summary>
    [Fact]
    public void TrackTranscodeOutput_OnClientOwnedSession_PreservesPlannedPlayMethod()
    {
        var fixture = new Fixture();
        var session = fixture.CreateClientSession(PlaySessionA);
        Assert.Equal(PlayMethod.Transcode, session.Plan.PlayMethod);

        // Copy codecs on both streams — TrackTranscodeOutput would otherwise store DirectStream.
        fixture.Manager.TrackTranscodeOutput("copy", "copy", default, PlaySessionA);

        var after = fixture.Manager.Get(session.Id);
        Assert.NotNull(after);
        Assert.Equal(PlayMethod.Transcode, after.Plan.PlayMethod);
    }

    /// <summary>
    /// Scope boundary, mirroring #71's <c>TranscodingJobEnded_ForLegacyTrackedSession_StillRemovesIt</c>.
    /// A session the LEGACY pipeline established on its own is not client-owned, and recording the
    /// plan actually being executed is exactly what <c>Track</c> is for — that must keep working.
    /// This test passes before and after the fix; that is the point.
    /// </summary>
    [Fact]
    public void TrackTranscodeOutput_OnLegacyTrackedSession_StillOverwritesThePlan()
    {
        var fixture = new Fixture();
        var tracked = fixture.Manager.Track(
            PlaybackMediaKind.Video,
            new PlaybackPlan(PlayMethod.DirectPlay, default),
            PlaySessionA);

        fixture.Manager.TrackTranscodeOutput("h264", "aac", TranscodeReason.ContainerNotSupported, PlaySessionA);

        var after = fixture.Manager.Get(tracked.Id);
        Assert.NotNull(after);
        Assert.Equal(PlayMethod.Transcode, after.Plan.PlayMethod);
        Assert.Equal(TranscodeReason.ContainerNotSupported, after.Plan.TranscodeReasons);
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

    /// <summary>
    /// A legacy segment fetch must still refresh <c>UpdatedAt</c>. That stamp is the only one
    /// <c>SweepExpired</c> reads, so freezing it for client-owned sessions would hand issue #71's
    /// TTL backstop a live session to reap.
    /// </summary>
    [Fact]
    public void TrackTranscodeOutput_OnClientOwnedSession_StillRefreshesUpdatedAt()
    {
        var fixture = new Fixture();
        var session = fixture.CreateClientSession(PlaySessionA);
        fixture.Clock.Now = T0.AddHours(1);

        fixture.Manager.TrackTranscodeOutput("h264", "aac", TranscodeReason.ContainerNotSupported, PlaySessionA);

        var after = fixture.Manager.Get(session.Id);
        Assert.NotNull(after);
        Assert.Equal(T0.AddHours(1), after.UpdatedAt);
        Assert.Equal(T0, after.CreatedAt);
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
