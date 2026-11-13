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

namespace Tesserafin.MediaEncoding.Tests.Playback;

/// <summary>
/// #404 review P1: the user's own track preferences, resolved onto the source by
/// <c>MediaSourceManager</c>, fill whatever the client did not choose; an explicit choice still wins.
/// </summary>
public sealed class LocalVideoPlannerTests
{
    private static readonly PlaybackConstraints _declared = new(
        AllowDirectPlay: true,
        AllowDirectStream: true,
        AllowTranscoding: true,
        AllowVideoStreamCopy: true,
        AllowAudioStreamCopy: true,
        MaxBitrate: null,
        MaxAudioChannels: null,
        PreferredAudioStreamIndex: null,
        PreferredSubtitleStreamIndex: null,
        SubtitleMode: SubtitlePlaybackMode.Default,
        PreferredSubtitleLanguages: [],
        AlwaysBurnInSubtitleWhenTranscoding: false,
        StartTimeTicks: 0);

    [Fact]
    public void User_defaults_fill_the_tracks_the_client_left_open()
    {
        var source = new MediaSourceInfo { DefaultAudioStreamIndex = 3, DefaultSubtitleStreamIndex = -1 };

        var constraints = LocalVideoPlanner.WithUserDefaults(_declared, source);

        Assert.Equal(3, constraints.PreferredAudioStreamIndex);
        Assert.Equal(-1, constraints.PreferredSubtitleStreamIndex);
    }

    [Fact]
    public void A_user_with_no_default_subtitle_gets_none_whatever_mode_the_client_declared()
    {
        var source = new MediaSourceInfo { DefaultSubtitleStreamIndex = null };

        Assert.Equal(-1, LocalVideoPlanner.WithUserDefaults(_declared, source).PreferredSubtitleStreamIndex);
    }

    [Fact]
    public void An_explicit_client_choice_wins_over_the_user_default()
    {
        var source = new MediaSourceInfo { DefaultAudioStreamIndex = 3, DefaultSubtitleStreamIndex = -1 };

        var constraints = LocalVideoPlanner.WithUserDefaults(
            _declared with { PreferredAudioStreamIndex = 1, PreferredSubtitleStreamIndex = 4 },
            source);

        Assert.Equal(1, constraints.PreferredAudioStreamIndex);
        Assert.Equal(4, constraints.PreferredSubtitleStreamIndex);
    }

    [Fact]
    public void A_later_direct_play_source_uses_its_own_user_defaults()
    {
        var capabilities = new ClientCapabilities(
            new DecodeCapabilities(
                [new DecodeProfile(MediaKind.Video, ["mp4"], ["h264"], ["aac"])],
                [new VideoCodecCapability("h264", [], null, null, [], null, null)],
                [new AudioCodecCapability("aac", null, null, null, null)],
                [new SubtitleCapability("srt", Tesserafin.Playback.Decision.SubtitleDeliveryMethod.External)],
                false,
                false),
            []);
        var first = Source("first", "unsupported", 1, 3);
        var second = Source("second", "h264", 2, 4);
        var options = new MediaOptions
        {
            ItemId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            MediaSources = [first, second],
            Profile = new DeviceProfile(),
        };
        var store = new InMemoryV2PlanStore();
        var planner = new LocalVideoPlanner(
            new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object,
            new PlaybackEngine(),
            store,
            NullLogger<LocalVideoPlanner>.Instance);

        using var capture = store.BeginCapture();
        using var request = LocalVideoPlanner.BeginRequest(new LocalVideoRequest(capabilities, _declared));
        var plan = planner.PlanVideo(options);
        var record = store.TakeCaptured();

        Assert.NotNull(plan);
        Assert.NotNull(record);
        Assert.Equal("second", record.Decision.SelectedSource);
        Assert.Equal(2, record.Decision.SelectedStreams.Audio);
        Assert.Equal(4, record.Decision.SelectedStreams.Subtitle?.Index);
    }

