using System.Collections.Generic;
using Tesserafin.Playback.Decision;

namespace Tesserafin.Playback.Shadow;

/// <summary>
/// Projects a v2 <see cref="PlaybackDecision"/> into a <see cref="DecisionVector"/> comparable
/// against the legacy planner's decision, per docs/pr93-compatibility-lab.md §4.
/// </summary>
public static class V2DecisionProjector
{
    /// <summary>
    /// Projects a v2 playback decision into a <see cref="DecisionVector"/>.
    /// </summary>
    /// <param name="decision">The v2 engine's decision.</param>
    /// <returns>The equivalent, comparable decision vector.</returns>
    public static DecisionVector Project(PlaybackDecision decision)
    {
        var transformClasses = MapTransformClasses(decision.Transforms);
        var reasonCategories = new HashSet<ReasonCategory>();
        FoldReasonCategories(decision.Reasoning, reasonCategories);

        if (!decision.IsViable)
        {
            // PlaybackDecision.NotViable carries SelectedStreams.None and OutputSpec.Empty as
            // placeholder defaults (see PlaybackDecision.cs) - those are not real facts about the
            // world, just the shape an unbuilt decision takes. Projecting them as Unknown avoids
            // manufacturing a false "no stream selected"/"no output" divergence against whatever the
            // legacy side reports for the same failed attempt.
            return new DecisionVector(
                IsViable: false,
                Method: null,
                VideoStreamIndex: StreamSelection.Unknown,
                AudioStreamIndex: StreamSelection.Unknown,
                SubtitleStreamIndex: StreamSelection.Unknown,
                TransformClasses: transformClasses,
                ReasonCategories: reasonCategories,
                OutputContainer: null,
                OutputVideoCodec: null,
                OutputAudioCodec: null,
                SelectedSource: null,
                OutputWidth: null,
                OutputHeight: null,
                OutputBitrate: null,
                OutputVideoRange: null,
                OutputAudioChannels: null,
                SubtitleDeliveryMode: null,
                OutputSubtitleFormat: null);
        }

        var method = MapMethod(decision.Method);
        var subtitle = decision.SelectedStreams.Subtitle;

        // Unlike legacy, v2 always knows definitively whether a stream was selected: a null
        // Video/Audio index on a viable decision is a real "no stream" fact (e.g. audio-only
        // playback), not missing data. So these are None, not Unknown, when absent.
        var videoSelection = decision.SelectedStreams.Video is int videoIdx ? StreamSelection.Selected(videoIdx) : StreamSelection.None;
        var audioSelection = decision.SelectedStreams.Audio is int audioIdx ? StreamSelection.Selected(audioIdx) : StreamSelection.None;
        var subtitleSelection = subtitle is not null ? StreamSelection.Selected(subtitle.Index) : StreamSelection.None;

        return new DecisionVector(
            IsViable: true,
            Method: method,
            VideoStreamIndex: videoSelection,
            AudioStreamIndex: audioSelection,
            SubtitleStreamIndex: subtitleSelection,
            TransformClasses: transformClasses,
            ReasonCategories: reasonCategories,
            OutputContainer: decision.Output.Container,
            OutputVideoCodec: decision.Output.VideoCodec,
            OutputAudioCodec: decision.Output.AudioCodec,
            SelectedSource: decision.SelectedSource,
            OutputWidth: decision.Output.Resolution?.Width,
            OutputHeight: decision.Output.Resolution?.Height,
            // PR103: mirrors LegacyDecisionProjector's choice of the target VIDEO bitrate
            // specifically, not a total - see that projector's remarks on why TargetTotalBitrate is
            // avoided (it fabricates a known value out of an unknown half). OutputSpec.VideoBitrate
            // is the same "video-axis-only" ceiling, now genuinely populated instead of always null.
            OutputBitrate: decision.Output.VideoBitrate,
            OutputVideoRange: decision.Output.VideoRange,
            OutputAudioChannels: decision.Output.AudioChannels,
            SubtitleDeliveryMode: subtitle is not null ? MapDeliveryMode(subtitle.Delivery) : SubtitleDeliveryMode.None,
            OutputSubtitleFormat: decision.Output.SubtitleFormat);
    }

