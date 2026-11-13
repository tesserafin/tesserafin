using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Playback.Decision;

namespace Tesserafin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// PR117 (docs/pr116d-url-contract-design.md §2.2): the companion, client-facing descriptor of the
/// executable URL for a session already planned via <c>Playback/Sessions</c> - returned only from
/// <c>GET Playback/Sessions/{id}/Stream</c>, never composed into <see cref="PlaybackSessionListItem"/>
/// or <see cref="PlaybackDiagnosticDetail"/> (design doc §1.4: those two reach the admin-elevated,
/// cross-user diagnostics surface). Neither URL carries a credential: the client authenticates
/// each request for the resource with its own session or a playback capability.
/// </summary>
/// <param name="Url">
/// The executable URL, resolved at call time - without any credential,
/// relative (the client already knows its own <c>baseUrl</c>, same posture as the legacy
/// <c>TranscodingUrl</c>).
/// </param>
/// <param name="Protocol">Same vocabulary as <see cref="OutputSpec.Protocol"/> (<see cref="StreamingProtocol.Hls"/>/<see cref="StreamingProtocol.Http"/>).</param>
/// <param name="ServedBy">
/// The version of the engine that actually produced <see cref="Url"/>, resolved at THIS call - same
/// sentinel as <see cref="PlaybackSessionResponse.LegacyDecisionVersion"/>, but deliberately distinct
/// from the <c>DecisionVersion</c> the originating <c>POST</c>/<c>PUT</c> announced (design doc §3.1:
/// the two can diverge, this field is authoritative for what is actually served).
/// </param>
/// <param name="FallbackReason">
/// Why legacy was served instead of v2, or <see langword="null"/> when <see cref="ServedBy"/> is a
/// real v2 engine version. Same restricted enum already surfaced on the admin diagnostics detail
/// route (design doc §2.2/§4).
/// </param>
/// <param name="SubtitleUrl">
/// The external subtitle delivery URL, present only when the served stream's selected subtitle
/// delivery method is <see cref="SubtitleDeliveryMethod.External"/> - <see langword="null"/>
/// otherwise (design doc §2.2).
/// </param>
/// <param name="Container">
/// LANE S / issue #44 §8 arbitrage A: the EFFECTIVE output container of the stream
/// <see cref="Url"/> resolves to - the very value the URL itself carries
/// (<c>/stream.{container}</c> on <see cref="StreamingProtocol.Http"/>,
/// <c>&amp;SegmentContainer={container}</c> on <see cref="StreamingProtocol.Hls"/>, where it is
/// therefore the SEGMENT container, not <c>m3u8</c>). Until this field existed the server picked a
/// container and reported it nowhere, so a client had no way to name what it was about to play
/// without borrowing the legacy <c>TranscodingContainer</c> - which is exactly why reefin-web #24
/// had to fall the whole remux/non-HLS-transcode decision back to legacy. <see langword="null"/>
/// only when the served stream carries no container at all.
/// </param>
/// <param name="MimeType">
/// The content type a client should announce to its player for <see cref="Url"/>, resolved from
/// <see cref="Container"/> exactly the way the delivery endpoints themselves resolve their own
/// <c>Content-Type</c> header - <c>application/vnd.apple.mpegurl</c> on
/// <see cref="StreamingProtocol.Hls"/> (the playlist's type, not the segments'), otherwise
/// <c>MimeTypes.GetMimeType("." + Container)</c>, the same call
/// <c>VideosController</c>/<c>AudioHelper</c> make on the container they actually write. Reported
/// rather than re-derived client-side so the announced type can never disagree with the response
/// the URL will really produce. <see langword="null"/> when no container is known, or when no
/// mapping exists for it.
/// </param>
public sealed record PlaybackSessionStreamDescriptor(
    string Url,
    StreamingProtocol Protocol,
    int ServedBy,
    PlaybackLiveFallbackReason? FallbackReason,
    string? SubtitleUrl,
    string? Container,
    string? MimeType);
