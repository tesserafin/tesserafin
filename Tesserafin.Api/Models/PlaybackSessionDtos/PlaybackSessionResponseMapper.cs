using System;
using System.Collections.Generic;
using System.Linq;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Session;
using Tesserafin.Playback.Decision;
using LegacySubtitleDeliveryMethod = Tesserafin.Model.Dlna.SubtitleDeliveryMethod;

namespace Tesserafin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// Maps a legacy-planned <see cref="PlaybackSession"/> into the stable
/// <see cref="PlaybackSessionResponse"/> contract (docs/pr92-design-playback-api-and-diagnostics.md
/// §4.2). Mirrors the derivation heuristics of
/// <c>Tesserafin.Playback.Shadow.LegacyDecisionProjector</c> (PR98/PR111c) — same method mapping, same
/// best-effort tonemap/subtitle-conversion detection — but retargets them from the shadow
/// comparator's <c>DecisionVector</c>/<c>TransformClass</c> vocabulary onto the real
/// <see cref="Tesserafin.Playback.Decision"/> vocabulary this response exposes, and never onto that
/// shadow vocabulary itself.
/// </summary>
/// <remarks>
/// Legacy has no explicit transform vocabulary and <see cref="PlaybackPlan.TranscodeReasons"/>
/// records which walls a decision hit, not what the pipeline will actually do about it, so
/// <see cref="PlaybackSessionResponse.Transforms"/> is derived, best-effort, exactly as
/// <c>LegacyDecisionProjector</c> documents. Likewise, <see cref="Tesserafin.Model.Dlna.StreamInfo"/>
/// does not expose a reliable "selected video stream index", so
/// <see cref="PlaybackSessionResponse.SelectedStreams"/>.Video is always left <see langword="null"/>
/// for legacy-sourced sessions rather than fabricate one — the real
/// <see cref="SelectedStreams"/> contract has no tri-state "unknown" the way the shadow
/// <c>StreamSelection</c> type does, so this is a real (documented) loss of precision, not a bug.
/// </remarks>
public static class PlaybackSessionResponseMapper
{
    /// <summary>
    /// Maps a tracked playback session into its stable response projection.
    /// </summary>
    /// <param name="session">The session to map.</param>
    /// <returns>
    /// The mapped response, versioned <see cref="PlaybackSessionResponse.LegacyDecisionVersion"/>
    /// since the legacy planner remains the source of truth for this slice.
    /// </returns>
    public static PlaybackSessionResponse Map(PlaybackSession session)
    {
        // Without the retained engine record only the plan's own method and reasons are known.
        var plan = session.Plan;
        var method = MapMethod(plan.PlayMethod);

        return new PlaybackSessionResponse(
            session.Id.Value,
            MapKind(session.Kind),
            PlaybackSessionResponse.LegacyDecisionVersion,
            method,
            OutputSpec.Empty,
            SelectedStreams.None,
            DeriveTransforms(method, plan.TranscodeReasons, null),
            MapReasons(plan.TranscodeReasons),
            session.CreatedAt,
            session.UpdatedAt,
            session.PlaybackAttemptId);
    }

