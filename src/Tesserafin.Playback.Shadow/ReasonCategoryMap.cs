using System;
using System.Collections.Generic;

namespace Tesserafin.Playback.Shadow;

/// <summary>
/// The single source of truth folding leaf reason names — shared by legacy
/// <c>Tesserafin.Model.Session.TranscodeReason</c> flag names and v2
/// <c>Tesserafin.Playback.Decision.ReasonCode</c> member names, which mirror each other 1-1 — into a
/// <see cref="ReasonCategory"/>. Both <c>LegacyDecisionProjector</c> (removed, tesserafin#129 step 7) and
/// <see cref="V2DecisionProjector"/> drive their folding through this one map so the two sides can
/// never fold the same underlying reason into different categories. Names not present here (v2's
/// positive/marker codes: <c>StreamCopyable</c>, <c>SourceSelected</c>, <c>MethodChosen</c>,
/// <c>SubtitleBurnInRequired</c>, <c>SubtitleFormatConverted</c>, <c>DownmixRequired</c>,
/// <c>TonemapRequired</c>, and <c>NoViablePlan</c>) carry no comparable category and are excluded by
/// design.
/// </summary>
internal static class ReasonCategoryMap
{
    /// <summary>
    /// The reason name → category folding table. Keyed by the shared, mirrored enum member name
    /// (for example <c>"ContainerNotSupported"</c>), not by either enum type, so it applies equally
    /// to <c>TranscodeReason</c> flag names and <c>ReasonCode</c> member names.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, ReasonCategory> ByName = new Dictionary<string, ReasonCategory>(StringComparer.Ordinal)
    {
        ["ContainerNotSupported"] = ReasonCategory.Container,

        ["VideoCodecNotSupported"] = ReasonCategory.VideoCodec,
        ["VideoProfileNotSupported"] = ReasonCategory.VideoCodec,
        ["VideoLevelNotSupported"] = ReasonCategory.VideoCodec,
        ["VideoCodecTagNotSupported"] = ReasonCategory.VideoCodec,
        ["RefFramesNotSupported"] = ReasonCategory.VideoCodec,
        ["VideoRotationNotSupported"] = ReasonCategory.VideoCodec,
        ["AnamorphicVideoNotSupported"] = ReasonCategory.VideoCodec,
        ["InterlacedVideoNotSupported"] = ReasonCategory.VideoCodec,

        ["VideoRangeTypeNotSupported"] = ReasonCategory.VideoRange,

        ["VideoResolutionNotSupported"] = ReasonCategory.VideoDims,
        ["VideoBitDepthNotSupported"] = ReasonCategory.VideoDims,
        ["VideoFramerateNotSupported"] = ReasonCategory.VideoDims,

        ["AudioCodecNotSupported"] = ReasonCategory.AudioCodec,
        ["AudioProfileNotSupported"] = ReasonCategory.AudioCodec,

        ["AudioChannelsNotSupported"] = ReasonCategory.AudioChannels,

        ["AudioSampleRateNotSupported"] = ReasonCategory.AudioRate,
        ["AudioBitDepthNotSupported"] = ReasonCategory.AudioRate,

        ["VideoBitrateNotSupported"] = ReasonCategory.Bitrate,
        ["AudioBitrateNotSupported"] = ReasonCategory.Bitrate,
        ["ContainerBitrateExceedsLimit"] = ReasonCategory.Bitrate,

        ["SubtitleCodecNotSupported"] = ReasonCategory.Subtitle,

        ["SecondaryAudioNotSupported"] = ReasonCategory.StreamCount,
        ["StreamCountExceedsLimit"] = ReasonCategory.StreamCount,
        ["AudioIsExternal"] = ReasonCategory.StreamCount,

        ["UnknownVideoStreamInfo"] = ReasonCategory.Error,
        ["UnknownAudioStreamInfo"] = ReasonCategory.Error,
        ["DirectPlayError"] = ReasonCategory.Error,
    };

    /// <summary>
    /// Maps a reason name to its <see cref="ReasonCategory"/>, or <see langword="null"/> when the
    /// name has no comparable category (positive/marker codes, or an unrecognized name).
    /// </summary>
    /// <param name="name">The reason name, matching a <c>TranscodeReason</c> flag or <c>ReasonCode</c> member.</param>
    /// <returns>The folded category, or <see langword="null"/> if none applies.</returns>
    public static ReasonCategory? MapByName(string name) =>
        ByName.TryGetValue(name, out var category) ? category : null;
}
