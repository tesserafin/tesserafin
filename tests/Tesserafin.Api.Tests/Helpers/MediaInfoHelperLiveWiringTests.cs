using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Moq;
using Tesserafin.Api.Constants;
using Tesserafin.Api.Helpers;
using Tesserafin.Common.Net;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Devices;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Data;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.MediaInfo;
using Tesserafin.Model.Session;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Execution;
using Xunit;

namespace Tesserafin.Api.Tests.Helpers;

/// <summary>
/// PR115c: exercises <see cref="MediaInfoHelper.SetDeviceSpecificData"/> - the live streaming path -
/// end to end (real constructor, mocked dependencies) for the canary-authoritative
/// serve-v2-or-legacy-fallback decision. Distinguishes "served by v2" from "served by legacy" via
/// <see cref="MediaSourceInfo.DefaultAudioStreamIndex"/> (set from whichever <c>StreamInfo</c> was
/// actually chosen, at the very end of <see cref="MediaInfoHelper.SetDeviceSpecificData"/>) rather
/// than reflection, and asserts the retained <see cref="PlaybackLiveWiringOutcome"/> for every case -
/// every v2-served or fallback decision must be observable, per PR115c's scope.
/// </summary>
public class MediaInfoHelperLiveWiringTests
{
    private const string SourceId = "source-1";
    private const int LegacyAudioStreamIndex = 5;
    private const int V2AudioStreamIndex = 1;

    [Theory]
    [InlineData(PlaybackMethod.DirectPlay)]
    [InlineData(PlaybackMethod.Remux)]
    public void SetDeviceSpecificData_CanaryAuthoritativeDirectPlayOrRemux_ServesFromV2Plan(PlaybackMethod method)
    {
        var mediaSource = BuildMediaSource();
        var legacyStreamInfo = BuildLegacyStreamInfo(mediaSource, PlayMethod.DirectPlay);
        var plan = BuildPlan(method, transforms: []);

        var (helper, store, sessionId) = CreateFixture(legacyStreamInfo, PlaybackExecutionPlanResolution.Resolved, plan);

        Invoke(helper, mediaSource);

        Assert.Equal(V2AudioStreamIndex, mediaSource.DefaultAudioStreamIndex);
        AssertServed(store, sessionId);
    }

    [Fact]
    public void SetDeviceSpecificData_CanaryAuthoritativeVideoTranscode_ServesFromV2Plan()
    {
        var mediaSource = BuildMediaSource();
        var legacyStreamInfo = BuildLegacyStreamInfo(mediaSource, PlayMethod.Transcode);
        var plan = BuildPlan(PlaybackMethod.Transcode, transforms: [TransformKind.TranscodeVideo]);

        var (helper, store, sessionId) = CreateFixture(legacyStreamInfo, PlaybackExecutionPlanResolution.Resolved, plan);

        Invoke(helper, mediaSource);

        Assert.Equal(V2AudioStreamIndex, mediaSource.DefaultAudioStreamIndex);
        AssertServed(store, sessionId);
    }

    [Fact]
    public void SetDeviceSpecificData_CanaryAuthoritativeAudioTranscode_ServesFromV2Plan()
    {
        var mediaSource = BuildMediaSource();
        var legacyStreamInfo = BuildLegacyStreamInfo(mediaSource, PlayMethod.Transcode);
        var plan = BuildPlan(PlaybackMethod.Transcode, transforms: [TransformKind.TranscodeAudio]);

        var (helper, store, sessionId) = CreateFixture(legacyStreamInfo, PlaybackExecutionPlanResolution.Resolved, plan);

        Invoke(helper, mediaSource);

        Assert.Equal(V2AudioStreamIndex, mediaSource.DefaultAudioStreamIndex);
        AssertServed(store, sessionId);
    }

