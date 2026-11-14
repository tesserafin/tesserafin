using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Extensions.Json;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Session;
using Tesserafin.Playback.Contract.Diagnostics;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Engine;
using Tesserafin.Playback.Shadow;
using Xunit;

namespace Tesserafin.Playback.Shadow.Tests.Runtime;

/// <summary>
/// Shadow-safety tests for <see cref="ShadowPlaybackSessionPlanner"/> (PR98): the v2 engine runs
/// only in shadow, so nothing it does - including throwing - may affect the plan returned to
/// callers. Legacy stays the source of truth.
/// </summary>
public class ShadowPlaybackSessionPlannerTests
{
    [Fact]
    public void PlanVideo_ShadowEngineThrows_ReturnsInnerPlanUnchangedAndDoesNotThrow()
    {
        var options = CreateOptions();
        var expectedPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(expectedPlan);

        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<System.Collections.Generic.IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Throws<InvalidOperationException>();

        var decorator = new ShadowPlaybackSessionPlanner(mockInner.Object, mockEngine.Object, NullLogger<ShadowPlaybackSessionPlanner>.Instance);

        var result = decorator.PlanVideo(options);

        Assert.Same(expectedPlan, result);
    }

    [Fact]
    public void PlanAudio_ShadowEngineThrows_ReturnsInnerPlanUnchangedAndDoesNotThrow()
    {
        var options = CreateOptions();
        var expectedPlan = new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanAudio(options)).Returns(expectedPlan);

        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<System.Collections.Generic.IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Throws<InvalidOperationException>();

        var decorator = new ShadowPlaybackSessionPlanner(mockInner.Object, mockEngine.Object, NullLogger<ShadowPlaybackSessionPlanner>.Instance);

        var result = decorator.PlanAudio(options);

