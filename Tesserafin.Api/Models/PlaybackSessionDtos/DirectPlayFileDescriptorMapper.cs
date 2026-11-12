using System;
using System.Globalization;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Net;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Execution;

namespace Tesserafin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// Mints the stream descriptor of a session the v2 engine alone decided to direct play
/// (<see cref="DirectPlayFilmPlanner"/>), from the retained execution plan only. No legacy
/// <c>StreamInfo</c> is built and no credential is written into the URLs: the client presents its
/// own <c>playbackCapability</c> (scope <c>Media</c> for the file, <c>Subtitles</c> for a sidecar).
/// </summary>
public static class DirectPlayFileDescriptorMapper
{
    /// <summary>
    /// Maps an engine-decided direct-play session to its descriptor.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="playSessionId">The session's play session id; the file route is bound to it.</param>
    /// <param name="itemId">The item the session plays.</param>
    /// <param name="plan">The retained execution plan; its method must be direct play.</param>
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
        if (plan.Method != PlaybackMethod.DirectPlay)
        {
            throw new ArgumentException("Only a direct-play plan has a file descriptor.", nameof(plan));
        }

        var item = itemId.ToString("N", CultureInfo.InvariantCulture);
        var source = Uri.EscapeDataString(plan.SourceId);
        var url = FormattableString.Invariant(
            $"/Playback/Sessions/{sessionId}/File?itemId={item}&mediaSourceId={source}&playSessionId={Uri.EscapeDataString(playSessionId ?? string.Empty)}");

        return new PlaybackSessionStreamDescriptor(
            url,
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
