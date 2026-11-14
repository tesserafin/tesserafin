using System;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Session;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Session;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Engine;
using Tesserafin.Playback.Shadow;
using Xunit;

namespace Tesserafin.Playback.Shadow.Tests.Runtime;

/// <summary>
/// End-to-end retention tests for PR115a: wires the real <see cref="ShadowPlaybackSessionPlanner"/>
/// (mock inner planner + mock v2 engine) into a real <see cref="PlaybackSessionManager"/>, both
/// sharing one <see cref="InMemoryV2PlanStore"/> instance - exactly the DI shape <c>ApplicationHost</c>
/// wires. Mirrors <see cref="PlaybackSessionManagerDiagnosticsRetentionTests"/>'s shape, but for the
/// AUTHORITATIVE v2 channel rather than the observability one: proves
/// <see cref="ShadowPlaybackSessionPlanner.RunShadow"/> actually publishes a <see cref="V2PlanRecord"/>
/// into the ambient slot only when the effective mode makes it authoritative, and that
/// <see cref="PlaybackSessionManager"/> correlates it onto the real, freshly-minted session id -
/// attaching, evicting, or leaving untouched exactly as the retained diagnostics record does.
/// </summary>
public class PlaybackSessionManagerV2PlanRetentionTests
{
    [Fact]
    public void Create_ModeV2_RetainsRecordWithNonNullExecutionPlanKeyedByRealSessionId()
    {
        var options = CreateOptions();
        var legacyPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);
        var v2Store = new InMemoryV2PlanStore();
        var manager = BuildManager(options, legacyPlan, v2Store, () => PlaybackEngineMode.V2);

