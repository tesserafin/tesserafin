using System;
using System.Collections.Generic;
using System.Linq;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Session;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Execution;
using Xunit;
using LegacySubtitleDeliveryMethod = Tesserafin.Model.Dlna.SubtitleDeliveryMethod;

namespace Tesserafin.Api.Tests.Models.PlaybackSessionDtos;

/// <summary>
/// Unit tests for <see cref="PlaybackSessionResponseMapper"/>: hand-built <see cref="PlaybackSession"/>
/// values (mirroring <c>LegacyDecisionProjectorTests</c>'s style/fixtures), checked against the
/// expected <see cref="PlaybackSessionResponse"/>. Covers method mapping, best-effort transform/
/// reason derivation (including the PR111c tonemap/subtitle-conversion detection), and the
/// documented approximations (unknown video stream index, unpopulated total/audio bitrate).
/// </summary>
public sealed class PlaybackSessionResponseMapperTests
{
    [Fact]
    public void Map_DirectStream_NormalizesToRemuxMethodWithRemuxContainerTransform()
    {
        var session = BuildSession(PlayMethod.DirectStream, TranscodeReason.ContainerNotSupported);

        var response = PlaybackSessionResponseMapper.Map(session);

        Assert.Equal(PlaybackMethod.Remux, response.Method);
        Assert.Equal(new[] { TransformKind.RemuxContainer }, response.Transforms);
        Assert.Equal(new[] { ReasonCode.ContainerNotSupported }, response.Reasons);
    }

    [Theory]
    [InlineData(TranscodeReason.VideoCodecNotSupported, ReasonCode.VideoCodecNotSupported)]
    [InlineData(TranscodeReason.AudioCodecNotSupported, ReasonCode.AudioCodecNotSupported)]
    [InlineData(TranscodeReason.SubtitleCodecNotSupported, ReasonCode.SubtitleCodecNotSupported)]
    [InlineData(TranscodeReason.VideoRangeTypeNotSupported, ReasonCode.VideoRangeTypeNotSupported)]
    public void Map_Transcode_MirrorsEachTranscodeReasonToItsOneToOneReasonCode(TranscodeReason reason, ReasonCode expectedCode)
    {
        var session = BuildSession(PlayMethod.Transcode, reason);

        var response = PlaybackSessionResponseMapper.Map(session);

        Assert.Contains(expectedCode, response.Reasons);
    }

    [Fact]
    public void Map_Transcode_AudioCodecReason_DerivesTranscodeAudioOnly()
    {
        var session = BuildSession(PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported);

        var response = PlaybackSessionResponseMapper.Map(session);

        Assert.Equal(new[] { TransformKind.TranscodeAudio }, response.Transforms);
    }

    [Fact]
    public void Map_Transcode_AudioChannelsReason_DerivesTranscodeAudioAndDownmix()
    {
        var session = BuildSession(PlayMethod.Transcode, TranscodeReason.AudioChannelsNotSupported);

        var response = PlaybackSessionResponseMapper.Map(session);

        Assert.Equal(
            new HashSet<TransformKind> { TransformKind.TranscodeAudio, TransformKind.Downmix },
            response.Transforms.ToHashSet());
    }

    [Fact]
    public void Map_Transcode_VideoRangeReason_DerivesTranscodeVideoAndTonemap()
    {
        var session = BuildSession(PlayMethod.Transcode, TranscodeReason.VideoRangeTypeNotSupported);

        var response = PlaybackSessionResponseMapper.Map(session);

        Assert.Equal(
            new HashSet<TransformKind> { TransformKind.TranscodeVideo, TransformKind.Tonemap },
            response.Transforms.ToHashSet());
    }

    [Fact]
    public void Map_Transcode_SubtitleReason_DerivesBurnInSubtitle()
    {
        var session = BuildSession(PlayMethod.Transcode, TranscodeReason.SubtitleCodecNotSupported);

        var response = PlaybackSessionResponseMapper.Map(session);

        Assert.Equal(new[] { TransformKind.BurnInSubtitle }, response.Transforms);
    }

    [Fact]
    public void Map_NoStreamInfo_ReturnsEmptyOutputAndNoSelectedStreams()
    {
        var session = BuildSession(PlayMethod.DirectPlay, 0);

        var response = PlaybackSessionResponseMapper.Map(session);

        Assert.Equal(OutputSpec.Empty, response.Output);
        Assert.Equal(SelectedStreams.None, response.SelectedStreams);
        Assert.Empty(response.Transforms);
        Assert.Empty(response.Reasons);
    }

    [Fact]
    public void Map_DoesNotPopulateTotalOrAudioBitrate_ToAvoidFabricatingAValue()
    {
        var session = BuildSession(PlayMethod.Transcode, TranscodeReason.VideoBitrateNotSupported);

        var response = PlaybackSessionResponseMapper.Map(session);

        Assert.Null(response.Output.TotalBitrate);
        Assert.Null(response.Output.AudioBitrate);
    }