    /// <summary>
    /// Maps a tracked playback session into its stable response projection, preferring the
    /// authoritative v2 decision when one is retained (PR115a). For a session v2 is authoritative
    /// for - <paramref name="v2Record"/> is non-null and its <see cref="V2PlanRecord.ExecutionPlan"/>
    /// is non-null - the response is that decision verbatim, versioned with the real
    /// <see cref="PlaybackDecision.EngineVersion"/>: no legacy derivation heuristics
    /// (<see cref="DeriveTransforms"/>, <see cref="DetectedTonemap"/>, etc.) are involved, because
    /// none are needed - v2 already carries <see cref="PlaybackDecision.Transforms"/> and a
    /// structured <see cref="PlaybackDecision.Reasoning"/> tree directly. The guard is plan-centric,
    /// not viability-centric: <see cref="V2PlanRecord.Decision"/> can be
    /// <see cref="PlaybackDecision.IsViable"/> while <see cref="V2PlanRecord.ExecutionPlan"/> is still
    /// <see langword="null"/> (<see cref="Tesserafin.Playback.Execution.PlaybackExecutionPlanBuilder"/>
    /// refused it) - the PR115c live path falls back to legacy execution for that session, so the
    /// response must too, or it would announce v2 authorship the client never actually gets. A
    /// <see langword="null"/> record, or a retained record with a <see langword="null"/>
    /// <see cref="V2PlanRecord.ExecutionPlan"/> (viable decision or not), falls back to the legacy
    /// projection (<see cref="Map(PlaybackSession)"/>) versioned
    /// <see cref="PlaybackSessionResponse.LegacyDecisionVersion"/> - the response must never claim v2
    /// authorship when what the client will effectively get is legacy.
    /// </summary>
    /// <param name="session">The session to map.</param>
    /// <param name="v2Record">The retained v2 plan record for this session, if any.</param>
    /// <returns>The mapped response.</returns>
    public static PlaybackSessionResponse Map(PlaybackSession session, V2PlanRecord? v2Record)
    {
        if (v2Record?.ExecutionPlan is null)
        {
            return Map(session);
        }

        var decision = v2Record.Decision;

        return new PlaybackSessionResponse(
            session.Id.Value,
            MapKind(session.Kind),
            decision.EngineVersion,
            decision.Method,
            decision.Output,
            decision.SelectedStreams,
            decision.Transforms,
            FlattenReasons(decision.Reasoning),
            session.CreatedAt,
            session.UpdatedAt,
            session.PlaybackAttemptId);
    }

    /// <summary>
    /// Depth-first, pre-order walk of a <see cref="PlaybackDecision.Reasoning"/> tree, collecting
    /// each node's <see cref="ReasonNode.Code"/> in first-encounter order with duplicates removed -
    /// the same flat, code-only summary shape <see cref="MapReasons"/> produces for legacy, just
    /// read directly off v2's own structured trace instead of derived from
    /// <see cref="TranscodeReason"/> flags.
    /// </summary>
    /// <param name="root">The root of the reasoning tree.</param>
    /// <returns>The distinct reason codes encountered, in first-encounter order.</returns>
    private static IReadOnlyList<ReasonCode> FlattenReasons(ReasonNode root)
    {
        var seen = new HashSet<ReasonCode>();
        var codes = new List<ReasonCode>();

        void Visit(ReasonNode node)
        {
            if (seen.Add(node.Code))
            {
                codes.Add(node.Code);
            }

            foreach (var child in node.Children)
            {
                Visit(child);
            }
        }

        Visit(root);

        return codes;
    }

    private static MediaKind MapKind(PlaybackMediaKind kind) => kind switch
    {
        PlaybackMediaKind.Audio => MediaKind.Audio,
        PlaybackMediaKind.Video => MediaKind.Video,
        _ => MediaKind.Video,
    };

    private static PlaybackMethod MapMethod(PlayMethod method) => method switch
    {
        PlayMethod.DirectPlay => PlaybackMethod.DirectPlay,
        PlayMethod.DirectStream => PlaybackMethod.Remux,
        PlayMethod.Transcode => PlaybackMethod.Transcode,
        _ => PlaybackMethod.Transcode,
    };