        var session = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, options));

        Assert.NotNull(session);
        Assert.True(v2Store.TryGet(session!.Id, out var record));
        Assert.NotNull(record);
        Assert.NotNull(record!.ExecutionPlan);
        Assert.True(record.Decision.IsViable);
    }

    [Fact]
    public void Create_ModeShadow_RetainsNoRecord()
    {
        var options = CreateOptions();
        var legacyPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);
        var v2Store = new InMemoryV2PlanStore();
        var manager = BuildManager(options, legacyPlan, v2Store, () => PlaybackEngineMode.Shadow);

        var session = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, options));

        Assert.NotNull(session);
        Assert.False(v2Store.TryGet(session!.Id, out var record));
        Assert.Null(record);
    }

    [Fact]
    public void Patch_ConfigFlippedToShadow_EvictsPreviouslyAttachedV2Record()
    {
        var initialOptions = CreateOptions();
        var patchedOptions = CreateOptions();
        var initialPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);
        var patchedPlan = new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported);
        var v2Store = new InMemoryV2PlanStore();

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(initialOptions)).Returns(initialPlan);
        mockInner.Setup(p => p.PlanVideo(patchedOptions)).Returns(patchedPlan);
        var mockEngine = BuildEngineMock();

        // Mutable mode captured by the accessor closure - Create runs with V2 authoritative, then
        // config flips to Shadow before the Patch, mirroring a mid-rollout mode change.
        var mode = PlaybackEngineMode.V2;
        var shadowPlanner = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Mode = mode, SampleRate = 1.0 },
            metrics: null,
            diagnosticsStore: null,
            v2PlanStore: v2Store);
        var manager = new PlaybackSessionManager(shadowPlanner, new Mock<ITranscodeManager>().Object, new Mock<ISessionManager>().Object, diagnosticsStore: null, v2PlanStore: v2Store);

        var session = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, initialOptions));
        Assert.NotNull(session);
        Assert.True(v2Store.TryGet(session!.Id, out _));

        mode = PlaybackEngineMode.Shadow;
        var patched = manager.Patch(session.Id, new PlaybackSessionRequest(PlaybackMediaKind.Video, patchedOptions));

        Assert.NotNull(patched);
        Assert.False(v2Store.TryGet(patched!.Id, out var record));
        Assert.Null(record);
    }

    /// <summary>
    /// Issue #70, candidate 2 — the PUT atomicity guard. When the effective mode is AUTHORITATIVE
    /// and re-planning yields a record whose <c>ExecutionPlan</c> is null (the engine refused:
    /// NotViable, NoStreamsSelected, MissingOutputContainer — <c>PlaybackExecutionPlanBuilder</c>),
    /// there is nothing executable to switch the session to. The legacy <c>StreamBuilder</c>
    /// DEMOTES rather than refusing, so <c>Plan()</c> is non-null and
    /// <c>Patch</c>'s existing early return at "plan is null" does NOT fire — the PUT used to
    /// destroy the good previous record at the attach site and answer 200 with a session that could
    /// then only be served from legacy. It must answer 422 instead (the controller maps a null
    /// <c>Patch</c> to <c>UnprocessableEntity</c>) and change nothing at all.
    /// </summary>
    [Fact]
    public void Patch_AuthoritativeReplanWithNoExecutablePlan_ReturnsNull()
    {
        var fixture = new UnexecutableReplanFixture();

        var patched = fixture.Manager.Patch(fixture.Session.Id, new PlaybackSessionRequest(PlaybackMediaKind.Video, fixture.PatchedOptions));

        Assert.Null(patched);
    }

    /// <summary>
    /// Issue #70: the refused PUT must leave the SESSION exactly as it was — same plan, same kind,
    /// same request. A partially applied re-plan is worse than a refused one: the client is told the
    /// change failed while the server already moved.
    /// </summary>
    [Fact]
    public void Patch_AuthoritativeReplanWithNoExecutablePlan_LeavesPreviousSessionAndPlanIntact()
    {
        var fixture = new UnexecutableReplanFixture();

        Assert.Null(fixture.Manager.Patch(fixture.Session.Id, new PlaybackSessionRequest(PlaybackMediaKind.Video, fixture.PatchedOptions)));

        var after = fixture.Manager.Get(fixture.Session.Id);
        Assert.NotNull(after);
        Assert.Equal(PlayMethod.DirectPlay, after!.Plan.PlayMethod);
        Assert.Same(fixture.InitialOptions, after.Request?.Options);
        Assert.Equal(fixture.Session.UpdatedAt, after.UpdatedAt);
    }

    /// <summary>
    /// Issue #70, the actual destruction vector: the V2PlanStore entry. The good, executable record
    /// the POST attached must still be there and still be usable — that record is what
    /// <c>PlaybackExecutionPlanResolver</c> reads to serve the stream by v2 rather than falling back
    /// with <c>PlanNotExecutable</c>.
    /// </summary>
    [Fact]
    public void Patch_AuthoritativeReplanWithNoExecutablePlan_LeavesPreviousV2RecordAttachedAndExecutable()
    {
        var fixture = new UnexecutableReplanFixture();
        Assert.True(fixture.V2Store.TryGet(fixture.Session.Id, out var before));

        Assert.Null(fixture.Manager.Patch(fixture.Session.Id, new PlaybackSessionRequest(PlaybackMediaKind.Video, fixture.PatchedOptions)));

        Assert.True(fixture.V2Store.TryGet(fixture.Session.Id, out var after));
        Assert.NotNull(after);
        Assert.Same(before, after);
        Assert.NotNull(after!.ExecutionPlan);
        Assert.True(after.Decision.IsViable);
    }

    /// <summary>
    /// Issue #70, scope boundary. The guard fires only when the mode makes v2 AUTHORITATIVE. In
    /// Shadow the engine's refusal is an observation, not an execution outcome: legacy still plans
    /// the session, so the PUT must keep succeeding exactly as before — and the stale authoritative
    /// record from the earlier V2-mode call must still be evicted, which is the existing
    /// <c>Patch_ConfigFlippedToShadow_EvictsPreviouslyAttachedV2Record</c> contract.
    /// </summary>
    [Fact]
    public void Patch_ShadowModeReplanWithNoExecutablePlan_StillSucceeds()
    {
        var fixture = new UnexecutableReplanFixture();
        fixture.Mode = PlaybackEngineMode.Shadow;

        var patched = fixture.Manager.Patch(fixture.Session.Id, new PlaybackSessionRequest(PlaybackMediaKind.Video, fixture.PatchedOptions));

        Assert.NotNull(patched);
        Assert.Equal(PlayMethod.Transcode, patched!.Plan.PlayMethod);
        Assert.False(fixture.V2Store.TryGet(fixture.Session.Id, out _));
    }

    /// <summary>
    /// Issue #70, second scope boundary: an authoritative re-plan that DOES produce an executable
    /// plan must keep replacing both the session and the retained record. The guard must key on
    /// "nothing executable came out", never on "the mode is authoritative".
    /// </summary>
    [Fact]
    public void Patch_AuthoritativeReplanWithExecutablePlan_StillReplacesSessionAndRecord()
    {
        var fixture = new UnexecutableReplanFixture();
        fixture.EngineDecision = ViableDecision();

        var patched = fixture.Manager.Patch(fixture.Session.Id, new PlaybackSessionRequest(PlaybackMediaKind.Video, fixture.PatchedOptions));

        Assert.NotNull(patched);
        Assert.Equal(PlayMethod.Transcode, patched!.Plan.PlayMethod);
        Assert.True(fixture.V2Store.TryGet(fixture.Session.Id, out var record));
        Assert.NotNull(record!.ExecutionPlan);
    }

    [Fact]
    public void Delete_ExistingSessionWithRetainedV2Record_EvictsRecord()
    {
        var options = CreateOptions();
        var legacyPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);
        var v2Store = new InMemoryV2PlanStore();
        var manager = BuildManager(options, legacyPlan, v2Store, () => PlaybackEngineMode.V2);
        var session = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, options));
        Assert.NotNull(session);
        Assert.True(v2Store.TryGet(session!.Id, out _));

        var deleted = manager.Delete(session.Id);

        Assert.True(deleted);
        Assert.False(v2Store.TryGet(session.Id, out var record));
        Assert.Null(record);
    }

    private static PlaybackSessionManager BuildManager(
        MediaOptions options,
        PlaybackPlan legacyPlan,
        InMemoryV2PlanStore v2Store,
        Func<PlaybackEngineMode> modeAccessor)
    {
        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(legacyPlan);
        var mockEngine = BuildEngineMock();

        var shadowPlanner = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Mode = modeAccessor(), SampleRate = 1.0 },
            metrics: null,
            diagnosticsStore: null,
            v2PlanStore: v2Store);

        return new PlaybackSessionManager(shadowPlanner, new Mock<ITranscodeManager>().Object, new Mock<ISessionManager>().Object, diagnosticsStore: null, v2PlanStore: v2Store);
    }

    private static Mock<IPlaybackEngine> BuildEngineMock()
    {
        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<System.Collections.Generic.IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Returns(ViableDecision());
        return mockEngine;
    }

    private static PlaybackDecision ViableDecision() => PlaybackDecision.DirectPlay(
        "source-1",
        new SelectedStreams(Video: 0, Audio: 1, Subtitle: null),
        new OutputSpec(
            Container: "mp4",
            VideoCodec: "h264",
            AudioCodec: "aac",
            Resolution: null,
            VideoRange: null,
            AudioChannels: null,
            TotalBitrate: null,
            VideoBitrate: null,
            AudioBitrate: null,
            Protocol: StreamingProtocol.Http,
            SubtitleFormat: null),
        ReasonNode.Leaf(ReasonCode.MethodChosen, ReasonOutcome.Chosen, ReasonSubject.Method()),
        engineVersion: PlaybackEngine.EngineVersion);

    private static MediaOptions CreateOptions() => new() { Profile = new DeviceProfile() };

    /// <summary>
    /// Issue #70, candidate 2. Reproduces the exact shape the defect needs: a session created while
    /// v2 is authoritative and executable, then a PUT whose LEGACY plan is perfectly viable (the
    /// StreamBuilder demotes rather than refusing) while the v2 engine refuses outright, publishing
    /// an authoritative record with a null <c>ExecutionPlan</c>.
    /// </summary>
    private sealed class UnexecutableReplanFixture
    {
        public UnexecutableReplanFixture()
        {
            InitialOptions = CreateOptions();
            PatchedOptions = CreateOptions();
            EngineDecision = ViableDecision();

            var mockInner = new Mock<IPlaybackSessionPlanner>();
            mockInner.Setup(p => p.PlanVideo(InitialOptions)).Returns(new PlaybackPlan(PlayMethod.DirectPlay, default));
            mockInner.Setup(p => p.PlanVideo(PatchedOptions)).Returns(new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported));

            var mockEngine = new Mock<IPlaybackEngine>();
            mockEngine
                .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<System.Collections.Generic.IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
                .Returns(() => EngineDecision);

            var shadowPlanner = new ShadowPlaybackSessionPlanner(
                mockInner.Object,
                mockEngine.Object,
                NullLogger<ShadowPlaybackSessionPlanner>.Instance,
                () => new PlaybackShadowOptions { Mode = Mode, SampleRate = 1.0 },
                metrics: null,
                diagnosticsStore: null,
                v2PlanStore: V2Store);
            Manager = new PlaybackSessionManager(shadowPlanner, new Mock<ITranscodeManager>().Object, new Mock<ISessionManager>().Object, diagnosticsStore: null, v2PlanStore: V2Store);

            var created = Manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, InitialOptions));
            Assert.NotNull(created);
            Session = created!;
            Assert.True(V2Store.TryGet(Session.Id, out var record));
            Assert.NotNull(record!.ExecutionPlan);

            // Only from here on does the engine refuse - the session already holds a good record.
            EngineDecision = PlaybackDecision.NotViable(
                PlaybackMethod.Transcode,
                new ReasonNode(ReasonCode.NoViablePlan, ReasonOutcome.Rejected, ReasonSubject.Method(), null, []),
                PlaybackEngine.EngineVersion);
        }

        public InMemoryV2PlanStore V2Store { get; } = new();

        public PlaybackSessionManager Manager { get; }

        public PlaybackSession Session { get; }

        public MediaOptions InitialOptions { get; }

        public MediaOptions PatchedOptions { get; }

        public PlaybackEngineMode Mode { get; set; } = PlaybackEngineMode.V2;

        public PlaybackDecision EngineDecision { get; set; }
    }
}