    [Theory]
    [InlineData(Tesserafin.Playback.Decision.SubtitleDeliveryMethod.Burn)]
    [InlineData(Tesserafin.Playback.Decision.SubtitleDeliveryMethod.Hls)]
    public void SetDeviceSpecificData_CanaryAuthoritativeSubtitleTranscode_ServesFromV2Plan(Tesserafin.Playback.Decision.SubtitleDeliveryMethod delivery)
    {
        var mediaSource = BuildMediaSource(withSubtitleStream: true);
        var legacyStreamInfo = BuildLegacyStreamInfo(mediaSource, PlayMethod.Transcode);
        var plan = BuildPlan(PlaybackMethod.Transcode, transforms: [TransformKind.TranscodeVideo], subtitleIndex: 2, subtitleDelivery: delivery);

        var (helper, store, sessionId) = CreateFixture(legacyStreamInfo, PlaybackExecutionPlanResolution.Resolved, plan);

        Invoke(helper, mediaSource);

        Assert.Equal(V2AudioStreamIndex, mediaSource.DefaultAudioStreamIndex);
        AssertServed(store, sessionId);
    }

    [Fact]
    public void SetDeviceSpecificData_NoAuthoritativeRecord_FallsBackToLegacyWithTypedReason()
    {
        var mediaSource = BuildMediaSource();
        var legacyStreamInfo = BuildLegacyStreamInfo(mediaSource, PlayMethod.DirectPlay);

        var (helper, store, sessionId) = CreateFixture(legacyStreamInfo, PlaybackExecutionPlanResolution.NoAuthoritativeRecord, plan: null);

        Invoke(helper, mediaSource);

        Assert.Equal(LegacyAudioStreamIndex, mediaSource.DefaultAudioStreamIndex);
        AssertFallback(store, sessionId, PlaybackLiveFallbackReason.NoAuthoritativeRecord);
    }

    [Fact]
    public void SetDeviceSpecificData_PlanNotExecutable_FallsBackToLegacyWithTypedReason()
    {
        var mediaSource = BuildMediaSource();
        var legacyStreamInfo = BuildLegacyStreamInfo(mediaSource, PlayMethod.DirectPlay);

        var (helper, store, sessionId) = CreateFixture(legacyStreamInfo, PlaybackExecutionPlanResolution.PlanNotExecutable, plan: null);

        Invoke(helper, mediaSource);

        Assert.Equal(LegacyAudioStreamIndex, mediaSource.DefaultAudioStreamIndex);
        AssertFallback(store, sessionId, PlaybackLiveFallbackReason.PlanNotExecutable);
    }

    [Fact]
    public void SetDeviceSpecificData_PlanSourceIdDoesNotMatchServedMediaSource_FallsBackToLegacyWithTypedReason()
    {
        var mediaSource = BuildMediaSource();
        var legacyStreamInfo = BuildLegacyStreamInfo(mediaSource, PlayMethod.DirectPlay);
        var plan = BuildPlan(PlaybackMethod.DirectPlay, transforms: [], sourceId: "some-other-source");

        var (helper, store, sessionId) = CreateFixture(legacyStreamInfo, PlaybackExecutionPlanResolution.Resolved, plan);

        Invoke(helper, mediaSource);

        Assert.Equal(LegacyAudioStreamIndex, mediaSource.DefaultAudioStreamIndex);
        AssertFallback(store, sessionId, PlaybackLiveFallbackReason.SourceIdMismatch);
    }

    [Theory]
    [InlineData(PlaybackEngineMode.Legacy)]
    [InlineData(PlaybackEngineMode.Shadow)]
    public void SetDeviceSpecificData_KillSwitchOff_FallsBackToLegacyEvenWithAResolvablePlan(PlaybackEngineMode mode)
    {
        var mediaSource = BuildMediaSource();
        var legacyStreamInfo = BuildLegacyStreamInfo(mediaSource, PlayMethod.DirectPlay);
        var plan = BuildPlan(PlaybackMethod.DirectPlay, transforms: []);

        // The resolver is set up to return a perfectly valid, matching plan - proving the kill switch
        // overrides an otherwise-servable v2 plan, not just the trivial "no plan" path.
        var (helper, store, sessionId) = CreateFixture(legacyStreamInfo, PlaybackExecutionPlanResolution.Resolved, plan, mode: mode);

        Invoke(helper, mediaSource);

        Assert.Equal(LegacyAudioStreamIndex, mediaSource.DefaultAudioStreamIndex);
        AssertFallback(store, sessionId, PlaybackLiveFallbackReason.KillSwitch);
    }