        Assert.Same(expectedPlan, result);
    }

    [Fact]
    public void PlanVideo_InnerReturnsNull_ReturnsNullAndDoesNotThrow()
    {
        // The "no viable plan" case: options with empty MediaSources is exactly the shape that
        // previously produced a v2 NRE (per PR98 spec) - the shadow path must contain it.
        var options = CreateOptions();

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns((PlaybackPlan?)null);

        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<System.Collections.Generic.IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Throws<NullReferenceException>();

        var decorator = new ShadowPlaybackSessionPlanner(mockInner.Object, mockEngine.Object, NullLogger<ShadowPlaybackSessionPlanner>.Instance);

        var result = decorator.PlanVideo(options);

        Assert.Null(result);
    }

    [Fact]
    public void PlanVideo_ShadowEngineSucceeds_StillReturnsInnerPlanUnchangedByReference()
    {
        // Even when the shadow run succeeds cleanly, its decision must never be substituted for the
        // legacy plan: legacy is the source of truth, unconditionally.
        var options = CreateOptions();
        var expectedPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(expectedPlan);

        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<System.Collections.Generic.IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Returns(PlaybackDecision.DirectPlay(
                "source-1",
                SelectedStreams.None,
                OutputSpec.Empty,
                ReasonNode.Leaf(ReasonCode.MethodChosen, ReasonOutcome.Chosen, ReasonSubject.Method()),
                engineVersion: PlaybackEngine.EngineVersion));

        var decorator = new ShadowPlaybackSessionPlanner(mockInner.Object, mockEngine.Object, NullLogger<ShadowPlaybackSessionPlanner>.Instance);

        var result = decorator.PlanVideo(options);

        Assert.Same(expectedPlan, result);
    }

    [Fact]
    public void PlanVideo_OptionsCarryRealUserId_PublishedContextUsesIt()
    {
        // PR113b: options.UserId (populated by the calling controller from the real requester) must
        // reach the shadow run's PlaybackRequestContext.UserId - previously always Guid.Empty here
        // regardless of who actually made the request.
        var userId = Guid.NewGuid();
        var options = CreateOptions();
        options.UserId = userId;
        var expectedPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(expectedPlan);

        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<System.Collections.Generic.IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Returns(PlaybackDecision.DirectPlay(
                "source-1",
                SelectedStreams.None,
                OutputSpec.Empty,
                ReasonNode.Leaf(ReasonCode.MethodChosen, ReasonOutcome.Chosen, ReasonSubject.Method()),
                engineVersion: PlaybackEngine.EngineVersion));

        ShadowDiagnosticRecord? published = null;
        var mockDiagnosticsStore = new Mock<IShadowDiagnosticsStore>();
        mockDiagnosticsStore
            .Setup(s => s.Publish(It.IsAny<ShadowDiagnosticRecord>()))
            .Callback<ShadowDiagnosticRecord>(record => published = record);

        var decorator = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Enabled = true, SampleRate = 1.0 },
            diagnosticsStore: mockDiagnosticsStore.Object);

        decorator.PlanVideo(options);

        Assert.NotNull(published);
        Assert.Equal(userId, published!.Context.UserId);
    }

    [Fact]
    public void PlanVideo_OptionsUserIdNotSet_PublishedContextUserIdIsEmpty()
    {
        // Backward compatibility: a caller that never sets MediaOptions.UserId (every pre-PR113b
        // call site, and most test fixtures) must keep getting Guid.Empty, exactly as before.
        var options = CreateOptions();
        var expectedPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(expectedPlan);

        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<System.Collections.Generic.IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Returns(PlaybackDecision.DirectPlay(
                "source-1",
                SelectedStreams.None,
                OutputSpec.Empty,
                ReasonNode.Leaf(ReasonCode.MethodChosen, ReasonOutcome.Chosen, ReasonSubject.Method()),
                engineVersion: PlaybackEngine.EngineVersion));

        ShadowDiagnosticRecord? published = null;
        var mockDiagnosticsStore = new Mock<IShadowDiagnosticsStore>();
        mockDiagnosticsStore
            .Setup(s => s.Publish(It.IsAny<ShadowDiagnosticRecord>()))
            .Callback<ShadowDiagnosticRecord>(record => published = record);

        var decorator = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Enabled = true, SampleRate = 1.0 },
            diagnosticsStore: mockDiagnosticsStore.Object);

        decorator.PlanVideo(options);

        Assert.NotNull(published);
        Assert.Equal(Guid.Empty, published!.Context.UserId);
    }

    // --- PR100: options gating, sampling, timing budget, and metrics aggregation ---

    [Fact]
    public void PlanVideo_ShadowDisabled_DoesNotInvokeEngineAndReturnsInnerPlanUnchanged()
    {
        var options = CreateOptions();
        var expectedPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(expectedPlan);

        var mockEngine = new Mock<IPlaybackEngine>();

        var decorator = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Enabled = false, SampleRate = 1.0 });

        var result = decorator.PlanVideo(options);

        Assert.Same(expectedPlan, result);
        mockEngine.Verify(
            e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<System.Collections.Generic.IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()),
            Times.Never);
        Assert.Equal(0, decorator.Metrics.GetSnapshot().TotalExecutions);
    }

    [Fact]
    public void PlanVideo_SampleRateZero_DoesNotInvokeEngine()
    {
        var options = CreateOptions();
        var expectedPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(expectedPlan);

        var mockEngine = new Mock<IPlaybackEngine>();

        var decorator = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Enabled = true, SampleRate = 0.0 });

        var result = decorator.PlanVideo(options);

        Assert.Same(expectedPlan, result);
        mockEngine.Verify(
            e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<System.Collections.Generic.IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()),
            Times.Never);
        Assert.Equal(0, decorator.Metrics.GetSnapshot().TotalExecutions);
    }

    [Fact]
    public void PlanVideo_EnabledSampleRateOne_RunsShadowAndIncrementsMetrics()
    {
        var options = CreateOptions();
        var expectedPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(expectedPlan);

        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<System.Collections.Generic.IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Returns(PlaybackDecision.DirectPlay(
                "source-1",
                SelectedStreams.None,
                OutputSpec.Empty,
                ReasonNode.Leaf(ReasonCode.MethodChosen, ReasonOutcome.Chosen, ReasonSubject.Method()),
                engineVersion: PlaybackEngine.EngineVersion));

        var decorator = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Enabled = true, SampleRate = 1.0 });

        var result = decorator.PlanVideo(options);

        Assert.Same(expectedPlan, result);
        mockEngine.Verify(
            e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<System.Collections.Generic.IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()),
            Times.Once);
        Assert.Equal(1, decorator.Metrics.GetSnapshot().TotalExecutions);
    }

    [Fact]
    public void PlanVideo_EngineThrows_CountsExceptionAndLeavesResultIntact()
    {
        var options = CreateOptions();
        var expectedPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(expectedPlan);

        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<System.Collections.Generic.IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Throws<InvalidOperationException>();

        var decorator = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Enabled = true, SampleRate = 1.0 });

        var result = decorator.PlanVideo(options);

        Assert.Same(expectedPlan, result);
        var snapshot = decorator.Metrics.GetSnapshot();
        Assert.Equal(1, snapshot.TotalExecutions);
        Assert.Equal(1, snapshot.ExceptionCount);
    }

    [Fact]
    public void PlanVideo_ExceedsBudget_CountsBudgetExceeded()
    {
        var options = CreateOptions();
        var expectedPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(expectedPlan);

        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<System.Collections.Generic.IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Returns(() =>
            {
                // Force the measured shadow duration past a near-zero budget without relying on
                // real wall-clock sleeps in the fast path elsewhere in the file.
                System.Threading.Thread.Sleep(5);
                return PlaybackDecision.DirectPlay(
                    "source-1",
                    SelectedStreams.None,
                    OutputSpec.Empty,
                    ReasonNode.Leaf(ReasonCode.MethodChosen, ReasonOutcome.Chosen, ReasonSubject.Method()),
                    engineVersion: PlaybackEngine.EngineVersion);
            });

        var decorator = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Enabled = true, SampleRate = 1.0, MaxExecutionMs = 0 });

        decorator.PlanVideo(options);

        var snapshot = decorator.Metrics.GetSnapshot();
        Assert.Equal(1, snapshot.BudgetExceededCount);
    }

    [Fact]
    public void PlanVideo_AfterSummaryIntervalExecutions_EmitsSinglePeriodicSummaryLog()
    {
        var options = CreateOptions();
        var expectedPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(expectedPlan);

        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<System.Collections.Generic.IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Returns(PlaybackDecision.DirectPlay(
                "source-1",
                SelectedStreams.None,
                OutputSpec.Empty,
                ReasonNode.Leaf(ReasonCode.MethodChosen, ReasonOutcome.Chosen, ReasonSubject.Method()),
                engineVersion: PlaybackEngine.EngineVersion));

        var mockLogger = new Mock<ILogger<ShadowPlaybackSessionPlanner>>();

        var decorator = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            mockLogger.Object,
            () => new PlaybackShadowOptions { Enabled = true, SampleRate = 1.0 });

        for (var i = 0; i < ShadowMetrics.SummaryIntervalExecutions; i++)
        {
            decorator.PlanVideo(options);
        }

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("Shadow v2 playback metrics summary", StringComparison.Ordinal)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    // --- PR111e: pre-legacy snapshot ordering ---

    [Fact]
    public void PlanVideo_ShadowInputMappingThrows_LegacyStillRunsExactlyOnceAndResultIntact()
    {
        // A null entry in MediaSources forces DlnaPlaybackAdapter.ToSnapshot to throw during the
        // PRE-legacy capture phase (PrepareShadow) - proving a mapping failure can never prevent
        // legacy from running (exactly once) or leak into the returned plan; it is caught, logged,
        // and counted exactly like a post-legacy shadow exception used to be.
        var options = CreateOptions();
        options.MediaSources = [null!];
        var expectedPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(expectedPlan);

        var mockEngine = new Mock<IPlaybackEngine>();

        var decorator = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Enabled = true, SampleRate = 1.0 });

        var result = decorator.PlanVideo(options);

        Assert.Same(expectedPlan, result);
        mockInner.Verify(p => p.PlanVideo(options), Times.Once);
        mockEngine.Verify(
            e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()),
            Times.Never);

        var snapshot = decorator.Metrics.GetSnapshot();
        Assert.Equal(1, snapshot.TotalExecutions);
        Assert.Equal(1, snapshot.ExceptionCount);
    }

    [Fact]
    public void PlanVideo_InnerMutatesContainerDuringPlan_ShadowSnapshotKeepsOriginalValue()
    {
        // Reproduces legacy StreamBuilder's real side effect: it mutates the shared
        // MediaSourceInfo.Container in place while planning (normalizing a raw ffprobe multi-value
        // CSV down to a single value). The fake inner planner below does the same thing, from
        // INSIDE its PlanVideo call, so the assertion genuinely exercises "captured before the call,
        // not merely before the mutation happens to run" - PR111e's whole point.
        var source = new MediaSourceInfo { Id = "src-1", Container = "mov,mp4,m4a,3gp,3g2,mj2" };
        var options = CreateOptions();
        options.MediaSources = [source];
        var expectedPlan = new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner
            .Setup(p => p.PlanVideo(options))
            .Callback(() => source.Container = "mov")
            .Returns(expectedPlan);

        IReadOnlyList<MediaSourceSnapshot>? capturedSources = null;
        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Callback<PlaybackRequestContext, Tesserafin.Playback.Decision.ClientCapabilities, IReadOnlyList<MediaSourceSnapshot>, PlaybackConstraints>((_, _, sources, _) => capturedSources = sources)
            .Returns(PlaybackDecision.DirectPlay(
                "source-1",
                SelectedStreams.None,
                OutputSpec.Empty,
                ReasonNode.Leaf(ReasonCode.MethodChosen, ReasonOutcome.Chosen, ReasonSubject.Method()),
                engineVersion: PlaybackEngine.EngineVersion));

        var decorator = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Enabled = true, SampleRate = 1.0 });

        decorator.PlanVideo(options);

        // The mutation genuinely happened, on the exact shared object - this is not a no-op fake.
        Assert.Equal("mov", source.Container);

        // But the engine was given the value captured BEFORE that mutation ran.
        Assert.NotNull(capturedSources);
        Assert.Equal("mov,mp4,m4a,3gp,3g2,mj2", capturedSources![0].Container);
    }

    [Fact]
    public void PlanVideo_ShadowDisabled_PathologicalOptionsProveNoMappingWasAttempted()
    {
        // MediaSources containing null would throw if DlnaPlaybackAdapter.ToSnapshot ever ran over it
        // - even inside PrepareShadow's own try/catch, that would still count as an exception. Shadow
        // mode disabled must short-circuit before any mapping is attempted at all: zero exceptions
        // recorded is the proof, not just "the engine was never invoked" (which the existing
        // PlanVideo_ShadowDisabled_DoesNotInvokeEngineAndReturnsInnerPlanUnchanged already covers).
        var options = CreateOptions();
        options.MediaSources = [null!];
        var expectedPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(expectedPlan);

        var mockEngine = new Mock<IPlaybackEngine>();

        var decorator = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Enabled = false, SampleRate = 1.0 });

        var result = decorator.PlanVideo(options);

        Assert.Same(expectedPlan, result);
        var snapshot = decorator.Metrics.GetSnapshot();
        Assert.Equal(0, snapshot.TotalExecutions);
        Assert.Equal(0, snapshot.ExceptionCount);
    }

    [Fact]
    public void PlanVideo_SampleRateZero_PathologicalOptionsProveNoMappingWasAttempted()
    {
        // Same proof as PlanVideo_ShadowDisabled_PathologicalOptionsProveNoMappingWasAttempted, for
        // the "sampled out" (rather than "disabled") skip path.
        var options = CreateOptions();
        options.MediaSources = [null!];
        var expectedPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(expectedPlan);

        var mockEngine = new Mock<IPlaybackEngine>();

        var decorator = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Enabled = true, SampleRate = 0.0 });

        var result = decorator.PlanVideo(options);

        Assert.Same(expectedPlan, result);
        var snapshot = decorator.Metrics.GetSnapshot();
        Assert.Equal(0, snapshot.TotalExecutions);
        Assert.Equal(0, snapshot.ExceptionCount);
    }

    [Fact]
    public async Task PlanVideo_RealContainerCsvFixture_V2ReceivesUnmutatedContainerAndComparisonDoesNotDiverge()
    {
        // PR111e regression coverage for the real (Chrome, mp4-h264-ac3-aac-srt-2600k) oracle case
        // (see OracleCaseFixtures.ApprovedDivergences' history): this source's real ffprobe container
        // is "mov,mp4,m4a,3gp,3g2,mj2" - legacy's StreamBuilder mutates it down to a single value as a
        // side effect of planning. Uses the REAL legacy PlaybackSessionPlanner (a thin delegate to
        // StreamBuilder) and the REAL v2 PlaybackEngine, not mocks, so both halves of the PR111e fix -
        // pre-legacy capture ordering (this class) and CSV-aware container comparison (PlaybackEngine) -
        // are exercised together exactly as production wires them.
        var options = await GetMediaOptions("Chrome", "mp4-h264-ac3-aac-srt-2600k");
        var originalContainer = options.MediaSources[0].Container;
        Assert.Equal("mov,mp4,m4a,3gp,3g2,mj2", originalContainer);

        var innerPlanner = new PlaybackSessionPlanner(new Mock<IMediaEncoder>().Object, NullLogger<PlaybackSessionPlanner>.Instance);
        var engine = new PlaybackEngine();

        ShadowDiagnosticRecord? published = null;
        var mockDiagnosticsStore = new Mock<IShadowDiagnosticsStore>();
        mockDiagnosticsStore
            .Setup(s => s.Publish(It.IsAny<ShadowDiagnosticRecord>()))
            .Callback<ShadowDiagnosticRecord>(record => published = record);

        var decorator = new ShadowPlaybackSessionPlanner(
            innerPlanner,
            engine,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Enabled = true, SampleRate = 1.0 },
            diagnosticsStore: mockDiagnosticsStore.Object);

        var plan = decorator.PlanVideo(options);

        Assert.NotNull(plan);
        Assert.Equal(PlayMethod.Transcode, plan.PlayMethod);

        // Legacy really did mutate the shared MediaSourceInfo, same as production - this is not a
        // fixture that happens to dodge the bug.
        Assert.NotEqual(originalContainer, options.MediaSources[0].Container);

        // But v2's shadow snapshot - captured before legacy ran - kept the original raw ffprobe CSV.
        Assert.NotNull(published);
        Assert.Equal(originalContainer, published!.Sources[0].Container);

        // And the (PR111e-fixed) container-CSV bug no longer causes a spurious divergence: this case
        // used to be an approved PotentialRegression in OracleCaseFixtures.ApprovedDivergences; that
        // entry is now gone because it classifies Equivalent.
        Assert.NotEqual(DivergenceClass.PotentialRegression, published.Divergence.Class);
        Assert.NotEqual(DivergenceClass.Unexplained, published.Divergence.Class);
    }

    // --- PR115a: canary/v2 authority gating ---

    [Fact]
    public void PlanVideo_ModeCanaryFullCohort_PublishesV2PlanRecordAndDiagnosticsStillPublish()
    {
        // CanaryPercentage=100 enrolls every user/device pair (CanaryCohort.IsInCohort short-circuits
        // true at >=100), so this call is authoritative regardless of the default Guid.Empty/null
        // user/device on CreateOptions()'s MediaOptions.
        var options = CreateOptions();
        var expectedPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(expectedPlan);

        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Returns(BuildViableDirectPlayDecision());

        ShadowDiagnosticRecord? publishedDiagnostics = null;
        var mockDiagnosticsStore = new Mock<IShadowDiagnosticsStore>();
        mockDiagnosticsStore
            .Setup(s => s.Publish(It.IsAny<ShadowDiagnosticRecord>()))
            .Callback<ShadowDiagnosticRecord>(record => publishedDiagnostics = record);

        var v2Store = new InMemoryV2PlanStore();
        var decorator = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Mode = PlaybackEngineMode.Canary, CanaryPercentage = 100, SampleRate = 1.0 },
            metrics: null,
            diagnosticsStore: mockDiagnosticsStore.Object,
            v2PlanStore: v2Store);

        V2PlanRecord? publishedV2;
        using (v2Store.BeginCapture())
        {
            var result = decorator.PlanVideo(options);
            Assert.Same(expectedPlan, result);
            publishedV2 = v2Store.TakeCaptured();
        }

        Assert.NotNull(publishedV2);
        Assert.NotNull(publishedV2!.ExecutionPlan);
        Assert.True(publishedV2.Decision.IsViable);

        // The shadow observability run happens unconditionally alongside the authoritative publish.
        Assert.NotNull(publishedDiagnostics);
    }

    [Fact]
    public void PlanVideo_ModeCanaryEmptyCohort_PublishesNoV2PlanRecordButDiagnosticsStillRun()
    {
        // CanaryPercentage=0 enrolls nobody (CanaryCohort.IsInCohort short-circuits false at <=0), so
        // this call is never authoritative - but SampleRate=1.0 still drives the pure-observability
        // shadow run for it.
        var options = CreateOptions();
        var expectedPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(expectedPlan);

        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Returns(BuildViableDirectPlayDecision());

        ShadowDiagnosticRecord? publishedDiagnostics = null;
        var mockDiagnosticsStore = new Mock<IShadowDiagnosticsStore>();
        mockDiagnosticsStore
            .Setup(s => s.Publish(It.IsAny<ShadowDiagnosticRecord>()))
            .Callback<ShadowDiagnosticRecord>(record => publishedDiagnostics = record);

        var v2Store = new InMemoryV2PlanStore();
        var decorator = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Mode = PlaybackEngineMode.Canary, CanaryPercentage = 0, SampleRate = 1.0 },
            metrics: null,
            diagnosticsStore: mockDiagnosticsStore.Object,
            v2PlanStore: v2Store);

        V2PlanRecord? publishedV2;
        using (v2Store.BeginCapture())
        {
            decorator.PlanVideo(options);
            publishedV2 = v2Store.TakeCaptured();
        }

        Assert.Null(publishedV2);
        Assert.NotNull(publishedDiagnostics);
    }

    [Fact]
    public void PlanVideo_ModeShadow_NeverPublishesV2PlanRecord()
    {
        var options = CreateOptions();
        var expectedPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(expectedPlan);

        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Returns(BuildViableDirectPlayDecision());

        var v2Store = new InMemoryV2PlanStore();
        var decorator = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Mode = PlaybackEngineMode.Shadow, SampleRate = 1.0 },
            metrics: null,
            diagnosticsStore: null,
            v2PlanStore: v2Store);

        V2PlanRecord? publishedV2;
        using (v2Store.BeginCapture())
        {
            var result = decorator.PlanVideo(options);
            Assert.Same(expectedPlan, result);
            publishedV2 = v2Store.TakeCaptured();
        }

        Assert.Null(publishedV2);
        mockEngine.Verify(
            e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()),
            Times.Once);
    }

    [Fact]
    public void PlanVideo_ModeLegacyDefaultWithEnabledTrue_EffectiveModeIsShadow_RunsEngineButNeverPublishesV2Record()
    {
        // PlaybackShadowOptions.Mode left at its default (Legacy) combined with the pre-PR115a
        // Enabled=true flag: GetEffectiveMode resolves this to Shadow, so the engine still runs for
        // observability, but it is never authoritative.
        var options = CreateOptions();
        var expectedPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(expectedPlan);

        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Returns(BuildViableDirectPlayDecision());

        var v2Store = new InMemoryV2PlanStore();
        var decorator = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Enabled = true, SampleRate = 1.0 },
            metrics: null,
            diagnosticsStore: null,
            v2PlanStore: v2Store);

        V2PlanRecord? publishedV2;
        using (v2Store.BeginCapture())
        {
            var result = decorator.PlanVideo(options);
            Assert.Same(expectedPlan, result);
            publishedV2 = v2Store.TakeCaptured();
        }

        Assert.Null(publishedV2);
        mockEngine.Verify(
            e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()),
            Times.Once);
    }

    [Fact]
    public void PlanVideo_ModeV2WithSampleRateZero_StillPublishesV2PlanRecord()
    {
        // Sampling only ever gates pure-observability runs: an authoritative run (full v2 mode here)
        // must happen every time regardless of SampleRate, or a session would randomly flip engines.
        var options = CreateOptions();
        var expectedPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(expectedPlan);

        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Returns(BuildViableDirectPlayDecision());

        var v2Store = new InMemoryV2PlanStore();
        var decorator = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Mode = PlaybackEngineMode.V2, SampleRate = 0.0 },
            metrics: null,
            diagnosticsStore: null,
            v2PlanStore: v2Store);

        V2PlanRecord? publishedV2;
        using (v2Store.BeginCapture())
        {
            var result = decorator.PlanVideo(options);
            Assert.Same(expectedPlan, result);
            publishedV2 = v2Store.TakeCaptured();
        }

        Assert.NotNull(publishedV2);
        Assert.NotNull(publishedV2!.ExecutionPlan);
        mockEngine.Verify(
            e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()),
            Times.Once);
    }

    [Fact]
    public void PlanVideo_ModeCanaryNotViableDecision_PublishesV2PlanRecordWithNullExecutionPlan()
    {
        // v2 was authoritative for this session but the engine's decision was not executable: the
        // record is still published (its presence IS the statement of authority) but with a null
        // ExecutionPlan, so PlaybackExecutionPlanResolver.Resolve falls back to legacy for it.
        var options = CreateOptions();
        var expectedPlan = new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(expectedPlan);

        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Returns(BuildNotViableDecision());

        var v2Store = new InMemoryV2PlanStore();
        var decorator = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Mode = PlaybackEngineMode.Canary, CanaryPercentage = 100, SampleRate = 1.0 },
            metrics: null,
            diagnosticsStore: null,
            v2PlanStore: v2Store);

        V2PlanRecord? publishedV2;
        using (v2Store.BeginCapture())
        {
            var result = decorator.PlanVideo(options);
            Assert.Same(expectedPlan, result);
            publishedV2 = v2Store.TakeCaptured();
        }

        Assert.NotNull(publishedV2);
        Assert.Null(publishedV2!.ExecutionPlan);
        Assert.False(publishedV2.Decision.IsViable);
    }

    [Fact]
    public void PlanVideo_ModeCanaryEngineThrows_PublishesNoV2PlanRecordAndReturnsLegacyPlanUnchanged()
    {
        // The engine throwing must never leave a canary session's execution authority in a partial
        // state: RunShadow's Publish call is strictly after Decide returns successfully, so a throw
        // there means nothing is ever published for this call.
        var options = CreateOptions();
        var expectedPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(expectedPlan);

        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Throws<InvalidOperationException>();

        var v2Store = new InMemoryV2PlanStore();
        var decorator = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Mode = PlaybackEngineMode.Canary, CanaryPercentage = 100, SampleRate = 1.0 },
            metrics: null,
            diagnosticsStore: null,
            v2PlanStore: v2Store);

        V2PlanRecord? publishedV2;
        using (v2Store.BeginCapture())
        {
            var result = decorator.PlanVideo(options);
            Assert.Same(expectedPlan, result);
            publishedV2 = v2Store.TakeCaptured();
        }

        Assert.Null(publishedV2);
    }

    // Issue #75 slice 75b: the end-to-end seam. A structural scan captured into the ambient scope
    // (as the Tesserafin.Api filter does) must reach the retained diagnostic through a real plan run.
    [Fact]
    public void PlanVideo_CapturedStructuralScan_ReachesRetainedDiagnostic_AndEvictsWithSession()
    {
        var options = CreateOptions();
        var expectedPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(expectedPlan);

        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Returns(BuildViableDirectPlayDecision());

        var store = new InMemoryShadowDiagnosticsStore();
        var decorator = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Enabled = true, SampleRate = 1.0 },
            metrics: null,
            diagnosticsStore: store);

        var scan = SampleScan();

        ShadowDiagnosticRecord? published;
        using (store.BeginCapture(new ShadowCaptureInputs(EmptyDeclaredCapabilities(), 1234, scan)))
        {
            var result = decorator.PlanVideo(options);
            Assert.Same(expectedPlan, result);
            published = store.TakeCaptured();
        }

        // The scan captured into the scope reached the retained diagnostic unchanged.
        Assert.NotNull(published);
        Assert.NotNull(published!.ContractMapping);
        Assert.Same(scan, published.ContractMapping!.StructuralScan);
        Assert.Equal(1234, published.ContractMapping.PayloadSizeBytes);

        // TTL / eviction: the scan data lives on the session record and is evicted with the session,
        // via the EXISTING store lifecycle - no new TTL was introduced.
        var id = PlaybackSessionId.NewId();
        store.Attach(id, published);
        Assert.True(store.TryGet(id, out var retained));
        Assert.NotNull(retained!.ContractMapping!.StructuralScan);
        store.Remove(id);
        Assert.False(store.TryGet(id, out _));
    }

    [Fact]
    public void PlanVideo_NoCapturedScan_LeavesStructuralScanNull()
    {
        // Anti-vacuity: a run with no captured scan yields a diagnostic whose StructuralScan is null,
        // so a scan that did not run is always distinguishable from one that ran and found nothing.
        var options = CreateOptions();
        var expectedPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(expectedPlan);

        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Tesserafin.Playback.Decision.ClientCapabilities>(), It.IsAny<IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Returns(BuildViableDirectPlayDecision());

        var store = new InMemoryShadowDiagnosticsStore();
        var decorator = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Enabled = true, SampleRate = 1.0 },
            metrics: null,
            diagnosticsStore: store);

        ShadowDiagnosticRecord? published;
        using (store.BeginCapture(new ShadowCaptureInputs(EmptyDeclaredCapabilities(), null)))
        {
            decorator.PlanVideo(options);
            published = store.TakeCaptured();
        }

        Assert.NotNull(published);
        Assert.NotNull(published!.ContractMapping);
        Assert.Null(published.ContractMapping!.StructuralScan);
    }

    private static ContractStructuralScan SampleScan() => new(
        UnknownMemberTotal: 2,
        UnknownMembers: new[]
        {
            new ContractUnknownMemberCount(ContractPath.Request, 1),
            new ContractUnknownMemberCount(ContractPath.Decode, 1),
        },
        WrongTypes: Array.Empty<ContractFieldIssue>(),
        ScannedBodyByteCount: 4096,
        BodyLimitExceeded: false);

    private static Tesserafin.Playback.Decision.ClientCapabilities EmptyDeclaredCapabilities()
    {
        var decode = new DecodeCapabilities(
            Array.Empty<DecodeProfile>(),
            Array.Empty<VideoCodecCapability>(),
            Array.Empty<AudioCodecCapability>(),
            Array.Empty<SubtitleCapability>(),
            SupportsHls: false,
            SupportsDash: false);

        return new Tesserafin.Playback.Decision.ClientCapabilities(decode, Array.Empty<PlaybackOutputProfile>());
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

    private static PlaybackDecision BuildNotViableDecision() => PlaybackDecision.NotViable(
        PlaybackMethod.Transcode,
        new ReasonNode(ReasonCode.NoViablePlan, ReasonOutcome.Rejected, ReasonSubject.Method(), null, []),
        engineVersion: PlaybackEngine.EngineVersion);

    private static MediaOptions CreateOptions() => new() { Profile = new DeviceProfile() };

    /// <summary>
    /// Loads real fixture data shared with the encoding planner tests - same helper,
    /// same "Test Data" JSON files, so the CSV-container regression test above exercises a genuine
    /// production fixture rather than a hand-built one.
    /// </summary>
    private static async ValueTask<MediaOptions> GetMediaOptions(string deviceProfile, params string[] sources)
    {
        var mediaSources = sources.Select(src => TestData<MediaSourceInfo>(src))
            .Select(val => val.Result)
            .ToArray();
        var mediaSourceId = mediaSources[0]?.Id;

        var dp = await TestData<DeviceProfile>(deviceProfile);

        return new MediaOptions()
        {
            ItemId = new Guid("11D229B7-2D48-4B95-9F9B-49F6AB75E613"),
            MediaSourceId = mediaSourceId,
            MediaSources = mediaSources,
            DeviceId = "test-deviceId",
            Profile = dp,
            AllowAudioStreamCopy = true,
            AllowVideoStreamCopy = true,
            EnableDirectStream = false,
        };
    }

    private static async ValueTask<T> TestData<T>(string name)
    {
        var path = Path.Join("Test Data", typeof(T).Name + "-" + name + ".json");

        using var stream = File.OpenRead(path);

        var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonDefaults.Options);
        if (value is not null)
        {
            return value;
        }

        throw new SerializationException("Invalid test data: " + name);
    }
}