    [Fact]
    public void A_remux_the_client_declared_an_hls_output_for_is_owned_without_the_legacy_planner()
    {
        var (planner, store) = Planner(new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object);

        using var capture = store.BeginCapture();
        using var request = LocalVideoPlanner.BeginRequest(new LocalVideoRequest(Browser(hlsOutput: true), _declared));
        var plan = planner.PlanVideo(Options(Source("film", "h264", 1, 2, "mkv")));
        var record = store.TakeCaptured();

        Assert.NotNull(plan);
        Assert.Equal(PlayMethod.DirectStream, plan.PlayMethod);
        Assert.Null(plan.StreamInfo);
        Assert.NotNull(record?.ExecutionPlan);
        Assert.Equal(PlaybackMethod.Remux, record.ExecutionPlan.Method);
        Assert.Equal(StreamingProtocol.Hls, record.ExecutionPlan.Protocol);
        Assert.Equal("mp4", record.ExecutionPlan.Container);
        Assert.Equal(1, record.ExecutionPlan.AudioStreamIndex);
        Assert.True(LocalVideoPlanner.IsExecutableRemux(record.ExecutionPlan));
    }

    [Fact]
    public void A_direct_play_version_is_preferred_over_an_earlier_version_that_needs_a_remux()
    {
        var (planner, store) = Planner(new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object);

        using var capture = store.BeginCapture();
        using var request = LocalVideoPlanner.BeginRequest(new LocalVideoRequest(Browser(hlsOutput: true), _declared));
        var plan = planner.PlanVideo(Options(Source("matroska", "h264", 1, 3, "mkv"), Source("mp4", "h264", 2, 4)));
        var record = store.TakeCaptured();

        Assert.Equal(PlayMethod.DirectPlay, plan?.PlayMethod);
        Assert.Equal("mp4", record?.Decision.SelectedSource);
        Assert.Equal(2, record?.Decision.SelectedStreams.Audio);
    }

    [Fact]
    public void A_remux_with_no_declared_hls_output_is_left_to_the_existing_chain()
    {
        var existing = new PlaybackPlan(PlayMethod.DirectStream, 0);
        var inner = new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict);
        inner.Setup(p => p.PlanVideo(It.IsAny<MediaOptions>())).Returns(existing);
        var (planner, store) = Planner(inner.Object);

        using var capture = store.BeginCapture();
        using var request = LocalVideoPlanner.BeginRequest(new LocalVideoRequest(Browser(hlsOutput: false), _declared));
        var plan = planner.PlanVideo(Options(Source("film", "h264", 1, 2, "mkv")));