    [Fact]
    public void SetDeviceSpecificData_DolbyVisionSourceWithCodecInLegacyCandidateCsv_FallsBackToLegacyWithTypedReason()
    {
        // Mirrors the PR115b design doc oracle case (mp4-dvhe.08-eac3-15200k): a Dolby Vision
        // (profile 8, HDR10 base layer) hevc source whose codec also appears in legacy's own
        // candidate VideoCodecs CSV - the class of session EncodingHelper.CanStreamCopyVideo can
        // stream-copy incompatibly, mandatorily excluded from the v2 live path until investigated.
        var mediaSource = BuildMediaSource(dolbyVisionVideoStream: true);
        var legacyStreamInfo = BuildLegacyStreamInfo(mediaSource, PlayMethod.DirectPlay);
        legacyStreamInfo.VideoCodecs = ["hevc", "h264"];
        var plan = BuildPlan(PlaybackMethod.DirectPlay, transforms: [], videoCodec: "hevc");

        var (helper, store, sessionId) = CreateFixture(legacyStreamInfo, PlaybackExecutionPlanResolution.Resolved, plan);

        Invoke(helper, mediaSource);

        Assert.Equal(LegacyAudioStreamIndex, mediaSource.DefaultAudioStreamIndex);
        AssertFallback(store, sessionId, PlaybackLiveFallbackReason.DolbyVisionExclusion);
    }

    [Fact]
    public void SetDeviceSpecificData_DolbyVisionSourceWithCodecNotInLegacyCandidateCsv_IsNotExcluded()
    {
        // Same Dolby Vision source, but legacy's own candidate CSV never offered its codec (legacy
        // would have transcoded away from it too) - the mandatory exclusion is scoped to the exact
        // class of risk the design doc identifies, not every HDR/DV source unconditionally.
        var mediaSource = BuildMediaSource(dolbyVisionVideoStream: true);
        var legacyStreamInfo = BuildLegacyStreamInfo(mediaSource, PlayMethod.Transcode);
        legacyStreamInfo.VideoCodecs = ["av1"];
        var plan = BuildPlan(PlaybackMethod.Transcode, transforms: [TransformKind.TranscodeVideo], videoCodec: "hevc");

        var (helper, store, sessionId) = CreateFixture(legacyStreamInfo, PlaybackExecutionPlanResolution.Resolved, plan);

        Invoke(helper, mediaSource);

        Assert.Equal(V2AudioStreamIndex, mediaSource.DefaultAudioStreamIndex);
        AssertServed(store, sessionId);
    }

    [Fact]
    public void SetDeviceSpecificData_AdapterThrows_FallsBackToLegacyWithTypedReason()
    {
        // An out-of-range PlaybackMethod (impossible via the real v2 engine, but a cheap, reliable
        // way to force PlaybackExecutionPlanAdapter.ToStreamInfo's ToPlayMethod switch to throw
        // ArgumentOutOfRangeException) stands in for "the adapter throws for some reason on an
        // otherwise-eligible plan". The point under test is that no exception escapes
        // SetDeviceSpecificData and legacy is served instead - the same "v2 must never break the live
        // path" discipline ShadowPlaybackSessionPlanner already applies to the shadow run.
        var mediaSource = BuildMediaSource();
        var legacyStreamInfo = BuildLegacyStreamInfo(mediaSource, PlayMethod.DirectPlay);
        var plan = BuildPlan((PlaybackMethod)99, transforms: []);

        var (helper, store, sessionId) = CreateFixture(legacyStreamInfo, PlaybackExecutionPlanResolution.Resolved, plan);

        Invoke(helper, mediaSource);

        Assert.Equal(LegacyAudioStreamIndex, mediaSource.DefaultAudioStreamIndex);
        var found = store.TryGet(sessionId, out var outcome);
        Assert.True(found);
        Assert.NotNull(outcome);
        Assert.False(outcome!.ServedByV2);
    }