    [Fact]
    public void Map_AudioKind_MapsToMediaKindAudio()
    {
        var session = new PlaybackSession(
            PlaybackSessionId.NewId(),
            PlaybackMediaKind.Audio,
            null,
            null,
            new PlaybackPlan(PlayMethod.DirectPlay, default),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(1));

        var response = PlaybackSessionResponseMapper.Map(session);

        Assert.Equal(MediaKind.Audio, response.Kind);
        Assert.Equal(session.Id.Value, response.Id);
        Assert.Equal(session.CreatedAt, response.CreatedAt);
        Assert.Equal(session.UpdatedAt, response.UpdatedAt);
    }

    // --- PR115a: Map(session, v2Record) overload ---

    [Fact]
    public void Map_WithNullV2Record_IsIdenticalToLegacyMap()
    {
        var session = BuildSession(PlayMethod.DirectPlay, 0);

        var legacyResponse = PlaybackSessionResponseMapper.Map(session);
        var overloadResponse = PlaybackSessionResponseMapper.Map(session, v2Record: null);

        AssertResponsesEqual(legacyResponse, overloadResponse);
        Assert.Equal(PlaybackSessionResponse.LegacyDecisionVersion, overloadResponse.DecisionVersion);
    }

    [Fact]
    public void Map_WithViableV2RecordAndExecutionPlan_ReturnsV2DecisionVerbatimAndDedupedFlattenedReasons()
    {
        // The legacy-sourced session (method/streamInfo) is deliberately different from the v2
        // decision below - proving the overload takes EVERYTHING from the v2 decision verbatim, not
        // a blend with the legacy projection.
        var session = BuildSession(PlayMethod.DirectPlay, 0);

        var output = new OutputSpec(
            Container: "mkv",
            VideoCodec: "hevc",
            AudioCodec: "eac3",
            Resolution: new Resolution(1920, 1080),
            VideoRange: "HDR10",
            AudioChannels: 6,
            TotalBitrate: 8_000_000,
            VideoBitrate: 7_500_000,
            AudioBitrate: 500_000,
            Protocol: StreamingProtocol.Hls,
            SubtitleFormat: "vtt");
        var selectedStreams = new SelectedStreams(
            Video: 0,
            Audio: 2,
            Subtitle: new SelectedSubtitle(3, Tesserafin.Playback.Decision.SubtitleDeliveryMethod.Hls));
        var transforms = new List<TransformKind> { TransformKind.TranscodeVideo, TransformKind.Tonemap };

        // ReasonCode.VideoRangeTypeNotSupported appears twice in the tree (once directly under the
        // root, once nested under the VideoCodecNotSupported child) - the flattened output must dedup
        // it to its FIRST encounter, in pre-order: [MethodChosen, VideoRangeTypeNotSupported,
        // VideoCodecNotSupported]. VideoCodecNotSupported's own duplicate child must not add a second
        // entry.
        var reasoning = new ReasonNode(
            ReasonCode.MethodChosen,
            ReasonOutcome.Chosen,
            ReasonSubject.Method(),
            null,
            new[]
            {
                ReasonNode.Leaf(ReasonCode.VideoRangeTypeNotSupported, ReasonOutcome.Rejected, ReasonSubject.Method()),
                new ReasonNode(
                    ReasonCode.VideoCodecNotSupported,
                    ReasonOutcome.Rejected,
                    ReasonSubject.Method(),
                    null,
                    new[] { ReasonNode.Leaf(ReasonCode.VideoRangeTypeNotSupported, ReasonOutcome.Rejected, ReasonSubject.Method()) }),
            });

        var decision = PlaybackDecision.Transcode("v2-source", selectedStreams, output, transforms, reasoning, engineVersion: 6);
        var executionPlan = new PlaybackExecutionPlan(
            PlaybackMethod.Transcode,
            "v2-source",
            output.Container!,
            output.Protocol,
            selectedStreams.Video,
            output.VideoCodec,
            output.VideoBitrate,
            output.Resolution,
            output.VideoRange,
            selectedStreams.Audio,
            output.AudioCodec,
            output.AudioBitrate,
            output.AudioChannels,
            output.TotalBitrate,
            selectedStreams.Subtitle?.Index,
            selectedStreams.Subtitle?.Delivery,
            output.SubtitleFormat,
            transforms);
        var v2Record = new V2PlanRecord(decision, executionPlan, DateTimeOffset.UtcNow);

        var response = PlaybackSessionResponseMapper.Map(session, v2Record);

        Assert.Equal(6, response.DecisionVersion);
        Assert.Equal(decision.EngineVersion, response.DecisionVersion);
        Assert.Equal(PlaybackMethod.Transcode, response.Method);
        Assert.Equal(output, response.Output);
        Assert.Equal(selectedStreams, response.SelectedStreams);
        Assert.Equal(transforms, response.Transforms);
        Assert.Equal(
            new[] { ReasonCode.MethodChosen, ReasonCode.VideoRangeTypeNotSupported, ReasonCode.VideoCodecNotSupported },
            response.Reasons);
        Assert.Equal(session.Id.Value, response.Id);
        Assert.Equal(session.CreatedAt, response.CreatedAt);
        Assert.Equal(session.UpdatedAt, response.UpdatedAt);
    }

