using System;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.MediaInfo;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Engine;
using Tesserafin.Playback.Execution;
using Xunit;
using PlayMethod = Tesserafin.Model.Session.PlayMethod;
using TranscodeReason = Tesserafin.Model.Session.TranscodeReason;

namespace Tesserafin.MediaEncoding.Tests.Playback;

/// <summary>
/// Consolidation step 1, transcode film (docs/playback-path-inventory.md §6): which transcode plans
/// the film planner owns, and how the executor's request is rendered from a retained plan - every
/// encoding input comes from the plan, none from a client.
/// </summary>
public sealed class EnginePlanEncodingRequestTests
{
    private static readonly PlaybackConstraints _allowed = new(
        AllowDirectPlay: true,
        AllowDirectStream: true,
        AllowTranscoding: true,
        AllowVideoStreamCopy: true,
        AllowAudioStreamCopy: true,
        MaxBitrate: null,
        MaxAudioChannels: null,
        PreferredAudioStreamIndex: null,
        PreferredSubtitleStreamIndex: null,
        SubtitleMode: SubtitlePlaybackMode.None,
        PreferredSubtitleLanguages: [],
        AlwaysBurnInSubtitleWhenTranscoding: false,
        StartTimeTicks: 0);

    private static readonly PlaybackExecutionPlan _audioOnly = new(
        Method: PlaybackMethod.Transcode,
        SourceId: "film",
        Container: "mp4",
        Protocol: StreamingProtocol.Hls,
        VideoStreamIndex: 0,
        VideoCodec: "h264",
        VideoBitrate: null,
        Resolution: null,
        VideoRange: null,
        AudioStreamIndex: 1,
        AudioCodec: "aac",
        AudioBitrate: null,
        AudioChannels: 2,
        TotalBitrate: null,
        SubtitleStreamIndex: null,
        SubtitleDelivery: null,
        SubtitleFormat: null,
        Transforms: [TransformKind.RemuxContainer, TransformKind.CopyVideo, TransformKind.TranscodeAudio]);

    private static PlaybackExecutionPlan VideoOnly => _audioOnly with
    {
        AudioChannels = null,
        Transforms = [TransformKind.RemuxContainer, TransformKind.TranscodeVideo, TransformKind.CopyAudio],
    };

    [Fact]
    public void A_transcode_of_one_stream_or_both_to_h264_and_aac_over_hls_is_executable()
    {
        Assert.True(EngineFilmPlanner.IsExecutableTranscode(_audioOnly));
        Assert.True(EngineFilmPlanner.IsExecutableTranscode(VideoOnly));
        Assert.True(EngineFilmPlanner.IsExecutableTranscode(_audioOnly with { Transforms = [TransformKind.TranscodeVideo, TransformKind.TranscodeAudio, TransformKind.Downmix] }));
        Assert.True(EngineFilmPlanner.IsExecutableTranscode(_audioOnly with { Container = "ts" }));
        Assert.True(EngineFilmPlanner.IsExecutableTranscode(_audioOnly with
        {
            SubtitleStreamIndex = 2,
            SubtitleDelivery = Tesserafin.Playback.Decision.SubtitleDeliveryMethod.External,
            SubtitleFormat = "vtt",
            Transforms = [.. _audioOnly.Transforms, TransformKind.ConvertSubtitle, TransformKind.ExtractSubtitle],
        }));
        Assert.True(EngineFilmPlanner.IsExecutableHlsOutput(_audioOnly));
        Assert.False(EngineFilmPlanner.IsExecutableRemux(_audioOnly));
    }

    [Fact]
    public void Tone_mapping_burn_in_another_target_codec_or_another_delivery_is_not_executable_from_here()
    {
        Assert.False(EngineFilmPlanner.IsExecutableTranscode(VideoOnly with { Transforms = [.. VideoOnly.Transforms, TransformKind.Tonemap] }));
        Assert.False(EngineFilmPlanner.IsExecutableTranscode(VideoOnly with { VideoRange = "SDR" }));
        Assert.False(EngineFilmPlanner.IsExecutableTranscode(VideoOnly with { Transforms = [.. VideoOnly.Transforms, TransformKind.BurnInSubtitle] }));
        Assert.False(EngineFilmPlanner.IsExecutableTranscode(VideoOnly with { SubtitleStreamIndex = 2, SubtitleDelivery = Tesserafin.Playback.Decision.SubtitleDeliveryMethod.Burn }));
        Assert.False(EngineFilmPlanner.IsExecutableTranscode(VideoOnly with { VideoCodec = "hevc" }));
        Assert.False(EngineFilmPlanner.IsExecutableTranscode(_audioOnly with { AudioCodec = "mp3" }));
        Assert.False(EngineFilmPlanner.IsExecutableTranscode(_audioOnly with { Protocol = StreamingProtocol.Http }));
        Assert.False(EngineFilmPlanner.IsExecutableTranscode(_audioOnly with { Container = "mkv" }));
        Assert.False(EngineFilmPlanner.IsExecutableTranscode(_audioOnly with { Method = PlaybackMethod.Remux }));
        Assert.False(EngineFilmPlanner.IsExecutableTranscode(_audioOnly with { Transforms = [TransformKind.RemuxContainer, TransformKind.CopyVideo, TransformKind.CopyAudio] }));
        Assert.False(EngineFilmPlanner.IsExecutableTranscode(_audioOnly with { VideoStreamIndex = null }));
        Assert.False(EngineFilmPlanner.IsExecutableTranscode(null));
    }

