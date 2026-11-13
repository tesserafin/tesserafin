using System;
using System.Globalization;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Net;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Execution;

namespace Tesserafin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// Mints the stream descriptor of a session the v2 engine alone decided
/// (<see cref="EngineFilmPlanner"/>), from the retained execution plan only: the session's file
/// route for a direct play, the session's HLS route for a remux or transcode. No legacy <c>StreamInfo</c> is
/// built, no encoding parameter is written into the URL (the routes read them from the plan) and
/// no credential either: the client presents its own <c>playbackCapability</c> (scope
/// <c>Media</c> for the file or the HLS output, <c>Subtitles</c> for a sidecar).
/// </summary>
public static class EngineFilmDescriptorMapper
{
    /// <summary>
    /// Determines whether a retained plan is one this mapper has a descriptor for.
    /// </summary>
    /// <param name="plan">The retained execution plan.</param>
    /// <returns><see langword="true"/> for a direct play or an executable stream-copy remux.</returns>
    public static bool CanMap(PlaybackExecutionPlan? plan)
        => plan is { Method: PlaybackMethod.DirectPlay } || EngineFilmPlanner.IsExecutableHls(plan);

    /// <summary>
    /// Maps an engine-decided film session to its descriptor.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="playSessionId">The session's play session id; the file route is bound to it.</param>
    /// <param name="itemId">The item the session plays.</param>
    /// <param name="plan">The retained execution plan; see <see cref="CanMap"/>.</param>
    /// <param name="engineVersion">The engine version that decided the plan.</param>
    /// <returns>The descriptor.</returns>
    public static PlaybackSessionStreamDescriptor Map(
        PlaybackSessionId sessionId,
        string? playSessionId,
        Guid itemId,
        PlaybackExecutionPlan plan,
        int engineVersion)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!CanMap(plan))
        {
            throw new ArgumentException("Only a direct-play or an executable HLS plan has a session-owned descriptor.", nameof(plan));
        }

        var item = itemId.ToString("N", CultureInfo.InvariantCulture);
        var source = Uri.EscapeDataString(plan.SourceId);
        var binding = FormattableString.Invariant(
            $"itemId={item}&mediaSourceId={source}&playSessionId={Uri.EscapeDataString(playSessionId ?? string.Empty)}");

        if (plan.Method != PlaybackMethod.DirectPlay)
        {
            // Container is the segment container the client declared (fMP4 or MPEG-TS); the
            // resource itself is the media playlist.
            return new PlaybackSessionStreamDescriptor(
                FormattableString.Invariant($"/Playback/Sessions/{sessionId}/Hls/main.m3u8?{binding}"),
                StreamingProtocol.Hls,
                engineVersion,
                null,
                SubtitleUrl(item, source, plan),
                plan.Container,
                MimeTypes.GetMimeType("playlist.m3u8"));
        }

        return new PlaybackSessionStreamDescriptor(
            FormattableString.Invariant($"/Playback/Sessions/{sessionId}/File?{binding}"),
            StreamingProtocol.Http,
            engineVersion,
            null,
            SubtitleUrl(item, source, plan),
            plan.Container,
            string.IsNullOrEmpty(plan.Container) ? null : MimeTypes.GetMimeType("." + plan.Container, null));
    }

    private static string? SubtitleUrl(string item, string source, PlaybackExecutionPlan plan)
    {
        if (plan.SubtitleDelivery != SubtitleDeliveryMethod.External
            || plan.SubtitleStreamIndex is not int index
            || index < 0
            || string.IsNullOrEmpty(plan.SubtitleFormat))
        {
            return null;
        }

        return FormattableString.Invariant(
            $"/Videos/{item}/{source}/Subtitles/{index}/0/Stream.{Uri.EscapeDataString(plan.SubtitleFormat)}");
    }
}