    private static SubtitleDeliveryMode MapDeliveryMode(SubtitleDeliveryMethod method) => method switch
    {
        SubtitleDeliveryMethod.Embed => SubtitleDeliveryMode.Embed,
        SubtitleDeliveryMethod.External => SubtitleDeliveryMode.External,
        SubtitleDeliveryMethod.Burn => SubtitleDeliveryMode.Burn,
        SubtitleDeliveryMethod.Hls => SubtitleDeliveryMode.Hls,
        _ => SubtitleDeliveryMode.None,
    };

    private static NormalizedMethod MapMethod(PlaybackMethod method) => method switch
    {
        PlaybackMethod.DirectPlay => NormalizedMethod.DirectPlay,
        PlaybackMethod.Remux => NormalizedMethod.Remux,
        PlaybackMethod.Transcode => NormalizedMethod.Transcode,
        _ => NormalizedMethod.Transcode,
    };

    private static HashSet<TransformClass> MapTransformClasses(IReadOnlyList<TransformKind> transforms)
    {
        var classes = new HashSet<TransformClass>();
        foreach (var transform in transforms)
        {
            var mapped = transform switch
            {
                TransformKind.RemuxContainer => TransformClass.Remux,
                TransformKind.TranscodeVideo => TransformClass.TranscodeVideo,
                TransformKind.TranscodeAudio => TransformClass.TranscodeAudio,
                TransformKind.Downmix => TransformClass.Downmix,
                TransformKind.Tonemap => TransformClass.Tonemap,
                TransformKind.BurnInSubtitle => TransformClass.BurnInSubtitle,
                TransformKind.ExtractSubtitle => TransformClass.ExtractSubtitle,
                TransformKind.ConvertSubtitle => TransformClass.ConvertSubtitle,

                // CopyVideo/CopyAudio carry no comparable legacy signal; omitted by design.
                TransformKind.CopyVideo => (TransformClass?)null,
                TransformKind.CopyAudio => (TransformClass?)null,
                _ => (TransformClass?)null,
            };

            if (mapped is not null)
            {
                classes.Add(mapped.Value);
            }
        }

        return classes;
    }

    /// <summary>
    /// Recursively flattens a <see cref="ReasonNode"/> tree, mapping each leaf's
    /// <see cref="ReasonCode"/> to its <see cref="ReasonCategory"/> via the shared
    /// <see cref="ReasonCategoryMap"/> and adding it to <paramref name="categories"/>. Positive/
    /// marker codes (<c>MethodChosen</c>, <c>StreamCopyable</c>, <c>SourceSelected</c>,
    /// <c>TonemapRequired</c>, <c>DownmixRequired</c>, <c>SubtitleBurnInRequired</c>,
    /// <c>SubtitleFormatConverted</c>) and
    /// <c>NoViablePlan</c> are not in the map and are excluded: viability is already captured by
    /// <see cref="DecisionVector.IsViable"/>. <c>SubtitleFormatConverted</c> in particular has
    /// deliberately no <see cref="ReasonCategory"/> entry (like <c>SubtitleBurnInRequired</c>), so it
    /// is silently excluded here rather than manufacturing a false v2-only-reason divergence against
    /// legacy, which emits no reason bit at all for a successful subtitle conversion (see
    /// <c>LegacyDecisionProjector</c> (removed, tesserafin#129 step 7)).
    /// </summary>
    private static void FoldReasonCategories(ReasonNode node, HashSet<ReasonCategory> categories)
    {
        var category = ReasonCategoryMap.MapByName(node.Code.ToString());
        if (category is not null)
        {
            categories.Add(category.Value);
        }

        foreach (var child in node.Children)
        {
            FoldReasonCategories(child, categories);
        }
    }
}
