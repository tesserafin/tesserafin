using System;
using System.Linq;
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
public sealed class LocalMediaPlannerTests
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

        var constraints = LocalMediaPlanner.WithUserDefaults(_declared, source);

        Assert.Equal(3, constraints.PreferredAudioStreamIndex);
        Assert.Equal(-1, constraints.PreferredSubtitleStreamIndex);
    }

    [Fact]
    public void A_user_with_no_default_subtitle_gets_none_whatever_mode_the_client_declared()
    {
        var source = new MediaSourceInfo { DefaultSubtitleStreamIndex = null };

        Assert.Equal(-1, LocalMediaPlanner.WithUserDefaults(_declared, source).PreferredSubtitleStreamIndex);
    }

    [Fact]
    public void An_explicit_client_choice_wins_over_the_user_default()
    {
        var source = new MediaSourceInfo { DefaultAudioStreamIndex = 3, DefaultSubtitleStreamIndex = -1 };

        var constraints = LocalMediaPlanner.WithUserDefaults(
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
        var planner = new LocalMediaPlanner(
            new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object,
            new PlaybackEngine(),
            store,
            NullLogger<LocalMediaPlanner>.Instance);

        using var capture = store.BeginCapture();
        using var request = LocalMediaPlanner.BeginRequest(new LocalMediaRequest(capabilities, _declared));
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
        using var request = LocalMediaPlanner.BeginRequest(new LocalMediaRequest(Browser(hlsOutput: true), _declared));
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
        Assert.True(LocalMediaPlanner.IsExecutableRemux(record.ExecutionPlan));
    }

    [Fact]
    public void A_direct_play_version_is_preferred_over_an_earlier_version_that_needs_a_remux()
    {
        var (planner, store) = Planner(new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object);

        using var capture = store.BeginCapture();
        using var request = LocalMediaPlanner.BeginRequest(new LocalMediaRequest(Browser(hlsOutput: true), _declared));
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
        using var request = LocalMediaPlanner.BeginRequest(new LocalMediaRequest(Browser(hlsOutput: false), _declared));
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

        Assert.True(LocalMediaPlanner.IsExecutableRemux(copy));
        Assert.False(LocalMediaPlanner.IsExecutableRemux(copy with { Transforms = [.. copy.Transforms, encoding] }));
        Assert.False(LocalMediaPlanner.IsExecutableRemux(copy with { Protocol = StreamingProtocol.Http }));
        Assert.False(LocalMediaPlanner.IsExecutableRemux(copy with { Container = "mkv" }));
        Assert.False(LocalMediaPlanner.IsExecutableRemux(copy with { Method = PlaybackMethod.Transcode }));
        Assert.False(LocalMediaPlanner.IsExecutableRemux(copy with { SubtitleStreamIndex = 2, SubtitleDelivery = Tesserafin.Playback.Decision.SubtitleDeliveryMethod.Burn }));
        Assert.False(LocalMediaPlanner.IsExecutableRemux(null));
    }

    [Fact]
    public void An_audio_track_the_client_plays_as_it_is_is_owned_without_the_legacy_planner()
    {
        var (planner, store) = Planner(new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object);

        using var capture = store.BeginCapture();
        using var request = LocalMediaPlanner.BeginRequest(new LocalMediaRequest(AudioPlayer(["mp3"]), _declared));
        var plan = planner.PlanAudio(Options(Track("track", "mp3")));
        var record = store.TakeCaptured();

        Assert.Equal(PlayMethod.DirectPlay, plan?.PlayMethod);
        Assert.Null(plan?.StreamInfo);
        Assert.Equal(PlaybackMethod.DirectPlay, record?.ExecutionPlan?.Method);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_audio_track_that_needs_a_transcode_to_aac_over_hls_is_owned_without_the_legacy_planner(bool coverArt)
    {
        var (planner, store) = Planner(new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object);
        var track = Track("track", "flac");
        if (coverArt)
        {
            // An embedded picture is a video stream to the prober; it is not what an audio session plays.
            track.MediaStreams = [.. track.MediaStreams, new MediaStream { Index = 1, Type = MediaStreamType.EmbeddedImage, Codec = "mjpeg" }];
        }

        using var capture = store.BeginCapture();
        using var request = LocalMediaPlanner.BeginRequest(new LocalMediaRequest(AudioPlayer(["mp3"]), _declared));
        var plan = planner.PlanAudio(Options(track));
        var record = store.TakeCaptured();

        Assert.Equal(PlayMethod.Transcode, plan?.PlayMethod);
        Assert.Null(plan?.StreamInfo);
        var executed = record?.ExecutionPlan;
        Assert.NotNull(executed);
        Assert.Equal(PlaybackMethod.Transcode, executed.Method);
        Assert.Null(executed.VideoStreamIndex);
        Assert.Equal(0, executed.AudioStreamIndex);
        Assert.Equal("aac", executed.AudioCodec);
        Assert.True(LocalMediaPlanner.IsExecutableAudioTranscode(executed));
        Assert.True(LocalMediaPlanner.IsExecutableHlsOutput(executed));
    }

    [Fact]
    public void An_audio_transcode_to_a_target_the_session_route_does_not_encode_is_left_to_the_existing_chain()
    {
        var existing = new PlaybackPlan(PlayMethod.Transcode, 0);
        var inner = new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict);
        inner.Setup(p => p.PlanAudio(It.IsAny<MediaOptions>())).Returns(existing);
        var (planner, store) = Planner(inner.Object);
        var mp3Output = AudioPlayer(["mp3"]) with
        {
            OutputProfiles = [new PlaybackOutputProfile(MediaKind.Audio, StreamingProtocol.Hls, "ts", [], ["mp3"], null, null, null)],
        };

        using var capture = store.BeginCapture();
        using var request = LocalMediaPlanner.BeginRequest(new LocalMediaRequest(mp3Output, _declared));
        var plan = planner.PlanAudio(Options(Track("track", "flac")));

        Assert.Same(existing, plan);
        Assert.Null(store.TakeCaptured());
    }

    [Fact]
    public void An_audio_track_whose_codec_the_client_decodes_in_another_container_is_remuxed_without_the_legacy_planner()
    {
        var (planner, store) = Planner(new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object);
        var track = Track("track", "aac");
        track.Container = "mka";
        track.Path = "track.mka";

        using var capture = store.BeginCapture();
        using var request = LocalMediaPlanner.BeginRequest(new LocalMediaRequest(AudioPlayer(["mp3", "aac"]), _declared));
        var plan = planner.PlanAudio(Options(track));
        var record = store.TakeCaptured();

        Assert.Equal(PlayMethod.DirectStream, plan?.PlayMethod);
        Assert.Null(plan?.StreamInfo);
        var executed = record?.ExecutionPlan;
        Assert.NotNull(executed);
        Assert.Equal(PlaybackMethod.Remux, executed.Method);
        Assert.Null(executed.VideoStreamIndex);
        Assert.Equal(0, executed.AudioStreamIndex);
        Assert.Contains(TransformKind.CopyAudio, executed.Transforms);
        Assert.True(LocalMediaPlanner.IsExecutableAudioRemux(executed));
        Assert.True(LocalMediaPlanner.IsExecutableHlsOutput(executed));
    }

    [Fact]
    public void Only_an_hls_copy_of_one_audio_stream_is_an_executable_audio_remux()
    {
        var remux = new PlaybackExecutionPlan(
            PlaybackMethod.Remux,
            SourceId: "track",
            Container: "mp4",
            Protocol: StreamingProtocol.Hls,
            VideoStreamIndex: null,
            VideoCodec: null,
            VideoBitrate: null,
            Resolution: null,
            VideoRange: null,
            AudioStreamIndex: 0,
            AudioCodec: "aac",
            AudioBitrate: null,
            AudioChannels: null,
            SubtitleStreamIndex: null,
            SubtitleFormat: null,
            SubtitleDelivery: null,
            TotalBitrate: null,
            Transforms: [TransformKind.RemuxContainer, TransformKind.CopyAudio]);

        Assert.True(LocalMediaPlanner.IsExecutableAudioRemux(remux));
        Assert.True(LocalMediaPlanner.IsExecutableAudioRemux(remux with { Container = "ts" }));
        Assert.False(LocalMediaPlanner.IsExecutableAudioRemux(remux with { Container = "mka" }));
        Assert.False(LocalMediaPlanner.IsExecutableAudioRemux(remux with { AudioCodec = null }));
        Assert.False(LocalMediaPlanner.IsExecutableAudioRemux(remux with { Protocol = StreamingProtocol.Http }));
        Assert.False(LocalMediaPlanner.IsExecutableAudioRemux(remux with { Method = PlaybackMethod.Transcode }));
        Assert.False(LocalMediaPlanner.IsExecutableAudioRemux(remux with { Transforms = [TransformKind.RemuxContainer, TransformKind.TranscodeAudio] }));
        Assert.False(LocalMediaPlanner.IsExecutableAudioRemux(remux with { Transforms = [TransformKind.RemuxContainer, TransformKind.CopyAudio, TransformKind.Downmix] }));
        Assert.False(LocalMediaPlanner.IsExecutableAudioRemux(remux with { AudioStreamIndex = null }));
        Assert.False(LocalMediaPlanner.IsExecutableAudioRemux(remux with { VideoStreamIndex = 1, VideoCodec = "h264", Transforms = [TransformKind.RemuxContainer, TransformKind.CopyVideo, TransformKind.CopyAudio] }));
        Assert.False(LocalMediaPlanner.IsExecutableAudioRemux(null));

        // An audio remux is neither a video remux nor an audio transcode.
        Assert.False(LocalMediaPlanner.IsExecutableRemux(remux));
        Assert.False(LocalMediaPlanner.IsExecutableAudioTranscode(remux));
    }

    [Fact]
    public void Only_an_hls_aac_transcode_of_one_audio_stream_is_an_executable_audio_transcode()
    {
        var audio = new PlaybackExecutionPlan(
            PlaybackMethod.Transcode,
            SourceId: "track",
            Container: "mp4",
            Protocol: StreamingProtocol.Hls,
            VideoStreamIndex: null,
            VideoCodec: null,
            VideoBitrate: null,
            Resolution: null,
            VideoRange: null,
            AudioStreamIndex: 0,
            AudioCodec: "aac",
            AudioBitrate: null,
            AudioChannels: 2,
            SubtitleStreamIndex: null,
            SubtitleFormat: null,
            SubtitleDelivery: null,
            TotalBitrate: null,
            Transforms: [TransformKind.RemuxContainer, TransformKind.TranscodeAudio, TransformKind.Downmix]);

        Assert.True(LocalMediaPlanner.IsExecutableAudioTranscode(audio));
        Assert.True(LocalMediaPlanner.IsExecutableAudioTranscode(audio with { Container = "ts" }));
        Assert.False(LocalMediaPlanner.IsExecutableAudioTranscode(audio with { Container = "mp3" }));
        Assert.False(LocalMediaPlanner.IsExecutableAudioTranscode(audio with { AudioCodec = "mp3" }));
        Assert.False(LocalMediaPlanner.IsExecutableAudioTranscode(audio with { Protocol = StreamingProtocol.Http }));
        Assert.False(LocalMediaPlanner.IsExecutableAudioTranscode(audio with { Method = PlaybackMethod.Remux, Transforms = [TransformKind.RemuxContainer, TransformKind.CopyAudio] }));
        Assert.False(LocalMediaPlanner.IsExecutableAudioTranscode(audio with { Transforms = [TransformKind.RemuxContainer, TransformKind.CopyAudio] }));
        Assert.False(LocalMediaPlanner.IsExecutableAudioTranscode(audio with { AudioStreamIndex = null }));
        Assert.False(LocalMediaPlanner.IsExecutableAudioTranscode(audio with { VideoStreamIndex = 1, VideoCodec = "h264" }));
        Assert.False(LocalMediaPlanner.IsExecutableAudioTranscode(audio with { SubtitleStreamIndex = 2, SubtitleFormat = "srt", SubtitleDelivery = Tesserafin.Playback.Decision.SubtitleDeliveryMethod.External }));
        Assert.False(LocalMediaPlanner.IsExecutableAudioTranscode(null));

        // A video plan is never an audio one, and the reverse.
        Assert.False(LocalMediaPlanner.IsExecutableTranscode(audio));
        Assert.False(LocalMediaPlanner.IsExecutableRemux(audio));
    }

    private static (LocalMediaPlanner Planner, InMemoryV2PlanStore Store) Planner(IPlaybackSessionPlanner inner)
    {
        var store = new InMemoryV2PlanStore();
        return (new LocalMediaPlanner(inner, new PlaybackEngine(), store, NullLogger<LocalMediaPlanner>.Instance), store);
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

    private static ClientCapabilities AudioPlayer(string[] codecs)
        => new(
            new DecodeCapabilities(
                [new DecodeProfile(MediaKind.Audio, codecs, [], codecs)],
                [],
                [.. codecs.Select(codec => new AudioCodecCapability(codec, null, null, null, null))],
                [],
                true,
                false),
            [new PlaybackOutputProfile(MediaKind.Audio, StreamingProtocol.Hls, "mp4", [], ["aac"], null, null, null)]);

    private static MediaSourceInfo Track(string id, string codec)
        => new()
        {
            Id = id,
            Path = id + "." + codec,
            Protocol = MediaProtocol.File,
            Container = codec,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            SupportsDirectPlay = true,
            DefaultAudioStreamIndex = 0,
            MediaStreams = [new MediaStream { Index = 0, Type = MediaStreamType.Audio, Codec = codec, IsDefault = true }],
        };
}