    [Fact]
    public void SetDeviceSpecificData_StopThresholdGuardTripped_FallsBackToLegacyWithTypedReason()
    {
        // A guard pre-tripped by a seeded metrics instance (1 v2 attempt, 1 adapter error, threshold
        // 10%, minimum sample size 1) - proving the guard overrides an otherwise-servable v2 plan,
        // the same "checked before resolving a plan" shape as the kill switch test above.
        var mediaSource = BuildMediaSource();
        var legacyStreamInfo = BuildLegacyStreamInfo(mediaSource, PlayMethod.DirectPlay);
        var plan = BuildPlan(PlaybackMethod.DirectPlay, transforms: []);

        var metrics = new PlaybackOperationalMetrics();
        metrics.RecordFallback(PlaybackLiveFallbackReason.AdapterError);
        var shadowOptions = new PlaybackShadowOptions { Mode = PlaybackEngineMode.Canary, CanaryPercentage = 100 };
        shadowOptions.StopThresholds.MinimumSampleSize = 1;
        shadowOptions.StopThresholds.AdapterErrorRateThreshold = 0.10;
        var guard = new PlaybackStopThresholdGuard(() => shadowOptions, metrics, Mock.Of<ILogger<PlaybackStopThresholdGuard>>());

        var (helper, store, sessionId) = CreateFixture(
            legacyStreamInfo,
            PlaybackExecutionPlanResolution.Resolved,
            plan,
            operationalMetrics: metrics,
            stopThresholdGuard: guard);

        Invoke(helper, mediaSource);

        Assert.Equal(LegacyAudioStreamIndex, mediaSource.DefaultAudioStreamIndex);
        AssertFallback(store, sessionId, PlaybackLiveFallbackReason.StopThresholdTripped);
    }

    [Fact]
    public void SetDeviceSpecificData_ServedByV2_RecordsIntoOperationalMetrics()
    {
        var mediaSource = BuildMediaSource();
        var legacyStreamInfo = BuildLegacyStreamInfo(mediaSource, PlayMethod.DirectPlay);
        var plan = BuildPlan(PlaybackMethod.DirectPlay, transforms: []);
        var metrics = new PlaybackOperationalMetrics();

        var (helper, _, _) = CreateFixture(legacyStreamInfo, PlaybackExecutionPlanResolution.Resolved, plan, operationalMetrics: metrics);

        Invoke(helper, mediaSource);

        Assert.Equal(1, metrics.ServedByV2Count);
        Assert.Equal(0, metrics.FallbackReasonCount(PlaybackLiveFallbackReason.KillSwitch));
    }

    [Fact]
    public void SetDeviceSpecificData_FallsBackToLegacy_RecordsFallbackReasonIntoOperationalMetrics()
    {
        var mediaSource = BuildMediaSource();
        var legacyStreamInfo = BuildLegacyStreamInfo(mediaSource, PlayMethod.DirectPlay);
        var metrics = new PlaybackOperationalMetrics();

        var (helper, _, _) = CreateFixture(legacyStreamInfo, PlaybackExecutionPlanResolution.NoAuthoritativeRecord, plan: null, operationalMetrics: metrics);

        Invoke(helper, mediaSource);

        Assert.Equal(0, metrics.ServedByV2Count);
        Assert.Equal(1, metrics.FallbackReasonCount(PlaybackLiveFallbackReason.NoAuthoritativeRecord));
    }