    [Fact]
    public void Map_WithNotViableV2Record_FallsBackToLegacyProjection()
    {
        // v2 was authoritative for the planning call but produced nothing executable: the response
        // must not claim v2 authorship it did not deliver on, so it falls back to the legacy
        // projection exactly as a null record would.
        var session = BuildSession(PlayMethod.DirectPlay, 0);

        var notViable = PlaybackDecision.NotViable(
            PlaybackMethod.Transcode,
            new ReasonNode(ReasonCode.NoViablePlan, ReasonOutcome.Rejected, ReasonSubject.Method(), null, []),
            engineVersion: 6);
        var v2Record = new V2PlanRecord(notViable, ExecutionPlan: null, DateTimeOffset.UtcNow);

        var legacyResponse = PlaybackSessionResponseMapper.Map(session);
        var overloadResponse = PlaybackSessionResponseMapper.Map(session, v2Record);

        AssertResponsesEqual(legacyResponse, overloadResponse);
        Assert.Equal(PlaybackSessionResponse.LegacyDecisionVersion, overloadResponse.DecisionVersion);
    }

    [Fact]
    public void Map_WithViableV2RecordButNullExecutionPlan_FallsBackToLegacyProjection()
    {
        // The plan builder can refuse a VIABLE decision (V2PlanRecord.ExecutionPlan remarks): the
        // PR115c live path falls back to legacy execution for that session, so the response must too
        // - it must never announce v2 authorship (DecisionVersion 6) when the client will effectively
        // get legacy. The guard is plan-centric, not viability-centric.
        var session = BuildSession(PlayMethod.DirectPlay, 0);

        var output = new OutputSpec(
            Container: "mkv",
            VideoCodec: "hevc",
            AudioCodec: "eac3",
            Resolution: new Resolution(1920, 1080),
            VideoRange: "HDR10",
            AudioChannels: 6,
            TotalBitrate: 8_000_000,
            VideoBitrate: 7_500_000,
            AudioBitrate: 500_000,
            Protocol: StreamingProtocol.Hls,
            SubtitleFormat: "vtt");
        var selectedStreams = new SelectedStreams(Video: 0, Audio: 2, Subtitle: null);
        var transforms = new List<TransformKind> { TransformKind.TranscodeVideo };
        var reasoning = ReasonNode.Leaf(ReasonCode.MethodChosen, ReasonOutcome.Chosen, ReasonSubject.Method());

        var viableDecision = PlaybackDecision.Transcode("v2-source", selectedStreams, output, transforms, reasoning, engineVersion: 6);
        Assert.True(viableDecision.IsViable);

        var v2Record = new V2PlanRecord(viableDecision, ExecutionPlan: null, DateTimeOffset.UtcNow);

        var legacyResponse = PlaybackSessionResponseMapper.Map(session);
        var overloadResponse = PlaybackSessionResponseMapper.Map(session, v2Record);

        AssertResponsesEqual(legacyResponse, overloadResponse);
        Assert.Equal(PlaybackSessionResponse.LegacyDecisionVersion, overloadResponse.DecisionVersion);
    }

    // PlaybackSessionResponse is a record whose Transforms/Reasons members are IReadOnlyList<T>:
    // the synthesized record Equals compares those by reference (List<T> has no value equality),
    // so two independently-mapped responses with equal list CONTENTS are never Assert.Equal. Compare
    // field-by-field instead, letting xunit's collection-aware Assert.Equal handle Transforms/Reasons
    // by sequence.
    private static void AssertResponsesEqual(PlaybackSessionResponse expected, PlaybackSessionResponse actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.DecisionVersion, actual.DecisionVersion);
        Assert.Equal(expected.Method, actual.Method);
        Assert.Equal(expected.Output, actual.Output);
        Assert.Equal(expected.SelectedStreams, actual.SelectedStreams);
        Assert.Equal(expected.Transforms, actual.Transforms);
        Assert.Equal(expected.Reasons, actual.Reasons);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
    }

    private static PlaybackSession BuildSession(PlayMethod playMethod, TranscodeReason reasons)
        => new(
            PlaybackSessionId.NewId(),
            PlaybackMediaKind.Video,
            null,
            null,
            new PlaybackPlan(playMethod, reasons),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(1));
}
