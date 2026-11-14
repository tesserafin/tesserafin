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
/// End-to-end retention tests for PR113: wires the real <see cref="ShadowPlaybackSessionPlanner"/>
/// (mock inner planner + mock v2 engine) into a real <see cref="PlaybackSessionManager"/>, both
/// sharing one <see cref="InMemoryShadowDiagnosticsStore"/> instance - exactly the DI shape
/// <c>ApplicationHost</c> wires. Unlike a test that mocks <see cref="IShadowDiagnosticsStore.Publish"/>
/// directly, this proves the whole handshake: that <see cref="ShadowPlaybackSessionPlanner.RunShadow"/>
/// actually publishes into the ambient slot, and that <see cref="PlaybackSessionManager"/> correlates
/// it onto the real, freshly-minted session id.
/// </summary>
public class PlaybackSessionManagerDiagnosticsRetentionTests
{
    [Fact]
    public void Create_ShadowEnabled_RetainsRecordKeyedByRealSessionId()
    {
        var options = CreateOptions();
        var legacyPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);
        var store = new InMemoryShadowDiagnosticsStore();
        var manager = BuildManager(options, legacyPlan, store, shadowEnabled: true);

        var session = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, options));

        Assert.NotNull(session);
        Assert.True(store.TryGet(session.Id, out var record));
        Assert.NotNull(record);
        Assert.Equal(PlaybackMediaKind.Video, record!.Kind);
        Assert.True(record.Decision.IsViable);
    }

    [Fact]
    public void Create_ShadowDisabled_RetainsNoRecord()
    {
        var options = CreateOptions();
        var legacyPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);
        var store = new InMemoryShadowDiagnosticsStore();
        var manager = BuildManager(options, legacyPlan, store, shadowEnabled: false);

        var session = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, options));

        Assert.NotNull(session);
        Assert.False(store.TryGet(session.Id, out var record));
        Assert.Null(record);
    }

    [Fact]
    public void Patch_ShadowEnabled_RetainsRecordKeyedByExistingSessionId()
    {
        var initialOptions = CreateOptions();
        var patchedOptions = CreateOptions();
        var initialPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);
        var patchedPlan = new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported);
        var store = new InMemoryShadowDiagnosticsStore();

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(initialOptions)).Returns(initialPlan);
        mockInner.Setup(p => p.PlanVideo(patchedOptions)).Returns(patchedPlan);
        var mockEngine = BuildEngineMock();
        var shadowPlanner = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Enabled = true, SampleRate = 1.0 },
            metrics: null,
            diagnosticsStore: store);
        var manager = new PlaybackSessionManager(shadowPlanner, new Mock<ITranscodeManager>().Object, new Mock<ISessionManager>().Object, store);
        var session = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, initialOptions));
        Assert.NotNull(session);

        var patched = manager.Patch(session.Id, new PlaybackSessionRequest(PlaybackMediaKind.Video, patchedOptions));

        Assert.NotNull(patched);
        Assert.True(store.TryGet(patched!.Id, out var record));
        Assert.NotNull(record);
    }

    [Fact]
    public void Patch_NoNewCapture_EvictsPreviouslyAttachedRecord()
    {
        var initialOptions = CreateOptions();
        var patchedOptions = CreateOptions();
        var initialPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);
        var patchedPlan = new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported);
        var store = new InMemoryShadowDiagnosticsStore();

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(initialOptions)).Returns(initialPlan);
        mockInner.Setup(p => p.PlanVideo(patchedOptions)).Returns(patchedPlan);
        var mockEngine = BuildEngineMock();

        // Shadow enabled for the initial Create (so a record gets attached), then disabled before
        // the Patch - mirrors a replan that succeeds without a new capture (shadow toggled off or
        // not sampled this call), the PR113a bug scenario.
        var shadowEnabled = true;
        var shadowPlanner = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Enabled = shadowEnabled, SampleRate = 1.0 },
            metrics: null,
            diagnosticsStore: store);
        var manager = new PlaybackSessionManager(shadowPlanner, new Mock<ITranscodeManager>().Object, new Mock<ISessionManager>().Object, store);

        var session = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, initialOptions));
        Assert.NotNull(session);
        Assert.True(store.TryGet(session!.Id, out _));

        shadowEnabled = false;
        var patched = manager.Patch(session.Id, new PlaybackSessionRequest(PlaybackMediaKind.Video, patchedOptions));

        Assert.NotNull(patched);
        Assert.False(store.TryGet(patched!.Id, out var record));
        Assert.Null(record);
    }

    [Fact]
    public void Create_ReplayedPlaySessionId_NoNewCapture_EvictsPreviouslyAttachedRecord()
    {
        var initialOptions = CreateOptions();
        var replayOptions = CreateOptions();
        var initialPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);
        var replayPlan = new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported);
        var store = new InMemoryShadowDiagnosticsStore();

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(initialOptions)).Returns(initialPlan);
        mockInner.Setup(p => p.PlanVideo(replayOptions)).Returns(replayPlan);
        var mockEngine = BuildEngineMock();

        // Same shadow-toggle shape as the Patch test above, but exercised through Create with a
        // reused playSessionId - StoreOrReplace keeps the existing session id in that case, so the
        // same stale-attachment bug applies to Create, not just Patch.
        var shadowEnabled = true;
        var shadowPlanner = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Enabled = shadowEnabled, SampleRate = 1.0 },
            metrics: null,
            diagnosticsStore: store);
        var manager = new PlaybackSessionManager(shadowPlanner, new Mock<ITranscodeManager>().Object, new Mock<ISessionManager>().Object, store);

        const string playSessionId = "play-session-1";
        var session = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, initialOptions), playSessionId);
        Assert.NotNull(session);
        Assert.True(store.TryGet(session!.Id, out _));

        shadowEnabled = false;
        var replayed = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, replayOptions), playSessionId);

        Assert.NotNull(replayed);
        Assert.Equal(session.Id, replayed!.Id);
        Assert.False(store.TryGet(replayed.Id, out var record));
        Assert.Null(record);
    }

    [Fact]
    public void Delete_ExistingSessionWithRetainedRecord_EvictsRecord()
    {
        var options = CreateOptions();
        var legacyPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);
        var store = new InMemoryShadowDiagnosticsStore();
        var manager = BuildManager(options, legacyPlan, store, shadowEnabled: true);
        var session = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, options));
        Assert.NotNull(session);
        Assert.True(store.TryGet(session!.Id, out _));

        var deleted = manager.Delete(session.Id);

        Assert.True(deleted);
        Assert.False(store.TryGet(session.Id, out var record));
        Assert.Null(record);
    }

    private static PlaybackSessionManager BuildManager(MediaOptions options, PlaybackPlan legacyPlan, InMemoryShadowDiagnosticsStore store, bool shadowEnabled)
    {
        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(legacyPlan);
        var mockEngine = BuildEngineMock();

        var shadowPlanner = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Enabled = shadowEnabled, SampleRate = 1.0 },
            metrics: null,
            diagnosticsStore: store);

        return new PlaybackSessionManager(shadowPlanner, new Mock<ITranscodeManager>().Object, new Mock<ISessionManager>().Object, store);
    }

    private static Mock<IPlaybackEngine> BuildEngineMock()
    {
        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<System.Collections.Generic.IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Returns(PlaybackDecision.DirectPlay(
                "source-1",
                SelectedStreams.None,
                OutputSpec.Empty,
                ReasonNode.Leaf(ReasonCode.MethodChosen, ReasonOutcome.Chosen, ReasonSubject.Method()),
                engineVersion: PlaybackEngine.EngineVersion));
        return mockEngine;
    }

    private static MediaOptions CreateOptions() => new() { Profile = new DeviceProfile() };
}
