using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Session;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Session;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Dlna;
using Tesserafin.Playback.Engine;
using Tesserafin.Playback.Execution;
using Tesserafin.Playback.Shadow;
using Xunit;
using DomainClientCapabilities = Tesserafin.Playback.Decision.ClientCapabilities;

namespace Tesserafin.Playback.Shadow.Tests.Runtime;

/// <summary>
/// Tests for <see cref="PlaybackExecutionPlanResolver"/> (PR115a): unit-level unknown/refused/viable
/// lookup cases against a bare <see cref="InMemoryV2PlanStore"/>, plus an end-to-end test wiring the
/// real <see cref="PlaybackSessionManager"/>/<see cref="ShadowPlaybackSessionPlanner"/>/
/// <see cref="InMemoryV2PlanStore"/> (same shape as
/// <see cref="PlaybackSessionManagerV2PlanRetentionTests"/>) that creates a canary-authoritative
/// session, resolves its plan by session id, and converts it to a legacy <see cref="StreamInfo"/> via
/// <see cref="PlaybackExecutionPlanAdapter"/> - proving the whole
/// <c>PlaybackDecision -&gt; PlaybackExecutionPlan -&gt; StreamInfo</c> contract this PR builds, even
/// though nothing on the live streaming path consumes it yet.
/// </summary>
/// <remarks>
/// PR115a: the resolver now reads a session's AUTHORITATIVE <see cref="V2PlanRecord"/> straight off
/// <see cref="IV2PlanStore"/> - the plan is already built at publish time by
/// <see cref="ShadowPlaybackSessionPlanner"/> (only when v2 was authoritative for that call), so there
/// is no <see cref="PlaybackExecutionPlanBuilder.TryBuild"/> call inside the resolver anymore, just a
/// lookup. It no longer reads <see cref="IShadowDiagnosticsStore"/> at all: that store only ever holds
/// observability data, never an authoritative decision.
/// </remarks>
public class PlaybackExecutionPlanResolverTests
{
    [Fact]
    public void Resolve_UnknownSessionId_ReturnsNull()
    {
        var store = new InMemoryV2PlanStore();
        var resolver = new PlaybackExecutionPlanResolver(store);

        var plan = resolver.Resolve(PlaybackSessionId.NewId());

        Assert.Null(plan);
    }

    [Fact]
    public void Resolve_RecordHoldsNullExecutionPlan_ReturnsNull()
    {
        // A record can be retained with a null ExecutionPlan when the builder refused it at publish
        // time (for example a NotViable decision) - v2 was still authoritative for the session, it
        // just produced nothing executable.
        var store = new InMemoryV2PlanStore();
        var id = PlaybackSessionId.NewId();
        var notViable = PlaybackDecision.NotViable(
            PlaybackMethod.Transcode,
            new ReasonNode(ReasonCode.NoViablePlan, ReasonOutcome.Rejected, ReasonSubject.Method(), null, []),
            engineVersion: PlaybackEngine.EngineVersion);
        store.Attach(id, new V2PlanRecord(notViable, ExecutionPlan: null, DateTimeOffset.UtcNow));
        var resolver = new PlaybackExecutionPlanResolver(store);

        var plan = resolver.Resolve(id);

        Assert.Null(plan);
    }

    [Fact]
    public void Resolve_RecordHoldsViableDecision_ReturnsTheStoredPlan()
    {
        var store = new InMemoryV2PlanStore();
        var id = PlaybackSessionId.NewId();
        var decision = BuildViableDirectPlayDecision();
        Assert.True(PlaybackExecutionPlanBuilder.TryBuild(decision, out var builtPlan, out _));
        store.Attach(id, new V2PlanRecord(decision, builtPlan, DateTimeOffset.UtcNow));
        var resolver = new PlaybackExecutionPlanResolver(store);

        var plan = resolver.Resolve(id);

        Assert.NotNull(plan);
        Assert.Same(builtPlan, plan);
        Assert.Equal(PlaybackMethod.DirectPlay, plan!.Method);
        Assert.Equal("source-1", plan.SourceId);
    }