    private static void AssertServed(InMemoryPlaybackLiveWiringDiagnosticsStore store, PlaybackSessionId sessionId)
    {
        var found = store.TryGet(sessionId, out var outcome);
        Assert.True(found);
        Assert.NotNull(outcome);
        Assert.True(outcome!.ServedByV2);
        Assert.Null(outcome.FallbackReason);
    }

    private static void AssertFallback(InMemoryPlaybackLiveWiringDiagnosticsStore store, PlaybackSessionId sessionId, PlaybackLiveFallbackReason reason)
    {
        var found = store.TryGet(sessionId, out var outcome);
        Assert.True(found);
        Assert.NotNull(outcome);
        Assert.False(outcome!.ServedByV2);
        Assert.Equal(reason, outcome.FallbackReason);
    }

    private static void Invoke(MediaInfoHelper helper, MediaSourceInfo mediaSource)
    {
        var item = new Video { Id = Guid.NewGuid() };
        var claimsPrincipal = BuildClaimsPrincipal();

        helper.SetDeviceSpecificData(
            item,
            mediaSource,
            new DeviceProfile(),
            claimsPrincipal,
            maxBitrate: null,
            startTimeTicks: 0,
            mediaSourceId: mediaSource.Id,
            audioStreamIndex: null,
            subtitleStreamIndex: null,
            maxAudioChannels: null,
            playSessionId: "play-session-1",
            userId: Guid.NewGuid(),
            enableDirectPlay: true,
            enableDirectStream: true,
            enableTranscoding: true,
            allowVideoStreamCopy: true,
            allowAudioStreamCopy: true,
            alwaysBurnInSubtitleWhenTranscoding: false,
            ipAddress: IPAddress.Loopback);
    }