    [Fact]
    public void The_planner_owns_a_transcode_without_the_legacy_planner_and_keeps_no_stream_info()
    {
        var inner = new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict);
        var store = new InMemoryV2PlanStore();
        var planner = new EngineFilmPlanner(inner.Object, new PlaybackEngine(), store, NullLogger<EngineFilmPlanner>.Instance);
        var options = new MediaOptions
        {
            ItemId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            MediaSources = [Source("ac3")],
            Profile = new DeviceProfile(),
        };

        PlaybackPlan? plan;
        using (EngineFilmPlanner.BeginRequest(new EngineFilmRequest(Browser(), _allowed)))
        {
            plan = planner.PlanVideo(options);
        }

        Assert.NotNull(plan);
        Assert.Equal(PlayMethod.Transcode, plan.PlayMethod);
        Assert.Null(plan.StreamInfo);
        Assert.Equal(TranscodeReason.ContainerNotSupported | TranscodeReason.AudioCodecNotSupported, plan.TranscodeReasons);
        inner.VerifyNoOtherCalls();
    }

    [Fact]
    public void A_browser_that_lists_av1_and_hevc_first_still_gets_an_h264_transcode_and_keeps_its_hevc_remux()
    {
        var chrome = Browser() with
        {
            OutputProfiles = [new PlaybackOutputProfile(MediaKind.Video, StreamingProtocol.Hls, "mp4", ["av1", "hevc", "h264"], ["aac"], null, null, null)],
        };

        var executable = EngineFilmPlanner.WithExecutableTargetsFirst(chrome);

        Assert.Equal(["h264", "av1", "hevc"], executable.OutputProfiles[0].VideoCodecs);
        Assert.Equal(["aac"], executable.OutputProfiles[0].AudioCodecs);
        Assert.Equal(chrome.Decode, executable.Decode);

        var inner = new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict);
        var store = new InMemoryV2PlanStore();
        var planner = new EngineFilmPlanner(inner.Object, new PlaybackEngine(), store, NullLogger<EngineFilmPlanner>.Instance);
        var options = new MediaOptions
        {
            ItemId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            MediaSources = [Source("aac", videoCodec: "mpeg4")],
            Profile = new DeviceProfile(),
        };

        // MPEG-4 Part 2 is decoded by no browser: the video is encoded, to H.264, and the plan is
        // one the session's HLS route executes. Without the reordering it would be AV1, which it
        // does not - and the film would fall to the legacy planner (a strict mock here).
        using (EngineFilmPlanner.BeginRequest(new EngineFilmRequest(chrome, _allowed)))
        {
            Assert.Equal(PlayMethod.Transcode, planner.PlanVideo(options)!.PlayMethod);
        }

        inner.VerifyNoOtherCalls();
    }

    [Fact]
    public void Encoded_audio_takes_at_most_a_quarter_of_a_total_ceiling()
    {
        var plan = _audioOnly with { TotalBitrate = 200_000 };

        Assert.Equal(50_000, EnginePlanEncodingRequest.Build(plan, Guid.NewGuid(), "ps", "device", Source("ac3"), null).AudioBitRate);
        Assert.Equal(
            EnginePlanEncodingRequest.MinimumAudioBitrate,
            EnginePlanEncodingRequest.Build(plan with { TotalBitrate = 64_000 }, Guid.NewGuid(), "ps", "device", Source("ac3"), null).AudioBitRate);
    }

    [Fact]
    public void A_copied_stream_is_requested_as_a_copy_and_an_encoded_one_with_the_plans_codec()
    {
        var audio = EnginePlanEncodingRequest.Build(_audioOnly, Guid.NewGuid(), "ps", "device", Source("ac3"), "AudioCodecNotSupported");

        Assert.True(audio.BuiltFromRetainedPlan);
        Assert.Equal("film", audio.MediaSourceId);
        Assert.Equal("ps", audio.PlaySessionId);
        Assert.Equal("mp4", audio.SegmentContainer);
        Assert.Equal(0, audio.VideoStreamIndex);
        Assert.Equal(1, audio.AudioStreamIndex);
        Assert.True(audio.AllowVideoStreamCopy);
        Assert.False(audio.AllowAudioStreamCopy);
        Assert.Equal("aac", audio.AudioCodec);
        Assert.Equal(2, audio.TranscodingMaxAudioChannels);
        Assert.Equal(EnginePlanEncodingRequest.DefaultAudioBitratePerChannelPair, audio.AudioBitRate);
        Assert.Null(audio.VideoBitRate);
        Assert.Null(audio.MaxWidth);

        var video = EnginePlanEncodingRequest.Build(VideoOnly, Guid.NewGuid(), "ps", "device", Source("aac"), null);

        Assert.False(video.AllowVideoStreamCopy);
        Assert.True(video.AllowAudioStreamCopy);
        Assert.Equal("h264", video.VideoCodec);
        Assert.Null(video.AudioBitRate);
        Assert.Null(video.TranscodingMaxAudioChannels);
    }

    [Fact]
    public void Bitrate_and_resolution_ceilings_are_the_plans()
    {
        var plan = VideoOnly with { VideoBitrate = 4_000_000, TotalBitrate = 1_000_000, Resolution = new Resolution(1280, 720) };

        var request = EnginePlanEncodingRequest.Build(plan, Guid.NewGuid(), "ps", "device", Source("aac", audioBitrate: 192_000), null);

        // The copied audio's own bitrate is its share of the total; the video gets the rest, and
        // never more than its own ceiling.
        Assert.Equal(1_000_000 - 192_000, request.VideoBitRate);
        Assert.Equal(1280, request.MaxWidth);
        Assert.Equal(720, request.MaxHeight);

        var both = plan with
        {
            AudioBitrate = 96_000,
            VideoBitrate = 500_000,
            Transforms = [TransformKind.TranscodeVideo, TransformKind.TranscodeAudio],
        };
        var encoded = EnginePlanEncodingRequest.Build(both, Guid.NewGuid(), "ps", "device", Source("ac3"), null);
        Assert.Equal(96_000, encoded.AudioBitRate);
        Assert.Equal(500_000, encoded.VideoBitRate);

        var starved = EnginePlanEncodingRequest.Build(plan with { TotalBitrate = 150_000 }, Guid.NewGuid(), "ps", "device", Source("aac", audioBitrate: 192_000), null);
        Assert.Equal(EnginePlanEncodingRequest.MinimumVideoBitrate, starved.VideoBitRate);
    }

    [Fact]
    public void A_plan_the_session_route_does_not_execute_has_no_request()
    {
        Assert.Throws<ArgumentException>(() => EnginePlanEncodingRequest.Build(_audioOnly with { Protocol = StreamingProtocol.Http }, Guid.NewGuid(), "ps", "device", null, null));
        Assert.Throws<ArgumentException>(() => EnginePlanEncodingRequest.Build(_audioOnly with { Method = PlaybackMethod.DirectPlay, Transforms = [] }, Guid.NewGuid(), "ps", "device", null, null));
    }

    private static ClientCapabilities Browser()
        => new(
            new DecodeCapabilities(
                [new DecodeProfile(MediaKind.Video, ["mp4"], ["h264"], ["aac"])],
                [new VideoCodecCapability("h264", [], null, null, [], null, null)],
                [new AudioCodecCapability("aac", null, null, null, null)],
                [],
                true,
                false),
            [new PlaybackOutputProfile(MediaKind.Video, StreamingProtocol.Hls, "mp4", ["h264"], ["aac"], null, null, null)]);

    private static MediaSourceInfo Source(string audioCodec, int? audioBitrate = null, string videoCodec = "h264")
        => new()
        {
            Id = "film",
            Path = "film.mkv",
            Protocol = MediaProtocol.File,
            VideoType = VideoType.VideoFile,
            Container = "mkv",
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            SupportsDirectPlay = true,
            DefaultAudioStreamIndex = 1,
            MediaStreams =
            [
                new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = videoCodec },
                new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = audioCodec, Channels = 2, BitRate = audioBitrate, IsDefault = true },
            ],
        };
}