    [Fact]
    public void Resolve_Detailed_UnknownSessionId_ReturnsNoAuthoritativeRecord()
    {
        var store = new InMemoryV2PlanStore();
        var resolver = new PlaybackExecutionPlanResolver(store);

        var resolution = resolver.Resolve(PlaybackSessionId.NewId(), out var plan);

        Assert.Equal(PlaybackExecutionPlanResolution.NoAuthoritativeRecord, resolution);
        Assert.Null(plan);
    }

    [Fact]
    public void Resolve_Detailed_RecordHoldsNullExecutionPlan_ReturnsPlanNotExecutable()
    {
        var store = new InMemoryV2PlanStore();
        var id = PlaybackSessionId.NewId();
        var notViable = PlaybackDecision.NotViable(
            PlaybackMethod.Transcode,
            new ReasonNode(ReasonCode.NoViablePlan, ReasonOutcome.Rejected, ReasonSubject.Method(), null, []),
            engineVersion: PlaybackEngine.EngineVersion);
        store.Attach(id, new V2PlanRecord(notViable, ExecutionPlan: null, DateTimeOffset.UtcNow));
        var resolver = new PlaybackExecutionPlanResolver(store);

        var resolution = resolver.Resolve(id, out var plan);

        Assert.Equal(PlaybackExecutionPlanResolution.PlanNotExecutable, resolution);
        Assert.Null(plan);
    }

    [Fact]
    public void Resolve_Detailed_RecordHoldsViableDecision_ReturnsResolvedWithTheStoredPlan()
    {
        var store = new InMemoryV2PlanStore();
        var id = PlaybackSessionId.NewId();
        var decision = BuildViableDirectPlayDecision();
        Assert.True(PlaybackExecutionPlanBuilder.TryBuild(decision, out var builtPlan, out _));
        store.Attach(id, new V2PlanRecord(decision, builtPlan, DateTimeOffset.UtcNow));
        var resolver = new PlaybackExecutionPlanResolver(store);

        var resolution = resolver.Resolve(id, out var plan);

        Assert.Equal(PlaybackExecutionPlanResolution.Resolved, resolution);
        Assert.Same(builtPlan, plan);
    }

    /// <summary>
    /// End-to-end: real session creation with the v2 engine authoritative (canary cohort at 100%)
    /// resolves to a plan that converts into a coherent legacy <see cref="StreamInfo"/> - the source,
    /// streams, and codecs v2 selected are preserved unchanged all the way through.
    /// </summary>
    [Fact]
    public void CreateSessionWithCanaryAuthoritative_ResolveById_ConvertsToCoherentStreamInfo()
    {
        var options = CreateOptions();
        var legacyPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);
        var v2Store = new InMemoryV2PlanStore();

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(legacyPlan);

        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<DomainClientCapabilities>(), It.IsAny<IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Returns(BuildViableDirectPlayDecision());

        var shadowPlanner = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Mode = PlaybackEngineMode.Canary, CanaryPercentage = 100 },
            metrics: null,
            diagnosticsStore: null,
            v2PlanStore: v2Store);
        var manager = new PlaybackSessionManager(shadowPlanner, new Mock<ITranscodeManager>().Object, new Mock<ISessionManager>().Object, diagnosticsStore: null, v2PlanStore: v2Store);
        var resolver = new PlaybackExecutionPlanResolver(v2Store);

        var session = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, options));
        Assert.NotNull(session);

        var plan = resolver.Resolve(session!.Id);
        Assert.NotNull(plan);
        Assert.Equal("source-1", plan!.SourceId);

        var mediaSource = new MediaSourceInfo { Id = "source-1", Container = "mkv" };
        var deviceProfile = new DeviceProfile();
        var executionContext = new PlaybackExecutionContext(Guid.NewGuid(), null, null, null, 0, false);
        var streamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(plan, executionContext, mediaSource, deviceProfile);

        Assert.Equal(PlayMethod.DirectPlay, streamInfo.PlayMethod);
        Assert.Equal("mp4", streamInfo.Container);
        Assert.Equal("source-1", streamInfo.MediaSourceId);
        Assert.Same(mediaSource, streamInfo.MediaSource);
        Assert.Equal(1, streamInfo.AudioStreamIndex);
        Assert.Equal(["h264"], streamInfo.VideoCodecs);
        Assert.Equal(["aac"], streamInfo.AudioCodecs);
    }

    private static PlaybackDecision BuildViableDirectPlayDecision() => PlaybackDecision.DirectPlay(
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
}
