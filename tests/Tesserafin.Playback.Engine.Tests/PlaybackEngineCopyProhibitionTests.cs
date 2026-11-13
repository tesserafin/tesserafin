using Tesserafin.Playback.Decision;
using Xunit;

namespace Tesserafin.Playback.Engine.Tests;

/// <summary>
/// Engine version 9: a stream copy the request forbids is re-encoded inside a transcode too, and a
/// demotion caused by such a prohibition re-encodes the forbidden axis only. This is how a caller
/// whose copied stream turned out not to be copyable asks for the same playback without that copy.
/// </summary>
public static class PlaybackEngineCopyProhibitionTests
{
    private static readonly PlaybackOutputProfile _hlsFmp4 = new(MediaKind.Video, StreamingProtocol.Hls, "mp4", ["h264"], ["aac"], null, null, null);

    [Fact]
    public static void AudioOnlyIncompatible_CopiesVideo_AndEncodesAudio()
    {
        var decision = Decide("h264", "dts", EngineTestFixtures.Constraints());

        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Equal(StreamingProtocol.Hls, decision.Output.Protocol);
        Assert.Equal("mp4", decision.Output.Container);
        Assert.Equal("h264", decision.Output.VideoCodec);
        Assert.Equal("aac", decision.Output.AudioCodec);
        Assert.Equal([TransformKind.RemuxContainer, TransformKind.CopyVideo, TransformKind.TranscodeAudio], decision.Transforms);
    }

    [Fact]
    public static void AudioOnlyIncompatible_VideoCopyForbidden_EncodesBoth()
    {
        var decision = Decide("h264", "dts", EngineTestFixtures.Constraints(allowVideoStreamCopy: false));

        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Equal("h264", decision.Output.VideoCodec);
        Assert.Equal([TransformKind.TranscodeVideo, TransformKind.TranscodeAudio], decision.Transforms);
    }

    [Fact]
    public static void VideoOnlyIncompatible_CopiesAudio_UnlessForbidden()
    {
        var copied = Decide("hevc", "aac", EngineTestFixtures.Constraints());
        Assert.Equal(PlaybackMethod.Transcode, copied.Method);
        Assert.Equal([TransformKind.RemuxContainer, TransformKind.TranscodeVideo, TransformKind.CopyAudio], copied.Transforms);

        var encoded = Decide("hevc", "aac", EngineTestFixtures.Constraints(allowAudioStreamCopy: false));
        Assert.Equal(PlaybackMethod.Transcode, encoded.Method);
        Assert.Equal("aac", encoded.Output.AudioCodec);
        Assert.Equal([TransformKind.TranscodeVideo, TransformKind.TranscodeAudio], encoded.Transforms);
    }

    [Fact]
    public static void Remux_VideoCopyForbidden_DemotesToATranscodeOfTheVideoOnly()
    {
        var decision = Decide("h264", "aac", EngineTestFixtures.Constraints(allowVideoStreamCopy: false));

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Equal(StreamingProtocol.Hls, decision.Output.Protocol);
        Assert.Equal([TransformKind.RemuxContainer, TransformKind.TranscodeVideo, TransformKind.CopyAudio], decision.Transforms);
    }

    [Fact]
    public static void Remux_AudioCopyForbidden_DemotesToATranscodeOfTheAudioOnly()
    {
        var decision = Decide("h264", "aac", EngineTestFixtures.Constraints(allowAudioStreamCopy: false));

        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Equal([TransformKind.RemuxContainer, TransformKind.CopyVideo, TransformKind.TranscodeAudio], decision.Transforms);
    }

    [Fact]
    public static void Remux_DirectStreamForbiddenAsSuch_StillEncodesBoth()
    {
        var decision = Decide("h264", "aac", EngineTestFixtures.Constraints(allowDirectStream: false));

        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Equal([TransformKind.TranscodeVideo, TransformKind.TranscodeAudio], decision.Transforms);
    }

    private static PlaybackDecision Decide(string videoCodec, string audioCodec, PlaybackConstraints constraints)
        => new PlaybackEngine().Decide(
            EngineTestFixtures.Context(MediaKind.Video),
            Browser(),
            [
                EngineTestFixtures.Source(
                    "film",
                    "mkv",
                    videoStreams: [EngineTestFixtures.VideoStream(0, videoCodec)],
                    audioStreams: [EngineTestFixtures.AudioStream(1, audioCodec, isDefault: true, channels: 2)]),
            ],
            constraints);

    private static ClientCapabilities Browser()
        => new(
            Decode: new DecodeCapabilities(
                DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, ["mp4"], ["h264"], ["aac"])],
                VideoCodecs: [new VideoCodecCapability("h264", [], null, null, [], null, null)],
                AudioCodecs: [new AudioCodecCapability("aac", null, null, null, null)],
                SubtitleDelivery: [],
                SupportsHls: true,
                SupportsDash: false),
            OutputProfiles: [_hlsFmp4]);
}
