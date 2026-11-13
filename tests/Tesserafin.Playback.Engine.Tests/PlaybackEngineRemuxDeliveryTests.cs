using Tesserafin.Playback.Decision;
using Xunit;

namespace Tesserafin.Playback.Engine.Tests;

/// <summary>
/// Engine version 8: a remux is delivered through the first client-declared output profile that
/// can carry both copied streams - its container and its protocol - and falls back to a decode
/// container over plain HTTP only when no declared profile can.
/// </summary>
public static class PlaybackEngineRemuxDeliveryTests
{
    private static readonly PlaybackOutputProfile _hlsFmp4 = new(MediaKind.Video, StreamingProtocol.Hls, "mp4", ["h264"], ["aac"], null, null, null);
    private static readonly PlaybackOutputProfile _hlsTs = new(MediaKind.Video, StreamingProtocol.Hls, "ts", ["h264"], ["aac"], null, null, null);

    [Fact]
    public static void Remux_UsesTheDeclaredOutputProfile_ContainerAndProtocol()
    {
        var decision = Decide(Browser(supportsHls: true, _hlsFmp4, _hlsTs), "aac");

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.Remux, decision.Method);
        Assert.Equal("mp4", decision.Output.Container);
        Assert.Equal(StreamingProtocol.Hls, decision.Output.Protocol);
        Assert.Equal("h264", decision.Output.VideoCodec);
        Assert.Equal("aac", decision.Output.AudioCodec);
        Assert.Contains(TransformKind.RemuxContainer, decision.Transforms);
        Assert.Contains(TransformKind.CopyVideo, decision.Transforms);
        Assert.Contains(TransformKind.CopyAudio, decision.Transforms);
        Assert.DoesNotContain(TransformKind.TranscodeVideo, decision.Transforms);
        Assert.DoesNotContain(TransformKind.TranscodeAudio, decision.Transforms);
    }

    [Fact]
    public static void Remux_FollowsTheClientsOwnProfileOrder()
    {
        var decision = Decide(Browser(supportsHls: true, _hlsTs, _hlsFmp4), "aac");

        Assert.Equal(PlaybackMethod.Remux, decision.Method);
        Assert.Equal("ts", decision.Output.Container);
        Assert.Equal(StreamingProtocol.Hls, decision.Output.Protocol);
    }

    [Fact]
    public static void Remux_ProfileThatCannotCarryACopiedStream_IsNotUsed()
    {
        // The client decodes AC-3 in mp4, but its only output profile carries AAC: nothing is
        // encoded in a remux, so that profile cannot deliver this source.
        var decision = Decide(Browser(supportsHls: true, _hlsFmp4), "ac3");

        Assert.Equal(PlaybackMethod.Remux, decision.Method);
        Assert.Equal("mp4", decision.Output.Container);
        Assert.Equal(StreamingProtocol.Http, decision.Output.Protocol);
    }

    [Fact]
    public static void Remux_ProtocolTheClientDoesNotSupport_IsNotUsed()
    {
        var decision = Decide(Browser(supportsHls: false, _hlsFmp4), "aac");

        Assert.Equal(PlaybackMethod.Remux, decision.Method);
        Assert.Equal("mp4", decision.Output.Container);
        Assert.Equal(StreamingProtocol.Http, decision.Output.Protocol);
    }

    private static PlaybackDecision Decide(ClientCapabilities capabilities, string audioCodec)
        => new PlaybackEngine().Decide(
            EngineTestFixtures.Context(MediaKind.Video),
            capabilities,
            [
                EngineTestFixtures.Source(
                    "film",
                    "mkv",
                    videoStreams: [EngineTestFixtures.VideoStream(0, "h264")],
                    audioStreams: [EngineTestFixtures.AudioStream(1, audioCodec, isDefault: true, channels: 2)]),
            ],
            EngineTestFixtures.Constraints());

    private static ClientCapabilities Browser(bool supportsHls, params PlaybackOutputProfile[] outputProfiles)
        => new(
            Decode: new DecodeCapabilities(
                DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, ["mp4"], ["h264"], ["aac", "ac3"])],
                VideoCodecs: [new VideoCodecCapability("h264", [], null, null, [], null, null)],
                AudioCodecs: [new AudioCodecCapability("aac", null, null, null, null), new AudioCodecCapability("ac3", null, null, null, null)],
                SubtitleDelivery: [],
                SupportsHls: supportsHls,
                SupportsDash: false),
            OutputProfiles: outputProfiles);
}
