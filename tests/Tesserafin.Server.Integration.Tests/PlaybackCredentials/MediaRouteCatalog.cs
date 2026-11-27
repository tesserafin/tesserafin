using System;
using System.Collections.Generic;
using System.Globalization;
using Tesserafin.Controller.Net.PlaybackCredentials;

namespace Tesserafin.Server.Integration.Tests.PlaybackCredentials;

/// <summary>
/// Every media route the playback capability is supposed to reach, and nothing else.
/// </summary>
/// <remarks>
/// This list is the request-level counterpart of <c>ci/credential-exposure-inventory.py</c> and of
/// the endpoint-metadata sweep in <c>MediaRouteMetadataTests</c>. The three have to agree: the
/// inventory names the routes, the metadata sweep proves the route table carries the right
/// attributes, and this catalogue drives real HTTP requests against each one.
/// </remarks>
public static class MediaRouteCatalog
{
    /// <summary>
    /// Builds the catalogue for one seeded fixture.
    /// </summary>
    /// <param name="itemId">The seeded item.</param>
    /// <param name="mediaSourceId">That item's media source id.</param>
    /// <param name="subtitleStreamIndex">The index of the fixture's external subtitle stream.</param>
    /// <returns>Every media route, bound to the fixture.</returns>
    public static IReadOnlyList<MediaRoute> For(Guid itemId, string mediaSourceId, int subtitleStreamIndex)
    {
        var item = itemId.ToString("D", CultureInfo.InvariantCulture);
        var subtitle = subtitleStreamIndex.ToString(CultureInfo.InvariantCulture);

        return
        [
            new("direct video", "direct video", PlaybackCapabilityScope.Media, "GET", $"/Videos/{item}/stream?static=true&mediaSourceId={mediaSourceId}", true, true, true, MediaRouteEvidence.Bytes),
            new("direct video by container", "direct video", PlaybackCapabilityScope.Media, "GET", $"/Videos/{item}/stream.mp4?static=true&mediaSourceId={mediaSourceId}", true, true, true, MediaRouteEvidence.Bytes),
            new("direct video HEAD", "Range/HEAD", PlaybackCapabilityScope.Media, "HEAD", $"/Videos/{item}/stream?static=true&mediaSourceId={mediaSourceId}", true, true, true, MediaRouteEvidence.Entry),
            new("direct audio", "direct audio", PlaybackCapabilityScope.Media, "GET", $"/Audio/{item}/stream?static=true&mediaSourceId={mediaSourceId}", true, true, true, MediaRouteEvidence.Bytes),
            new("direct audio by container", "direct audio", PlaybackCapabilityScope.Media, "GET", $"/Audio/{item}/stream.mp4?static=true&mediaSourceId={mediaSourceId}", true, true, true, MediaRouteEvidence.Bytes),
            new("hls master video", "HLS master", PlaybackCapabilityScope.Media, "GET", $"/Videos/{item}/master.m3u8?mediaSourceId={mediaSourceId}", true, true, true, MediaRouteEvidence.Entry),
            new("hls master audio", "HLS master", PlaybackCapabilityScope.Media, "GET", $"/Audio/{item}/master.m3u8?mediaSourceId={mediaSourceId}", true, true, true, MediaRouteEvidence.Entry),
            new("hls variant video", "HLS variant", PlaybackCapabilityScope.Media, "GET", $"/Videos/{item}/main.m3u8?mediaSourceId={mediaSourceId}", true, true, true, MediaRouteEvidence.Entry),
            new("hls variant audio", "HLS variant", PlaybackCapabilityScope.Media, "GET", $"/Audio/{item}/main.m3u8?mediaSourceId={mediaSourceId}", true, true, true, MediaRouteEvidence.Entry),
            new("hls live", "HLS variant", PlaybackCapabilityScope.Media, "GET", $"/Videos/{item}/live.m3u8?mediaSourceId={mediaSourceId}", true, true, true, MediaRouteEvidence.Entry),
            new("hls segment video", "HLS segment", PlaybackCapabilityScope.Media, "GET", $"/Videos/{item}/hls1/main/0.mp4?mediaSourceId={mediaSourceId}", true, true, true, MediaRouteEvidence.Entry),
            new("hls segment audio", "HLS segment", PlaybackCapabilityScope.Media, "GET", $"/Audio/{item}/hls1/main/0.mp4?mediaSourceId={mediaSourceId}", true, true, true, MediaRouteEvidence.Entry),
            new("hls legacy playlist", "HLS variant", PlaybackCapabilityScope.Media, "GET", $"/Videos/{item}/hls/main/stream.m3u8?mediaSourceId={mediaSourceId}", true, true, false, MediaRouteEvidence.Entry),
            // #153-LTV-S1: the video segment route now reads `mediaSourceId` from the request, as
            // the legacy playlist route above already did. The two audio segment routes below still
            // name none, so the "bound capability on a route that names no media source" property
            // keeps its witnesses.
            // #153-LTV-R1: and the play session, which was not named at all.
            new("hls legacy segment", "HLS segment", PlaybackCapabilityScope.Media, "GET", $"/Videos/{item}/hls/main/0.ts?mediaSourceId={mediaSourceId}", true, true, true, MediaRouteEvidence.Entry),
            new("hls legacy audio mp3", "HLS segment", PlaybackCapabilityScope.Media, "GET", $"/Audio/{item}/hls/seg/stream.mp3", true, false, false, MediaRouteEvidence.Entry),
            new("hls legacy audio aac", "HLS segment", PlaybackCapabilityScope.Media, "GET", $"/Audio/{item}/hls/seg/stream.aac", true, false, false, MediaRouteEvidence.Entry),
            new("subtitle stream", "subtitle", PlaybackCapabilityScope.Subtitles, "GET", $"/Videos/{item}/{mediaSourceId}/Subtitles/{subtitle}/Stream.vtt", true, true, false, MediaRouteEvidence.Bytes),
            new("subtitle stream from position", "subtitle", PlaybackCapabilityScope.Subtitles, "GET", $"/Videos/{item}/{mediaSourceId}/Subtitles/{subtitle}/0/Stream.vtt", true, true, false, MediaRouteEvidence.Bytes),
            new("subtitle playlist", "subtitle", PlaybackCapabilityScope.Subtitles, "GET", $"/Videos/{item}/{mediaSourceId}/Subtitles/{subtitle}/subtitles.m3u8?segmentLength=10", true, true, false, MediaRouteEvidence.Entry),
            new("fallback font list", "font", PlaybackCapabilityScope.Fonts, "GET", "/FallbackFont/Fonts", false, false, false, MediaRouteEvidence.Entry),
            new("fallback font file", "font", PlaybackCapabilityScope.Fonts, "GET", "/FallbackFont/Fonts/x.ttf", false, false, false, MediaRouteEvidence.Entry),
            new("attachment", "attachment", PlaybackCapabilityScope.Attachments, "GET", $"/Videos/{item}/{mediaSourceId}/Attachments/0", true, true, false, MediaRouteEvidence.Entry),
            new("trickplay tiles", "trickplay", PlaybackCapabilityScope.Trickplay, "GET", $"/Videos/{item}/Trickplay/320/tiles.m3u8?mediaSourceId={mediaSourceId}", true, true, false, MediaRouteEvidence.Entry),
            new("trickplay image", "trickplay", PlaybackCapabilityScope.Trickplay, "GET", $"/Videos/{item}/Trickplay/320/0.jpg?mediaSourceId={mediaSourceId}", true, true, false, MediaRouteEvidence.Entry),
        ];
    }
}
