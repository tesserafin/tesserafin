using System;
using Tesserafin.Api.Controllers;
using Xunit;

namespace Tesserafin.Api.Tests.Controllers;

/// <summary>
/// The live playlist of a Live TV session: every segment uri ffmpeg wrote is bound to the session,
/// tags and line endings are left as they are.
/// </summary>
public sealed class DynamicHlsLiveSessionPlaylistTests
{
    private static readonly Guid _item = Guid.Parse("0f1e2d3c4b5a69788796a5b4c3d2e1f0");

    [Fact]
    public void Segment_and_initialisation_uris_carry_the_session_binding_when_no_capability_is_propagated()
    {
        const string Playlist = "#EXTM3U\r\n#EXT-X-PLAYLIST-TYPE:EVENT\r\n#EXT-X-MAP:URI=\"hls/abc/abc-1.mp4\"\r\n#EXTINF:3.0,\r\nhls/abc/abc0.mp4\r\n#EXTINF:3.0,\r\nhls/abc/abc1.mp4\r\n";

        var bound = DynamicHlsController.BindLiveSegments(Playlist, _item, "src 1", "ps", capabilityPropagated: false);

        const string Binding = "itemId=0f1e2d3c4b5a69788796a5b4c3d2e1f0&mediaSourceId=src%201&playSessionId=ps";
        Assert.Equal(
            "#EXTM3U\r\n#EXT-X-PLAYLIST-TYPE:EVENT\r\n#EXT-X-MAP:URI=\"hls/abc/abc-1.mp4?" + Binding + "\"\r\n#EXTINF:3.0,\r\nhls/abc/abc0.mp4?" + Binding + "\r\n#EXTINF:3.0,\r\nhls/abc/abc1.mp4?" + Binding + "\r\n",
            bound);
    }

    [Fact]
    public void Only_the_item_is_added_when_the_capability_propagation_adds_the_rest()
    {
        var bound = DynamicHlsController.BindLiveSegments("#EXTM3U\nhls/abc/abc0.ts\n", _item, "src", "ps", capabilityPropagated: true);

        Assert.Equal("#EXTM3U\nhls/abc/abc0.ts?itemId=0f1e2d3c4b5a69788796a5b4c3d2e1f0\n", bound);
    }
}
