using System.Linq;
using Tesserafin.Data.Enums;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Net;

namespace Tesserafin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// Maps the <c>StreamInfo</c> <see cref="IPlaybackLiveStreamResolver.Resolve"/> actually resolved
/// (PR117, docs/pr116d-url-contract-design.md §2.2) into the client-facing
/// <see cref="PlaybackSessionStreamDescriptor"/>. Reads only what is already on the resolved
/// <see cref="StreamInfo"/> - never re-decides, never re-serializes via a new URL-building path
/// (design doc §1.3: the executable parity invariant requires reusing <c>StreamInfo.ToUrl</c>
/// verbatim). No credential is written into either URL: a descriptor names a resource, and the
/// client authenticates each request for it with its own session (an <c>Authorization</c> header)
/// or a <c>playbackCapability</c> it mints. This is the same contract as the session-owned routes
/// <see cref="EngineFilmDescriptorMapper"/> names.
/// </summary>
public static class PlaybackSessionStreamDescriptorMapper
{
    /// <summary>
    /// Builds the descriptor for a resolved stream.
    /// </summary>
    /// <param name="streamInfo">The <c>StreamInfo</c> that will actually be served, already re-stamped with the caller's own <c>PlaySessionId</c>/<c>StartPositionTicks</c>.</param>
    /// <param name="servedBy">The real engine version that produced <paramref name="streamInfo"/>, or <see cref="PlaybackSessionResponse.LegacyDecisionVersion"/> when it fell back to legacy.</param>
    /// <param name="fallbackReason">Why legacy was served instead of v2, or <see langword="null"/> when <paramref name="servedBy"/> is a real v2 version.</param>
    /// <param name="transcoderSupport">Used to resolve the external subtitle delivery URL, mirroring <c>MediaInfoHelper.SetDeviceSpecificSubtitleInfo</c>'s own resolution - never mutates <paramref name="streamInfo"/>'s media source.</param>
    /// <returns>The mapped descriptor.</returns>
    public static PlaybackSessionStreamDescriptor Map(
        StreamInfo streamInfo,
        int servedBy,
        PlaybackLiveFallbackReason? fallbackReason,
        ITranscoderSupport transcoderSupport)
    {
        return new PlaybackSessionStreamDescriptor(
            streamInfo.ToUrl(null, null, null),
            streamInfo.SubProtocol == MediaStreamProtocol.hls ? Tesserafin.Playback.Decision.StreamingProtocol.Hls : Tesserafin.Playback.Decision.StreamingProtocol.Http,
            servedBy,
            fallbackReason,
            ResolveSubtitleUrl(streamInfo, transcoderSupport),
            streamInfo.Container,
            ResolveMimeType(streamInfo));
    }

    /// <summary>
    /// Issue #44 §8 arbitrage A: the content type of what <c>StreamInfo.ToUrl</c> just addressed.
    /// Resolved from the SAME <see cref="StreamInfo.Container"/> the URL embeds, through the SAME
    /// <c>MimeTypes.GetMimeType</c> lookup the delivery endpoints use for their own
    /// <c>Content-Type</c> header (<c>VideosController.GetVideoStream</c> /
    /// <c>AudioHelper.GetAudioStream</c>: <c>GetMimeType("." + OutputContainer, false)</c>) - so the
    /// announced type cannot disagree with the response the URL will actually produce. Deliberately
    /// not "corrected" for audio sessions whose container maps to a <c>video/</c> type: the delivery
    /// endpoint really does send that header, and this descriptor reports what is served, not what
    /// would look tidier.
    /// </summary>
    private static string? ResolveMimeType(StreamInfo streamInfo)
    {
        // HLS addresses master.m3u8; Container is then the SEGMENT container (&SegmentContainer=),
        // so mapping it here would announce the segments' type for a playlist URL.
        if (streamInfo.SubProtocol == MediaStreamProtocol.hls)
        {
            return MimeTypes.GetMimeType("playlist.m3u8", null);
        }

        return string.IsNullOrEmpty(streamInfo.Container)
            ? null
            : MimeTypes.GetMimeType("." + streamInfo.Container, null);
    }

    /// <summary>
    /// Mirrors <c>MediaInfoHelper.SetDeviceSpecificSubtitleInfo</c>'s own condition for an external
    /// subtitle delivery URL (design doc §2.2: "présent uniquement quand ... Method == External"),
    /// read-only - the legacy method mutates a shared <c>MediaSourceInfo</c>, this one only reads
    /// <see cref="StreamInfo.GetSubtitleProfiles(ITranscoderSupport, bool, string, string)"/>'s own
    /// projection, since the descriptor never needs to mutate the caller's media source.
    /// </summary>
    private static string? ResolveSubtitleUrl(StreamInfo streamInfo, ITranscoderSupport transcoderSupport)
    {
        if (streamInfo.SubtitleStreamIndex is not int index || index < 0
            || streamInfo.SubtitleDeliveryMethod != SubtitleDeliveryMethod.External)
        {
            return null;
        }

        var profile = streamInfo
            .GetSubtitleProfiles(transcoderSupport, includeSelectedTrackOnly: true, baseUrl: "-", accessToken: null)
            .FirstOrDefault(p => p.Index == index && p.DeliveryMethod == SubtitleDeliveryMethod.External);

        return profile?.Url?.TrimStart('-');
    }
}
