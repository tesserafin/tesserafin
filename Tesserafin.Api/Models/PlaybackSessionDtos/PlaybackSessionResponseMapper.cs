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
/// <c>LegacyDecisionProjector</c> documented. Likewise, a session without a retained engine record
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
            DeriveTransforms(method, plan.TranscodeReasons),
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
    /// (<see cref="DeriveTransforms"/>) are involved, because
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
    private static IReadOnlyList<TransformKind> DeriveTransforms(PlaybackMethod method, TranscodeReason reasons)
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

        if (reasons.HasFlag(TranscodeReason.SubtitleCodecNotSupported))
        {
            transforms.Add(TransformKind.BurnInSubtitle);
        }

        if (reasons.HasFlag(TranscodeReason.ContainerNotSupported))
        {
            transforms.Add(TransformKind.RemuxContainer);
        }

        return transforms.ToList();
    }
}
