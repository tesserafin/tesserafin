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
    /// The play sessions the fixture's sessions are created under, one each: a second session a
    /// user creates under the same play session REPLACES the first.
    /// </summary>
    public const string FilePlaySession = "r1-session-file";

    /// <summary>The progressive session's play session.</summary>
    public const string ProgressivePlaySession = "r1-session-progressive";

    /// <summary>The HLS session's play session.</summary>
    public const string HlsPlaySession = "r1-session-hls";

    /// <summary>
    /// Builds the catalogue for one seeded fixture.
    /// </summary>
    /// <param name="itemId">The seeded item.</param>
    /// <param name="mediaSourceId">That item's media source id.</param>
    /// <param name="subtitleStreamIndex">The index of the fixture's external subtitle stream.</param>
    /// <param name="sessions">The sessions the session routes are addressed through.</param>
    /// <returns>Every media route, bound to the fixture.</returns>
    public static IReadOnlyList<MediaRoute> For(Guid itemId, string mediaSourceId, int subtitleStreamIndex, MediaRouteSessions sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var item = itemId.ToString("D", CultureInfo.InvariantCulture);
        var subtitle = subtitleStreamIndex.ToString(CultureInfo.InvariantCulture);
        string Binding(string playSession) => $"itemId={item}&mediaSourceId={mediaSourceId}&playSessionId={playSession}";
        string Session(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

        return
        [
            // The session routes (tesserafin#129 step 7: a session plays from these alone). Each
            // names its item, media source and play session in the query, and each is checked
            // against its session's owner and retained plan after the capability.
            new("session file", "session file", PlaybackCapabilityScope.Media, "GET", $"/Playback/Sessions/{Session(sessions.File)}/File?{Binding(FilePlaySession)}", true, true, true, MediaRouteEvidence.Bytes, FilePlaySession),
            new("session file HEAD", "Range/HEAD", PlaybackCapabilityScope.Media, "HEAD", $"/Playback/Sessions/{Session(sessions.File)}/File?{Binding(FilePlaySession)}", true, true, true, MediaRouteEvidence.Entry, FilePlaySession),
            new("session progressive", "session progressive", PlaybackCapabilityScope.Media, "GET", $"/Playback/Sessions/{Session(sessions.Progressive)}/Progressive.ts?{Binding(ProgressivePlaySession)}", true, true, true, MediaRouteEvidence.Entry, ProgressivePlaySession),
            new("session hls playlist", "HLS variant", PlaybackCapabilityScope.Media, "GET", $"/Playback/Sessions/{Session(sessions.Hls)}/Hls/main.m3u8?{Binding(HlsPlaySession)}", true, true, true, MediaRouteEvidence.Entry, HlsPlaySession),
            new("session hls segment", "HLS segment", PlaybackCapabilityScope.Media, "GET", $"/Playback/Sessions/{Session(sessions.Hls)}/Hls/hls1/main/0.ts?runtimeTicks=0&actualSegmentLengthTicks=0&{Binding(HlsPlaySession)}", true, true, true, MediaRouteEvidence.Entry, HlsPlaySession),
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