        Assert.Same(existing, plan);
        Assert.Null(store.TakeCaptured());
    }

    [Theory]
    [InlineData(TransformKind.TranscodeAudio)]
    [InlineData(TransformKind.TranscodeVideo)]
    [InlineData(TransformKind.Downmix)]
    [InlineData(TransformKind.Tonemap)]
    [InlineData(TransformKind.BurnInSubtitle)]
    public void A_plan_that_would_encode_is_not_an_executable_remux(TransformKind encoding)
    {
        var copy = new PlaybackExecutionPlan(
            Method: PlaybackMethod.Remux,
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
            AudioChannels: null,
            TotalBitrate: null,
            SubtitleStreamIndex: null,
            SubtitleDelivery: null,
            SubtitleFormat: null,
            Transforms: [TransformKind.RemuxContainer, TransformKind.CopyVideo, TransformKind.CopyAudio]);

        Assert.True(LocalVideoPlanner.IsExecutableRemux(copy));
        Assert.False(LocalVideoPlanner.IsExecutableRemux(copy with { Transforms = [.. copy.Transforms, encoding] }));
        Assert.False(LocalVideoPlanner.IsExecutableRemux(copy with { Protocol = StreamingProtocol.Http }));
        Assert.False(LocalVideoPlanner.IsExecutableRemux(copy with { Container = "mkv" }));
        Assert.False(LocalVideoPlanner.IsExecutableRemux(copy with { Method = PlaybackMethod.Transcode }));
        Assert.False(LocalVideoPlanner.IsExecutableRemux(copy with { SubtitleStreamIndex = 2, SubtitleDelivery = Tesserafin.Playback.Decision.SubtitleDeliveryMethod.Burn }));
        Assert.False(LocalVideoPlanner.IsExecutableRemux(null));
    }

    [Theory]
    [InlineData("mpeg4", "aac", TransformKind.TranscodeVideo, TransformKind.CopyAudio)]
    [InlineData("h264", "ac3", TransformKind.CopyVideo, TransformKind.TranscodeAudio)]
    [InlineData("mpeg4", "ac3", TransformKind.TranscodeVideo, TransformKind.TranscodeAudio)]
    public void Sdr_hls_transcoding_is_owned_without_the_legacy_planner(string videoCodec, string audioCodec, TransformKind videoTransform, TransformKind audioTransform)
    {
        var (planner, store) = Planner(new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object);
        var source = Source("film", videoCodec, 1, 2, "mkv");
        source.MediaStreams[1].Codec = audioCodec;
        using var capture = store.BeginCapture();
        using var request = LocalVideoPlanner.BeginRequest(new LocalVideoRequest(Browser(hlsOutput: true), _declared));
        var planned = planner.PlanVideo(Options(source));
        var record = store.TakeCaptured();

        Assert.Equal(PlayMethod.Transcode, planned?.PlayMethod);
        Assert.Null(planned?.StreamInfo);
        Assert.NotNull(record?.ExecutionPlan);
        Assert.Equal(PlaybackMethod.Transcode, record.ExecutionPlan.Method);
        Assert.Equal("h264", record.ExecutionPlan.VideoCodec);
        Assert.Equal("aac", record.ExecutionPlan.AudioCodec);
        Assert.Contains(videoTransform, record.ExecutionPlan.Transforms);
        Assert.Contains(audioTransform, record.ExecutionPlan.Transforms);
        Assert.True(LocalVideoPlanner.IsExecutableHls(record.ExecutionPlan));
    }

    private static (LocalVideoPlanner Planner, InMemoryV2PlanStore Store) Planner(IPlaybackSessionPlanner inner)
    {
        var store = new InMemoryV2PlanStore();
        return (new LocalVideoPlanner(inner, new PlaybackEngine(), store, NullLogger<LocalVideoPlanner>.Instance), store);
    }

    private static MediaOptions Options(params MediaSourceInfo[] sources)
        => new()
        {
            ItemId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            MediaSources = sources,
            Profile = new DeviceProfile(),
        };

    private static ClientCapabilities Browser(bool hlsOutput)
        => new(
            new DecodeCapabilities(
                [new DecodeProfile(MediaKind.Video, ["mp4"], ["h264"], ["aac"])],
                [new VideoCodecCapability("h264", [], null, null, [], null, null)],
                [new AudioCodecCapability("aac", null, null, null, null)],
                [new SubtitleCapability("srt", Tesserafin.Playback.Decision.SubtitleDeliveryMethod.External)],
                true,
                false),
            hlsOutput ? [new PlaybackOutputProfile(MediaKind.Video, StreamingProtocol.Hls, "mp4", ["h264"], ["aac"], null, null, null)] : []);

    private static MediaSourceInfo Source(string id, string videoCodec, int audioIndex, int subtitleIndex, string container = "mp4")
        => new()
        {
            Id = id,
            Path = id + "." + container,
            Protocol = MediaProtocol.File,
            VideoType = VideoType.VideoFile,
            Container = container,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            SupportsDirectPlay = true,
            DefaultAudioStreamIndex = audioIndex,
            DefaultSubtitleStreamIndex = subtitleIndex,
            MediaStreams =
            [
                new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = videoCodec },
                new MediaStream { Index = audioIndex, Type = MediaStreamType.Audio, Codec = "aac", IsDefault = true },
                new MediaStream { Index = subtitleIndex, Type = MediaStreamType.Subtitle, Codec = "srt", IsExternal = true },
            ],
        };
}
