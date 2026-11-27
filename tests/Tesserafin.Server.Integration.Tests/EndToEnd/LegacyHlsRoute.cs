using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// The existing chain's client-option HLS route (<c>/Videos/{id}/master.m3u8</c>), named the way a
/// client that builds its own URL names it. Nothing in the server hands this URL out any more
/// (PlaybackInfo is gone, tesserafin#129 step 7), but the route still answers until it is deleted
/// with the rest of that chain, so its isolation and its playlist contract stay under test.
/// </summary>
internal static partial class LegacyHlsRoute
{
    /// <summary>
    /// The master playlist URL of an H.264/AAC transcode of <paramref name="itemId"/>, under the
    /// device the client authenticates as.
    /// </summary>
    /// <param name="client">The caller, whose authorization header names its device.</param>
    /// <param name="itemId">The item to play; its single media source has the item's id.</param>
    /// <param name="playSessionId">The play session the URL names; a fresh one when omitted.</param>
    /// <returns>The relative master playlist URL.</returns>
    public static string MasterUrl(HttpClient client, Guid itemId, string? playSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        var header = client.DefaultRequestHeaders.TryGetValues(AuthHelper.AuthHeaderName, out var values) ? values.First() : string.Empty;
        var device = DeviceIdParameter().Match(header);
        if (!device.Success)
        {
            throw new InvalidOperationException("The client's authorization header names no device.");
        }

        var playSession = playSessionId ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        return FormattableString.Invariant(
            $"Videos/{itemId}/master.m3u8?static=false&videoCodec=h264&audioCodec=aac&videoBitRate=1500000&audioBitRate=128000&segmentContainer=ts&mediaSourceId={itemId:N}&deviceId={Uri.EscapeDataString(device.Groups["id"].Value)}&playSessionId={Uri.EscapeDataString(playSession)}");
    }

    [GeneratedRegex("DeviceId=\"(?<id>[^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceIdParameter();
}
