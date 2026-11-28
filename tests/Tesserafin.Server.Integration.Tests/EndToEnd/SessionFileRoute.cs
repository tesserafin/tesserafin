using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Tesserafin.Extensions.Json;
using Tesserafin.Playback.Decision;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// The bytes of a library item as a client reaches them now: a direct-play session's
/// <c>File</c> route (tesserafin#129 step 7: a session plays from its own routes alone).
/// </summary>
internal static class SessionFileRoute
{
    /// <summary>
    /// Opens a direct-play session for <paramref name="itemId"/> and names its <c>File</c> route.
    /// </summary>
    /// <param name="client">The caller.</param>
    /// <param name="userId">The caller's user id.</param>
    /// <param name="itemId">The item; its single media source has the item's id.</param>
    /// <param name="playSessionId">The play session to open it under.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The status the session was created with, and its <c>File</c> url when it was.</returns>
    public static async Task<(HttpStatusCode Status, string? Url)> OpenAsync(HttpClient client, Guid userId, Guid itemId, string playSessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var (capabilities, constraints) = EndToEndCapabilityPresets.DirectPlay();

        // An item with an external SRT track: the client renders it beside the stream, so the
        // session is not refused for a subtitle it cannot deliver (decided 2026-11-27).
        capabilities = capabilities with { Decode = capabilities.Decode with { SubtitleDelivery = [new SubtitleCapability("srt", SubtitleDeliveryMethod.External)] } };
        var request = new CreatePlaybackSessionRequest(itemId, userId, capabilities, constraints, MediaSourceId: null, PlaySessionId: playSessionId);
        using var response = await client.PostAsJsonAsync("Playback/Sessions", request, JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (response.StatusCode, null);
        }

        var session = await response.Content.ReadFromJsonAsync<PlaybackSessionResponse>(JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
        return (HttpStatusCode.OK, Url(session!.Id, itemId, playSessionId));
    }

    /// <summary>
    /// Names a session's <c>File</c> route.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="itemId">The item it plays.</param>
    /// <param name="playSessionId">Its play session.</param>
    /// <returns>The relative url.</returns>
    public static string Url(Guid sessionId, Guid itemId, string playSessionId)
        => FormattableString.Invariant($"Playback/Sessions/{sessionId:N}/File?itemId={itemId}&mediaSourceId={itemId:N}&playSessionId={Uri.EscapeDataString(playSessionId)}");

    /// <summary>
    /// A fresh play session id, as a client chooses one.
    /// </summary>
    /// <returns>The id.</returns>
    public static string NewPlaySession() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
}
