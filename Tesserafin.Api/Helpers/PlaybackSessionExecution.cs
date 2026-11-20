using System;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Streaming;
using Tesserafin.Model.Dlna;

namespace Tesserafin.Api.Helpers;

/// <summary>
/// The one check every session output route makes before it serves or starts anything: the state
/// the executor resolved from a request built from the retained plan is that plan, and nothing else.
/// Shared by the session's HLS route and its progressive route.
/// </summary>
public static class PlaybackSessionExecution
{
    /// <summary>
    /// Whether the state the executor resolved is the plan and nothing else: the streams the plan
    /// selected, each copied exactly when the plan copies it, and each encoded stream encoded to
    /// the plan's codec; an audio plan is an audio output, with no video stream at all. The executor
    /// falls back to a first stream when an index no longer exists (a file replaced since planning,
    /// or, for an audio output, a plan that selected another than the source's default audio stream)
    /// and refuses a copy it cannot make; either is a different playback, not this plan.
    /// </summary>
    /// <param name="state">The state the executor resolved.</param>
    /// <param name="request">The request built from the retained plan.</param>
    /// <returns><see langword="true"/> if the state executes the plan.</returns>
    public static bool IsThePlan(StreamState state, StreamingRequestDto request)
        => state.VideoStream?.Index == request.VideoStreamIndex
            && state.AudioStream?.Index == request.AudioStreamIndex
            && (request is VideoRequestDto
                ? IsThePlannedCodec(state.OutputVideoCodec, request.AllowVideoStreamCopy, request.VideoCodec)
                : !state.IsOutputVideo && state.OutputVideoCodec is null)
            && (state.AudioStream is null || IsThePlannedCodec(state.OutputAudioCodec, request.AllowAudioStreamCopy, request.AudioCodec))
            && IsThePlannedSubtitle(state, request);

    /// <summary>
    /// A plan that burns a subtitle in is executed only with that stream overlaid on an encoded
    /// video; a plan that does not burns nothing in.
    /// </summary>
    private static bool IsThePlannedSubtitle(StreamState state, StreamingRequestDto request)
        => request.SubtitleMethod == SubtitleDeliveryMethod.Encode
            ? request.SubtitleStreamIndex is int index
                && state.SubtitleStream?.Index == index
                && state.SubtitleDeliveryMethod == SubtitleDeliveryMethod.Encode
                && !EncodingHelper.IsCopyCodec(state.OutputVideoCodec)
            : state.SubtitleDeliveryMethod != SubtitleDeliveryMethod.Encode || state.SubtitleStream is null;

    private static bool IsThePlannedCodec(string? outputCodec, bool planCopies, string? plannedCodec)
        => planCopies
            ? EncodingHelper.IsCopyCodec(outputCodec)
            : !EncodingHelper.IsCopyCodec(outputCodec) && string.Equals(outputCodec, plannedCodec, StringComparison.OrdinalIgnoreCase);
}
