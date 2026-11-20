using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Tesserafin.Extensions.Json;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Dlna;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// Drives the existing chain through its remaining caller, <c>POST Items/{id}/PlaybackInfo</c>
/// (every client start that is not <c>Playback/Sessions</c>): a session no longer plans a transcode
/// the session journey does not execute (it refuses it), so that chain's HLS contracts are reached
/// here, with the client's capabilities translated to the <c>DeviceProfile</c> this endpoint takes.
/// </summary>
internal static partial class LegacyPlaybackInfo
{
    /// <summary>
    /// Asks <c>PlaybackInfo</c> for <paramref name="itemId"/> and returns the transcoding url of its
    /// first media source.
    /// </summary>
    /// <param name="client">The caller.</param>
    /// <param name="itemId">The item to play.</param>
    /// <param name="userId">The caller's user id.</param>
    /// <param name="preset">The capabilities and constraints to translate.</param>
    /// <param name="playSessionId">When set, the play session the url names, as a client chooses it on these routes.</param>
    /// <returns>The transcoding url.</returns>
    public static async Task<string> TranscodingUrlAsync(
        HttpClient client,
        Guid itemId,
        Guid userId,
        (ClientCapabilities Capabilities, PlaybackConstraints Constraints) preset,
        string? playSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        var (capabilities, constraints) = preset;
        var request = new JsonObject
        {
            ["UserId"] = userId,
            ["DeviceProfile"] = JsonSerializer.SerializeToNode(ReverseDlnaAdapter.ToDeviceProfile(capabilities), JsonDefaults.Options),
            ["EnableDirectPlay"] = constraints.AllowDirectPlay,
            ["EnableDirectStream"] = constraints.AllowDirectStream,
            ["EnableTranscoding"] = constraints.AllowTranscoding,
            ["AllowVideoStreamCopy"] = constraints.AllowVideoStreamCopy,
            ["AllowAudioStreamCopy"] = constraints.AllowAudioStreamCopy,
        };
        using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(FormattableString.Invariant($"Items/{itemId}/PlaybackInfo"), content, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, FormattableString.Invariant($"POST Items/{{id}}/PlaybackInfo: {(int)response.StatusCode} {body[..Math.Min(body.Length, 400)]}"));
        var url = JsonNode.Parse(body)!["MediaSources"]!.AsArray()[0]!["TranscodingUrl"]?.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(url), "PlaybackInfo named no transcoding url: " + body[..Math.Min(body.Length, 400)]);
        return playSessionId is null ? url! : PlaySessionParameter().Replace(url!, "${key}" + Uri.EscapeDataString(playSessionId));
    }

    [GeneratedRegex("(?<key>[?&]PlaySessionId=)[^&]*", RegexOptions.IgnoreCase)]
    private static partial Regex PlaySessionParameter();
}