    private static OutputSpec MapOutput(StreamInfo? streamInfo)
    {
        if (streamInfo is null)
        {
            return OutputSpec.Empty;
        }

        var resolution = streamInfo.TargetWidth is int width && streamInfo.TargetHeight is int height
            ? new Resolution(width, height)
            : null;

        var protocol = streamInfo.SubProtocol == MediaStreamProtocol.hls ? StreamingProtocol.Hls : StreamingProtocol.Http;

        // A subtitle format is only meaningful when a subtitle is actually selected (see
        // MapSelectedSubtitle) - -1/null means "no subtitle selected", not "format unknown".
        var subtitleSelected = streamInfo.SubtitleStreamIndex is int subtitleIdx && subtitleIdx >= 0;

        return new OutputSpec(
            Container: streamInfo.Container,
            VideoCodec: streamInfo.TargetVideoCodec.FirstOrDefault(),
            AudioCodec: streamInfo.TargetAudioCodec.FirstOrDefault(),
            Resolution: resolution,
            VideoRange: ProjectVideoRange(streamInfo),
            AudioChannels: streamInfo.TargetAudioChannels,
            // Legacy StreamInfo.TargetTotalBitrate fabricates a value out of unknown per-stream
            // ceilings (`(TargetAudioBitrate ?? 0) + (TargetVideoBitrate ?? 0)`, defaulting the
            // unknown half to zero) - this mapper must not manufacture a known value out of an
            // unknown one, so TotalBitrate and AudioBitrate are left unpopulated rather than
            // invented. Only VideoBitrate has a clean legacy source (StreamInfo.TargetVideoBitrate).
            TotalBitrate: null,
            VideoBitrate: streamInfo.TargetVideoBitrate,
            AudioBitrate: null,
            Protocol: protocol,
            SubtitleFormat: subtitleSelected ? streamInfo.SubtitleFormat : null);
    }

    /// <summary>
    /// Maps the legacy <see cref="VideoRangeType"/> to the same string vocabulary
    /// <see cref="OutputSpec.VideoRange"/> uses (for example <c>"SDR"</c>, <c>"HDR10"</c>).
    /// <see cref="VideoRangeType.Unknown"/> maps to <see langword="null"/>, not a literal
    /// "Unknown" string, since it is not a real range value.
    /// </summary>
    private static string? ProjectVideoRange(StreamInfo streamInfo)
    {
        var rangeType = streamInfo.TargetVideoRangeType;
        return rangeType == VideoRangeType.Unknown ? null : rangeType.ToString();
    }

    private static SelectedStreams MapSelectedStreams(StreamInfo? streamInfo)
    {
        if (streamInfo is null)
        {
            return SelectedStreams.None;
        }

        // Legacy StreamInfo has no reliable notion of "the selected video stream index" (the same
        // gap LegacyDecisionProjector documents for the shadow comparator): rather than fabricate
        // one, this is left null. Unlike the shadow DecisionVector's StreamSelection, the real
        // SelectedStreams contract has no separate "unknown" state, so this null is a documented
        // loss of precision, not "no video stream selected".
        int? video = null;

        // A null legacy AudioStreamIndex means "fell back to the default audio track", not "no
        // audio selected" (audio is mandatory for video playback). The real SelectedStreams
        // contract does not distinguish those two cases either, so this is likewise an
        // approximation rather than an invented value.
        int? audio = streamInfo.AudioStreamIndex;

        return new SelectedStreams(video, audio, MapSelectedSubtitle(streamInfo));
    }

    private static SelectedSubtitle? MapSelectedSubtitle(StreamInfo streamInfo)
    {
        // -1 (not null) is how legacy StreamInfo/StreamBuilder represent "no subtitle selected".
        if (streamInfo.SubtitleStreamIndex is not int index || index < 0)
        {
            return null;
        }

        // Tesserafin.Model.Dlna.SubtitleDeliveryMethod and Tesserafin.Playback.Decision.SubtitleDeliveryMethod
        // share the same name: the decision-vocab side is fully qualified below to disambiguate,
        // rather than adding a second alias on top of LegacySubtitleDeliveryMethod.
        var delivery = streamInfo.SubtitleDeliveryMethod switch
        {
            LegacySubtitleDeliveryMethod.Encode => Tesserafin.Playback.Decision.SubtitleDeliveryMethod.Burn,
            LegacySubtitleDeliveryMethod.Embed => Tesserafin.Playback.Decision.SubtitleDeliveryMethod.Embed,
            LegacySubtitleDeliveryMethod.External => Tesserafin.Playback.Decision.SubtitleDeliveryMethod.External,
            LegacySubtitleDeliveryMethod.Hls => Tesserafin.Playback.Decision.SubtitleDeliveryMethod.Hls,
            // Drop means the subtitle is not actually delivered to the client - equivalent to no
            // selection for this response's purposes.
            _ => (Tesserafin.Playback.Decision.SubtitleDeliveryMethod?)null,
        };

        return delivery is Tesserafin.Playback.Decision.SubtitleDeliveryMethod method ? new SelectedSubtitle(index, method) : null;
    }