    private static (MediaInfoHelper Helper, InMemoryPlaybackLiveWiringDiagnosticsStore Store, PlaybackSessionId SessionId) CreateFixture(
        StreamInfo legacyStreamInfo,
        PlaybackExecutionPlanResolution resolution,
        PlaybackExecutionPlan? plan,
        PlaybackEngineMode mode = PlaybackEngineMode.Canary,
        PlaybackOperationalMetrics? operationalMetrics = null,
        PlaybackStopThresholdGuard? stopThresholdGuard = null)
    {
        var sessionId = PlaybackSessionId.NewId();
        var session = new PlaybackSession(
            sessionId,
            PlaybackMediaKind.Video,
            "play-session-1",
            null,
            new PlaybackPlan(legacyStreamInfo.PlayMethod, default, legacyStreamInfo),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var sessionManagerMock = new Mock<IPlaybackSessionManager>();
        sessionManagerMock
            .Setup(m => m.Create(It.IsAny<PlaybackSessionRequest>(), It.IsAny<string?>()))
            .Returns(session);

        var resolverMock = new Mock<IPlaybackExecutionPlanResolver>();
        resolverMock
            .Setup(r => r.Resolve(It.IsAny<PlaybackSessionId>(), out plan))
            .Returns(resolution);

        var user = new User("test-user", "auth-provider", "reset-provider");
        user.SetPermission(PermissionKind.EnableAudioPlaybackTranscoding, true);
        user.SetPermission(PermissionKind.EnableVideoPlaybackTranscoding, true);
        user.SetPermission(PermissionKind.EnablePlaybackRemuxing, true);
        var userManagerMock = new Mock<IUserManager>();
        userManagerMock.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns(user);

        var configManagerMock = new Mock<IServerConfigurationManager>();
        configManagerMock.Setup(c => c.Configuration).Returns(new ServerConfiguration
        {
            PlaybackShadow = new PlaybackShadowOptions { Mode = mode, CanaryPercentage = 100 },
        });

        var store = new InMemoryPlaybackLiveWiringDiagnosticsStore();

        var helper = new MediaInfoHelper(
            userManagerMock.Object,
            Mock.Of<IItemLookupService>(),
            Mock.Of<IMediaSourceManager>(),
            Mock.Of<IMediaEncoder>(),
            configManagerMock.Object,
            Mock.Of<ILogger<MediaInfoHelper>>(),
            Mock.Of<INetworkManager>(),
            Mock.Of<IDeviceManager>(),
            sessionManagerMock.Object,
            resolverMock.Object,
            store,
            operationalMetrics,
            stopThresholdGuard);

        return (helper, store, sessionId);
    }

    private static ClaimsPrincipal BuildClaimsPrincipal(string deviceId = "device-1", string token = "token-1")
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(InternalClaimTypes.DeviceId, deviceId),
            new Claim(InternalClaimTypes.Token, token),
        });
        return new ClaimsPrincipal(identity);
    }

    private static MediaSourceInfo BuildMediaSource(bool withSubtitleStream = false, bool dolbyVisionVideoStream = false)
    {
        var videoStream = new MediaStream
        {
            Index = 0,
            Type = MediaStreamType.Video,
            Codec = "hevc",
        };

        if (dolbyVisionVideoStream)
        {
            // Same shape as the PR111b/PR115b oracle fixture mp4-dvhe.08-eac3-15200k: profile 8,
            // HDR10 base layer compatibility id - resolves to VideoRangeType.DOVIWithHDR10.
            videoStream.CodecTag = "dvhe";
            videoStream.DvProfile = 8;
            videoStream.DvBlSignalCompatibilityId = 1;
            videoStream.RpuPresentFlag = 1;
            videoStream.BlPresentFlag = 1;
        }

        var audioStream = new MediaStream
        {
            Index = V2AudioStreamIndex,
            Type = MediaStreamType.Audio,
            Codec = "aac",
        };

        var streams = new List<MediaStream> { videoStream, audioStream };
        if (withSubtitleStream)
        {
            streams.Add(new MediaStream
            {
                Index = 2,
                Type = MediaStreamType.Subtitle,
                Codec = "subrip",
                IsExternal = false,
            });
        }

        return new MediaSourceInfo
        {
            Id = SourceId,
            Protocol = MediaProtocol.File,
            Container = "mkv",
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            MediaStreams = streams,
        };
    }

    private static StreamInfo BuildLegacyStreamInfo(MediaSourceInfo mediaSource, PlayMethod playMethod) => new()
    {
        ItemId = Guid.NewGuid(),
        MediaSource = mediaSource,
        DeviceProfile = new DeviceProfile(),
        PlayMethod = playMethod,
        Container = "mkv",
        AudioStreamIndex = LegacyAudioStreamIndex,
        VideoCodecs = ["hevc"],
        AudioCodecs = ["aac"],
    };

    private static PlaybackExecutionPlan BuildPlan(
        PlaybackMethod method,
        TransformKind[] transforms,
        string sourceId = SourceId,
        int? videoIndex = 0,
        string videoCodec = "hevc",
        int? subtitleIndex = null,
        Tesserafin.Playback.Decision.SubtitleDeliveryMethod? subtitleDelivery = null) => new(
        Method: method,
        SourceId: sourceId,
        Container: "mp4",
        Protocol: subtitleDelivery == Tesserafin.Playback.Decision.SubtitleDeliveryMethod.Hls ? StreamingProtocol.Hls : StreamingProtocol.Http,
        VideoStreamIndex: videoIndex,
        VideoCodec: videoIndex is null ? null : videoCodec,
        VideoBitrate: method == PlaybackMethod.Transcode ? 4_000_000 : null,
        Resolution: null,
        VideoRange: videoIndex is null ? null : "SDR",
        AudioStreamIndex: V2AudioStreamIndex,
        AudioCodec: "aac",
        AudioBitrate: transforms.Contains(TransformKind.TranscodeAudio) ? 192_000 : null,
        AudioChannels: 2,
        TotalBitrate: 4_000_000,
        SubtitleStreamIndex: subtitleIndex,
        SubtitleDelivery: subtitleIndex is null ? null : subtitleDelivery,
        SubtitleFormat: subtitleIndex is null ? null : "ass",
        Transforms: transforms);
}
