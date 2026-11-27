using System.Collections.Generic;

namespace Tesserafin.Playback.Shadow;

/// <summary>
/// A playback decision reduced to a comparable vector of categories, per
/// docs/pr93-compatibility-lab.md §4: method, selected stream indices, the set of transform
/// classes, the set of reason categories, and the output shape (container/codecs/resolution/
/// bitrate/video range/audio channels/subtitle delivery). Produced from either side by
/// <c>LegacyDecisionProjector</c> (removed, tesserafin#129 step 7) or <see cref="V2DecisionProjector"/>, and compared by
/// <see cref="ShadowComparer"/> — never by raw equality of the underlying
/// <c>Tesserafin.Model.Dlna.StreamInfo</c> / <c>Tesserafin.Playback.Decision.PlaybackDecision</c> types.
/// </summary>
/// <remarks>
/// PR101 hardening: every "not known" value is tri-state-capable rather than a bare
/// <see langword="null"/>, because <see langword="null"/> used to conflate "unknown", "not
/// applicable", and "explicitly none" - which meant a legacy-selects-no-subtitle-vs-v2-selects-one
/// divergence could slip through unnoticed. <see cref="VideoStreamIndex"/>, <see cref="AudioStreamIndex"/>,
/// and <see cref="SubtitleStreamIndex"/> use <see cref="StreamSelection"/> to distinguish "unknown"
/// from "positively none" from "selected". The remaining nullable fields keep a plain
/// <see langword="null"/> for "unknown/not applicable" (there is no meaningful third state for a
/// resolution or a bitrate), and <see cref="ShadowComparer"/> only ever treats a divergence as real
/// when both sides carry a known, differing value.
/// </remarks>
/// <param name="IsViable">Whether a playable plan was produced.</param>
/// <param name="Method">The normalized method chosen, or <see langword="null"/> when <paramref name="IsViable"/> is <see langword="false"/>.</param>
/// <param name="VideoStreamIndex">The selected video stream, per <see cref="StreamSelection"/>.</param>
/// <param name="AudioStreamIndex">The selected audio stream, per <see cref="StreamSelection"/>.</param>
/// <param name="SubtitleStreamIndex">The selected subtitle stream, per <see cref="StreamSelection"/>.</param>
/// <param name="TransformClasses">The set of pipeline transformations this decision implies.</param>
/// <param name="ReasonCategories">The set of reason categories that led to this decision.</param>
/// <param name="OutputContainer">The output container, or <see langword="null"/> if not known/applicable.</param>
/// <param name="OutputVideoCodec">The output video codec, or <see langword="null"/> if not known/applicable.</param>
/// <param name="OutputAudioCodec">The output audio codec, or <see langword="null"/> if not known/applicable.</param>
/// <param name="SelectedSource">The identifier of the selected media source, or <see langword="null"/> if not known.</param>
/// <param name="OutputWidth">The output video width in pixels, or <see langword="null"/> if not known/applicable.</param>
/// <param name="OutputHeight">The output video height in pixels, or <see langword="null"/> if not known/applicable.</param>
/// <param name="OutputBitrate">The output (video) bitrate, or <see langword="null"/> if not known/applicable.</param>
/// <param name="OutputVideoRange">The normalized output video range (for example <c>"SDR"</c>, <c>"HDR10"</c>), or <see langword="null"/> if not known/applicable.</param>
/// <param name="OutputAudioChannels">The output audio channel count, or <see langword="null"/> if not known/applicable.</param>
/// <param name="SubtitleDeliveryMode">How the selected subtitle, if any, is delivered, or <see langword="null"/> if not known.</param>
/// <param name="OutputSubtitleFormat">The format the selected subtitle, if any, is actually delivered in, or <see langword="null"/> if not known/applicable.</param>
public sealed record DecisionVector(
    bool IsViable,
    NormalizedMethod? Method,
    StreamSelection VideoStreamIndex,
    StreamSelection AudioStreamIndex,
    StreamSelection SubtitleStreamIndex,
    IReadOnlySet<TransformClass> TransformClasses,
    IReadOnlySet<ReasonCategory> ReasonCategories,
    string? OutputContainer,
    string? OutputVideoCodec,
    string? OutputAudioCodec,
    string? SelectedSource,
    int? OutputWidth,
    int? OutputHeight,
    int? OutputBitrate,
    string? OutputVideoRange,
    int? OutputAudioChannels,
    SubtitleDeliveryMode? SubtitleDeliveryMode,
    string? OutputSubtitleFormat);