    /// <summary>
    /// Maps each set <see cref="TranscodeReason"/> flag to its mirrored <see cref="ReasonCode"/>
    /// member (the two enums share member names one-to-one for the constraint codes, per
    /// <see cref="ReasonCode"/>'s own remarks). Only constraint codes can ever be derived this way
    /// - v2's positive/marker codes (<see cref="ReasonCode.MethodChosen"/>,
    /// <see cref="ReasonCode.TonemapRequired"/>, etc.) have no legacy flag and never appear here.
    /// </summary>
    private static IReadOnlyList<ReasonCode> MapReasons(TranscodeReason reasons)
    {
        var codes = new List<ReasonCode>();
        foreach (var flag in reasons.GetUniqueFlags())
        {
            if (Enum.TryParse<ReasonCode>(flag.ToString(), out var code))
            {
                codes.Add(code);
            }
        }

        return codes;
    }

    /// <summary>
    /// Best-effort derivation of the transform set a legacy decision implies, since
    /// <see cref="TranscodeReason"/> records which walls were hit, not what the pipeline will do
    /// about them. Ports <c>LegacyDecisionProjector.DeriveTransformClasses</c>'s logic directly off
    /// the legacy reason flags (no intermediate category-folding needed here: unlike the shadow
    /// comparator, which folds many-to-one into a handful of <c>ReasonCategory</c> values shared
    /// with v2, this mapper's flags already carry one-to-one names with <see cref="ReasonCode"/>).
    /// </summary>
    private static IReadOnlyList<TransformKind> DeriveTransforms(PlaybackMethod method, TranscodeReason reasons, StreamInfo? streamInfo)
    {
        var transforms = new HashSet<TransformKind>();

        if (method == PlaybackMethod.Remux)
        {
            transforms.Add(TransformKind.RemuxContainer);
            return transforms.ToList();
        }

        if (method != PlaybackMethod.Transcode)
        {
            return transforms.ToList();
        }

        const TranscodeReason videoCodecReasons =
            TranscodeReason.VideoCodecNotSupported | TranscodeReason.VideoProfileNotSupported |
            TranscodeReason.VideoLevelNotSupported | TranscodeReason.VideoCodecTagNotSupported |
            TranscodeReason.RefFramesNotSupported | TranscodeReason.VideoRotationNotSupported |
            TranscodeReason.AnamorphicVideoNotSupported | TranscodeReason.InterlacedVideoNotSupported;
        const TranscodeReason videoDimsReasons =
            TranscodeReason.VideoResolutionNotSupported | TranscodeReason.VideoBitDepthNotSupported |
            TranscodeReason.VideoFramerateNotSupported;
        const TranscodeReason audioCodecReasons = TranscodeReason.AudioCodecNotSupported | TranscodeReason.AudioProfileNotSupported;
        const TranscodeReason audioRateReasons = TranscodeReason.AudioSampleRateNotSupported | TranscodeReason.AudioBitDepthNotSupported;

        if ((reasons & (videoCodecReasons | videoDimsReasons | TranscodeReason.VideoRangeTypeNotSupported)) != 0)
        {
            transforms.Add(TransformKind.TranscodeVideo);
        }

        if ((reasons & (audioCodecReasons | TranscodeReason.AudioChannelsNotSupported | audioRateReasons)) != 0)
        {
            transforms.Add(TransformKind.TranscodeAudio);
        }

        if (reasons.HasFlag(TranscodeReason.AudioChannelsNotSupported))
        {
            transforms.Add(TransformKind.Downmix);
        }

        if (reasons.HasFlag(TranscodeReason.VideoRangeTypeNotSupported))
        {
            transforms.Add(TransformKind.Tonemap);
        }

        // PR111c: an HDR source transcoded to an SDR target is tonemapped even when the transcode
        // was forced by an unsupported video codec instead of the range itself, so legacy records
        // no VideoRangeTypeNotSupported bit for it. Derived from output state, independently of the
        // reason flags, and only alongside an actual video transcode - mirrors
        // LegacyDecisionProjector.DetectedTonemap exactly.
        if (transforms.Contains(TransformKind.TranscodeVideo) && DetectedTonemap(streamInfo))
        {
            transforms.Add(TransformKind.Tonemap);
        }

        if (reasons.HasFlag(TranscodeReason.SubtitleCodecNotSupported))
        {
            transforms.Add(TransformKind.BurnInSubtitle);
        }

        // PR111c: a real subtitle text-format conversion carries no reason bit at all (legacy emits
        // nothing for a successful re-encode) - mirrors LegacyDecisionProjector.DetectedSubtitleConversion.
        // Already gated to method == Transcode by the early return above.
        if (DetectedSubtitleConversion(streamInfo))
        {
            transforms.Add(TransformKind.ConvertSubtitle);
        }

        if (reasons.HasFlag(TranscodeReason.ContainerNotSupported))
        {
            transforms.Add(TransformKind.RemuxContainer);
        }

        return transforms.ToList();
    }

    /// <summary>
    /// Best-effort detection of a real legacy HDR-to-SDR tonemap. Direct port of
    /// <c>LegacyDecisionProjector.DetectedTonemap</c>.
    /// </summary>
    private static bool DetectedTonemap(StreamInfo? streamInfo)
    {
        if (streamInfo is null)
        {
            return false;
        }

        var sourceRange = streamInfo.TargetVideoStream?.VideoRangeType;
        if (sourceRange is null or VideoRangeType.Unknown or VideoRangeType.SDR)
        {
            return false;
        }

        return streamInfo.TargetVideoRangeType == VideoRangeType.SDR;
    }

    /// <summary>
    /// Best-effort detection of a real legacy subtitle text-format conversion. Direct port of
    /// <c>LegacyDecisionProjector.DetectedSubtitleConversion</c>.
    /// </summary>
    private static bool DetectedSubtitleConversion(StreamInfo? streamInfo)
    {
        if (streamInfo is null || streamInfo.SubtitleStreamIndex is null or < 0)
        {
            return false;
        }

        if (streamInfo.SubtitleDeliveryMethod == LegacySubtitleDeliveryMethod.Encode)
        {
            return false;
        }

        var sourceCodec = streamInfo.MediaSource?.MediaStreams
            ?.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && s.Index == streamInfo.SubtitleStreamIndex)
            ?.Codec;

        return !string.Equals(NormalizeSubtitleFormat(sourceCodec), NormalizeSubtitleFormat(streamInfo.SubtitleFormat), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Normalizes the <c>webvtt</c>/<c>vtt</c> spelling alias, same as
    /// <c>LegacyDecisionProjector.NormalizeSubtitleFormat</c>, so it never looks like a real
    /// conversion in <see cref="DetectedSubtitleConversion"/>.
    /// </summary>
    private static string? NormalizeSubtitleFormat(string? format) =>
        string.Equals(format, "webvtt", StringComparison.OrdinalIgnoreCase) ? "vtt" : format;
}
