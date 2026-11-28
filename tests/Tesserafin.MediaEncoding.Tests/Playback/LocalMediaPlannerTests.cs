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
        };
        var store = new InMemoryV2PlanStore();
        var planner = new LocalMediaPlanner(
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
    public void A_remux_with_no_declared_hls_output_has_no_plan()
    {
        // There is no other planner to hand it to (tesserafin#129 step 7): no plan, a 422.
        var (planner, store) = Planner(new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object);

        using var capture = store.BeginCapture();
        using var request = LocalMediaPlanner.BeginRequest(new LocalMediaRequest(Browser(hlsOutput: false), _declared));
        var plan = planner.PlanVideo(Options(Source("film", "h264", 1, 2, "mkv")));

        Assert.Null(plan);
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
    public void An_audio_transcode_to_a_target_other_than_aac_is_refused_not_handed_to_the_existing_chain()
    {
        var (planner, store) = Planner(new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object);
        var mp3Output = AudioPlayer(["mp3"]) with
        {
            OutputProfiles = [new PlaybackOutputProfile(MediaKind.Audio, StreamingProtocol.Hls, "ts", [], ["mp3"], null, null, null)],
        };

        using var capture = store.BeginCapture();
        using var request = LocalMediaPlanner.BeginRequest(new LocalMediaRequest(mp3Output, _declared));
        var plan = planner.PlanAudio(Options(Track("track", "flac")));

        Assert.Null(plan);
        Assert.Null(store.TakeCaptured());
    }

    [Fact]
    public void A_video_transcode_is_re_planned_to_h264_when_the_client_decodes_it_and_refused_when_it_does_not()
    {
        var film = Source("film", "mpeg4", 1, 2, "avi");
        var hevcOutput = Browser(hlsOutput: true) with
        {
            OutputProfiles = [new PlaybackOutputProfile(MediaKind.Video, StreamingProtocol.Hls, "mp4", ["hevc"], ["aac"], null, null, null)],
        };

        // Decodes H.264: re-planned to it, owned.
        var (planner, store) = Planner(new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object);
        using (store.BeginCapture())
        using (LocalMediaPlanner.BeginRequest(new LocalMediaRequest(hevcOutput, _declared)))
        {
            Assert.Equal(PlayMethod.Transcode, planner.PlanVideo(Options(film))?.PlayMethod);
            Assert.Equal("h264", store.TakeCaptured()?.ExecutionPlan?.VideoCodec);
        }

        // Decodes HEVC only: the only transcode is to HEVC, which is refused.
        var hevcOnly = hevcOutput with
        {
            Decode = hevcOutput.Decode with
            {
                DirectPlayProfiles = [new DecodeProfile(MediaKind.Video, ["mp4"], ["hevc"], ["aac"])],
                VideoCodecs = [new VideoCodecCapability("hevc", [], null, null, [], null, null)],
            },
        };
        var (refusing, refusingStore) = Planner(new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object);
        using (refusingStore.BeginCapture())
        using (LocalMediaPlanner.BeginRequest(new LocalMediaRequest(hevcOnly, _declared)))
        {
            Assert.Null(refusing.PlanVideo(Options(film)));
            Assert.Null(refusingStore.TakeCaptured());
        }
    }

    [Fact]
    public void A_remux_of_a_codec_the_session_does_not_encode_stays_allowed()
    {
        var (planner, store) = Planner(new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object);
        var hevcFilm = Source("film", "hevc", 1, 2, "mkv");
        var hevcClient = Browser(hlsOutput: true) with
        {
            Decode = Browser(hlsOutput: true).Decode with
            {
                DirectPlayProfiles = [new DecodeProfile(MediaKind.Video, ["mp4"], ["hevc"], ["aac"])],
                VideoCodecs = [new VideoCodecCapability("hevc", [], null, null, [], null, null)],
            },
            OutputProfiles = [new PlaybackOutputProfile(MediaKind.Video, StreamingProtocol.Hls, "mp4", ["hevc"], ["aac"], null, null, null)],
        };

        using var capture = store.BeginCapture();
        using var request = LocalMediaPlanner.BeginRequest(new LocalMediaRequest(hevcClient, _declared));

        Assert.Equal(PlayMethod.DirectStream, planner.PlanVideo(Options(hevcFilm))?.PlayMethod);
        Assert.Equal(PlaybackMethod.Remux, store.TakeCaptured()?.ExecutionPlan?.Method);
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
    public void An_opened_tuner_stream_is_remuxed_into_hls_never_played_as_it_is()
    {
        var (planner, store) = Planner(new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object);
        var tuner = Source("tuner", "h264", 1, -1, container: "ts");
        tuner.Path = "http://127.0.0.1/LiveTv/LiveStreamFiles/live-1/stream.ts";
        tuner.Protocol = MediaProtocol.Http;
        tuner.IsInfiniteStream = true;
        tuner.LiveStreamId = "live-1";
        tuner.MediaStreams = [.. tuner.MediaStreams.Where(stream => stream.Type != MediaStreamType.Subtitle)];
        var browser = Browser(hlsOutput: true) with
        {
            Decode = Browser(hlsOutput: true).Decode with { DirectPlayProfiles = [new DecodeProfile(MediaKind.Video, ["ts", "mp4"], ["h264"], ["aac"])] },
        };

        using var capture = store.BeginCapture();
        using var request = LocalMediaPlanner.BeginRequest(new LocalMediaRequest(browser, _declared, "live-1"));
        var plan = planner.PlanVideo(Options(tuner));
        var record = store.TakeCaptured();

        // The client could play the container as it is; an infinite, server-only stream still is not handed over.
        Assert.NotEqual(PlayMethod.DirectPlay, plan?.PlayMethod);
        Assert.Equal(PlaybackMethod.Remux, record?.ExecutionPlan?.Method);
        Assert.True(LocalMediaPlanner.IsExecutableHlsOutput(record?.ExecutionPlan));
    }

    [Fact]
    public void A_tuner_stream_the_session_did_not_open_is_refused_not_handed_to_the_existing_chain()
    {
        // The strict inner planner proves the existing chain is not reached (decided 2026-11-27).
        var (planner, store) = Planner(new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object);
        var tuner = Source("tuner", "h264", 1, -1, container: "ts");
        tuner.Protocol = MediaProtocol.Http;
        tuner.IsInfiniteStream = true;
        tuner.LiveStreamId = "someone-else";
        var declared = new LocalMediaRequest(Browser(hlsOutput: true), _declared, "live-1");

        using var capture = store.BeginCapture();
        using var request = LocalMediaPlanner.BeginRequest(declared);

        Assert.Null(planner.PlanVideo(Options(tuner)));
        Assert.Null(store.TakeCaptured());
        Assert.Equal(LocalMediaRefusal.NotTheHeldLiveStream, declared.Refusal);
    }

    [Fact]
    public void A_request_with_no_source_is_refused_not_handed_to_the_existing_chain()
    {
        var (planner, store) = Planner(new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object);
        var declared = new LocalMediaRequest(Browser(hlsOutput: true), _declared);

        using var capture = store.BeginCapture();
        using var request = LocalMediaPlanner.BeginRequest(declared);

        Assert.Null(planner.PlanVideo(Options()));
        Assert.Null(store.TakeCaptured());
        Assert.Equal(LocalMediaRefusal.NoSource, declared.Refusal);
    }

    [Theory]
    [InlineData("PGSSUB", false)]
    [InlineData("ass", false)]
    [InlineData("subrip", true)]
    public void A_subtitle_the_client_cannot_render_is_burnt_in_by_an_owned_transcode(string codec, bool external)
    {
        var (planner, store) = Planner(new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object);
        var film = Source("film", "h264", 1, 2, "mkv");
        film.MediaStreams = [.. film.MediaStreams.Where(stream => stream.Type != MediaStreamType.Subtitle),
            new MediaStream { Index = 2, Type = MediaStreamType.Subtitle, Codec = codec, IsExternal = external, Path = external ? "film.srt" : null }];
        var noSubtitles = Browser(hlsOutput: true) with
        {
            Decode = Browser(hlsOutput: true).Decode with { SubtitleDelivery = [] },
        };

        using var capture = store.BeginCapture();
        using var request = LocalMediaPlanner.BeginRequest(new LocalMediaRequest(noSubtitles, _declared with { PreferredSubtitleStreamIndex = 2 }));
        var plan = planner.PlanVideo(Options(film));
        var executed = store.TakeCaptured()?.ExecutionPlan;

        Assert.Equal(PlayMethod.Transcode, plan?.PlayMethod);
        Assert.NotNull(executed);
        Assert.Equal(Tesserafin.Playback.Decision.SubtitleDeliveryMethod.Burn, executed.SubtitleDelivery);
        Assert.Equal(2, executed.SubtitleStreamIndex);
        Assert.Contains(TransformKind.BurnInSubtitle, executed.Transforms);
        Assert.Contains(TransformKind.TranscodeVideo, executed.Transforms);
        Assert.True(LocalMediaPlanner.IsExecutableTranscode(executed));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_hdr_source_for_an_sdr_client_is_tone_mapped_where_the_executor_can_and_refused_where_it_cannot(bool canTonemap)
    {
        // The strict inner planner proves the existing chain is never reached: where the executor
        // cannot tone-map, the session is refused (decided 2026-11-27).
        var inner = new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict);
        var store = new InMemoryV2PlanStore();
        var planner = new LocalMediaPlanner(new PlaybackEngine(), store, NullLogger<LocalMediaPlanner>.Instance, canTonemap: () => canTonemap);
        var film = Source("film", "hevc", 1, 2, "mkv");
        film.MediaStreams = [new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "hevc", BitDepth = 10, ColorTransfer = "smpte2084", ColorPrimaries = "bt2020", ColorSpace = "bt2020nc" },
            .. film.MediaStreams.Where(stream => stream.Type != MediaStreamType.Video)];
        var sdrClient = Browser(hlsOutput: true) with
        {
            Decode = Browser(hlsOutput: true).Decode with
            {
                DirectPlayProfiles = [new DecodeProfile(MediaKind.Video, ["mp4"], ["h264", "hevc"], ["aac"])],
                VideoCodecs = [new VideoCodecCapability("h264", [], null, null, ["SDR"], null, null), new VideoCodecCapability("hevc", [], null, null, ["SDR"], null, null)],
            },
        };

        var declared = new LocalMediaRequest(sdrClient, _declared);
        using var capture = store.BeginCapture();
        using var request = LocalMediaPlanner.BeginRequest(declared);
        var plan = planner.PlanVideo(Options(film));
        var executed = store.TakeCaptured()?.ExecutionPlan;

        if (canTonemap)
        {
            Assert.Equal(PlayMethod.Transcode, plan?.PlayMethod);
            Assert.NotNull(executed);
            Assert.True(LocalMediaPlanner.IsToneMappingToSdr(executed));
            Assert.Equal("h264", executed.VideoCodec);
            Assert.True(LocalMediaPlanner.IsExecutableTranscode(executed));
        }
        else
        {
            Assert.Null(plan);
            Assert.Null(executed);
            Assert.Equal(LocalMediaRefusal.ToneMappingUnavailable, declared.Refusal);
        }
    }

    [Theory]
    [InlineData(new[] { "SDR", "HDR10" }, true, PlayMethod.Transcode, LocalMediaRefusal.None)]
    [InlineData(new[] { "SDR", "HDR10" }, false, null, LocalMediaRefusal.ToneMappingUnavailable)]
    [InlineData(new[] { "HDR10" }, true, null, LocalMediaRefusal.HdrOutputNotExecuted)]
    public void An_hdr_output_the_session_does_not_execute_becomes_sdr_when_the_client_accepts_it_and_is_refused_otherwise(
        string[] h264Ranges,
        bool canTonemap,
        PlayMethod? expected,
        LocalMediaRefusal refusal)
    {
        // The strict inner planner proves the existing chain is never reached (decided 2026-11-27).
        var store = new InMemoryV2PlanStore();
        var planner = new LocalMediaPlanner(new PlaybackEngine(), store, NullLogger<LocalMediaPlanner>.Instance, canTonemap: () => canTonemap);
        var film = Source("film", "hevc", 1, 2, "mkv");
        film.MediaStreams = [new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "hevc", BitDepth = 10, ColorTransfer = "smpte2084", ColorPrimaries = "bt2020", ColorSpace = "bt2020nc" },
            .. film.MediaStreams.Where(stream => stream.Type != MediaStreamType.Video)];
        // HEVC is decoded in SDR only, so the HDR10 source is encoded; H.264, the target, declares HDR10.
        var client = Browser(hlsOutput: true) with
        {
            Decode = Browser(hlsOutput: true).Decode with
            {
                DirectPlayProfiles = [new DecodeProfile(MediaKind.Video, ["mp4"], ["h264", "hevc"], ["aac"])],
                VideoCodecs = [new VideoCodecCapability("h264", [], null, null, h264Ranges, null, null), new VideoCodecCapability("hevc", [], null, null, ["SDR"], null, null)],
            },
        };
        var declared = new LocalMediaRequest(client, _declared);

        using var capture = store.BeginCapture();
        using var request = LocalMediaPlanner.BeginRequest(declared);
        var plan = planner.PlanVideo(Options(film));
        var executed = store.TakeCaptured()?.ExecutionPlan;

        Assert.Equal(expected, plan?.PlayMethod);
        Assert.Equal(refusal, declared.Refusal);
        if (expected is not null)
        {
            Assert.NotNull(executed);
            Assert.True(LocalMediaPlanner.IsToneMappingToSdr(executed));
            Assert.True(LocalMediaPlanner.IsExecutableTranscode(executed));
            Assert.False(LocalMediaPlanner.IsHdrOutputNotExecuted(executed));
        }
        else
        {
            Assert.Null(executed);
        }
    }

    [Fact]
    public void A_subtitle_planned_as_an_hls_rendition_is_delivered_as_the_external_track_the_client_renders()
    {
        // The strict inner planner proves the existing chain is never reached (decided 2026-11-27).
        var (planner, store) = Planner(new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object);
        var film = Source("film", "h264", 1, 2, "mkv");
        var client = Browser(hlsOutput: true) with
        {
            Decode = Browser(hlsOutput: true).Decode with
            {
                SubtitleDelivery = [new SubtitleCapability("srt", Tesserafin.Playback.Decision.SubtitleDeliveryMethod.Hls), new SubtitleCapability("vtt", Tesserafin.Playback.Decision.SubtitleDeliveryMethod.External)],
            },
        };
        var declared = new LocalMediaRequest(client, _declared with { PreferredSubtitleStreamIndex = 2 });

        using var capture = store.BeginCapture();
        using var request = LocalMediaPlanner.BeginRequest(declared);
        var plan = planner.PlanVideo(Options(film));
        var executed = store.TakeCaptured()?.ExecutionPlan;

        Assert.NotNull(plan);
        Assert.NotNull(executed);
        Assert.Equal(Tesserafin.Playback.Decision.SubtitleDeliveryMethod.External, executed.SubtitleDelivery);
        Assert.Equal(2, executed.SubtitleStreamIndex);
        Assert.True(LocalMediaPlanner.IsExecutableHlsOutput(executed));
        Assert.Equal(LocalMediaRefusal.None, declared.Refusal);
    }

    [Theory]
    [InlineData(true, PlayMethod.Transcode, LocalMediaRefusal.None)]
    [InlineData(false, null, LocalMediaRefusal.SubtitleDeliveryNotExecuted)]
    public void A_subtitle_the_client_wants_embedded_is_burnt_in_where_possible_and_refused_otherwise(bool allowTranscoding, PlayMethod? expected, LocalMediaRefusal refusal)
    {
        var (planner, store) = Planner(new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object);
        var film = Source("film", "h264", 1, 2, "mkv");
        var client = Browser(hlsOutput: true) with
        {
            Decode = Browser(hlsOutput: true).Decode with { SubtitleDelivery = [new SubtitleCapability("srt", Tesserafin.Playback.Decision.SubtitleDeliveryMethod.Embed)] },
        };
        var declared = new LocalMediaRequest(client, _declared with { PreferredSubtitleStreamIndex = 2, AllowTranscoding = allowTranscoding });

        using var capture = store.BeginCapture();
        using var request = LocalMediaPlanner.BeginRequest(declared);
        var plan = planner.PlanVideo(Options(film));
        var executed = store.TakeCaptured()?.ExecutionPlan;

        Assert.Equal(expected, plan?.PlayMethod);
        Assert.Equal(refusal, declared.Refusal);
        if (expected is not null)
        {
            Assert.NotNull(executed);
            Assert.Equal(Tesserafin.Playback.Decision.SubtitleDeliveryMethod.Burn, executed.SubtitleDelivery);
            Assert.True(LocalMediaPlanner.IsExecutableTranscode(executed));
        }
        else
        {
            Assert.Null(executed);
        }
    }

    [Fact]
    public void An_embedded_subtitle_the_client_reads_in_a_direct_play_stays_embedded()
    {
        var (planner, store) = Planner(new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object);
        var film = Source("film", "h264", 1, 2, "mp4");
        film.MediaStreams = [.. film.MediaStreams.Where(stream => stream.Type != MediaStreamType.Subtitle),
            new MediaStream { Index = 2, Type = MediaStreamType.Subtitle, Codec = "srt", IsExternal = false }];
        var client = Browser(hlsOutput: true) with
        {
            Decode = Browser(hlsOutput: true).Decode with { SubtitleDelivery = [new SubtitleCapability("srt", Tesserafin.Playback.Decision.SubtitleDeliveryMethod.Embed)] },
        };

        using var capture = store.BeginCapture();
        using var request = LocalMediaPlanner.BeginRequest(new LocalMediaRequest(client, _declared with { PreferredSubtitleStreamIndex = 2 }));
        var plan = planner.PlanVideo(Options(film));
        var executed = store.TakeCaptured()?.ExecutionPlan;

        Assert.Equal(PlayMethod.DirectPlay, plan?.PlayMethod);
        Assert.Equal(Tesserafin.Playback.Decision.SubtitleDeliveryMethod.Embed, executed?.SubtitleDelivery);
    }

    [Fact]
    public void Only_a_tone_mapping_to_sdr_of_an_encoded_video_is_executable()
    {
        var tonemap = new PlaybackExecutionPlan(
            PlaybackMethod.Transcode,
            SourceId: "film",
            Container: "mp4",
            Protocol: StreamingProtocol.Hls,
            VideoStreamIndex: 0,
            VideoCodec: "h264",
            VideoBitrate: null,
            Resolution: null,
            VideoRange: "SDR",
            AudioStreamIndex: 1,
            AudioCodec: "aac",
            AudioBitrate: null,
            AudioChannels: null,
            SubtitleStreamIndex: null,
            SubtitleFormat: null,
            SubtitleDelivery: null,
            TotalBitrate: null,
            Transforms: [TransformKind.RemuxContainer, TransformKind.TranscodeVideo, TransformKind.Tonemap, TransformKind.CopyAudio]);

        Assert.True(LocalMediaPlanner.IsExecutableTranscode(tonemap));
        Assert.False(LocalMediaPlanner.IsExecutableTranscode(tonemap with { VideoRange = "HDR10" }));
        Assert.False(LocalMediaPlanner.IsExecutableTranscode(tonemap with { VideoRange = null }));
        Assert.False(LocalMediaPlanner.IsExecutableTranscode(tonemap with { Transforms = [TransformKind.RemuxContainer, TransformKind.TranscodeVideo, TransformKind.CopyAudio] }));
        Assert.True(LocalMediaPlanner.IsExecutableTranscode(tonemap with { VideoRange = null, Transforms = [TransformKind.RemuxContainer, TransformKind.TranscodeVideo, TransformKind.CopyAudio] }));
    }

    [Fact]
    public void A_burn_in_is_executable_only_with_the_video_encoded_to_h264()
    {
        var burn = new PlaybackExecutionPlan(
            PlaybackMethod.Transcode,
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
            SubtitleStreamIndex: 2,
            SubtitleFormat: "PGSSUB",
            SubtitleDelivery: Tesserafin.Playback.Decision.SubtitleDeliveryMethod.Burn,
            TotalBitrate: null,
            Transforms: [TransformKind.RemuxContainer, TransformKind.TranscodeVideo, TransformKind.CopyAudio, TransformKind.BurnInSubtitle]);

        Assert.True(LocalMediaPlanner.IsExecutableTranscode(burn));
        Assert.False(LocalMediaPlanner.IsExecutableTranscode(burn with { SubtitleStreamIndex = null }));
        Assert.False(LocalMediaPlanner.IsExecutableTranscode(burn with { Transforms = [TransformKind.RemuxContainer, TransformKind.CopyVideo, TransformKind.CopyAudio, TransformKind.BurnInSubtitle] }));
        Assert.False(LocalMediaPlanner.IsExecutableTranscode(burn with { Transforms = [TransformKind.RemuxContainer, TransformKind.TranscodeVideo, TransformKind.CopyAudio] }));
        Assert.False(LocalMediaPlanner.IsExecutableRemux(burn with { Method = PlaybackMethod.Remux }));
    }

    [Theory]
    [InlineData("mpeg4", "avi", PlaybackMethod.Transcode)]
    [InlineData("h264", "mkv", PlaybackMethod.Remux)]
    public void A_client_with_no_hls_output_is_served_by_an_owned_progressive_output(string videoCodec, string container, PlaybackMethod expected)
    {
        var (planner, store) = Planner(new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object);
        var progressiveOnly = Browser(hlsOutput: false) with
        {
            Decode = Browser(hlsOutput: false).Decode with { SupportsHls = false },
            OutputProfiles = [new PlaybackOutputProfile(MediaKind.Video, StreamingProtocol.Http, "ts", ["h264"], ["aac"], null, null, null)],
        };

        using var capture = store.BeginCapture();
        using var request = LocalMediaPlanner.BeginRequest(new LocalMediaRequest(progressiveOnly, _declared));
        Assert.NotNull(planner.PlanVideo(Options(Source("film", videoCodec, 1, 2, container))));
        var executed = store.TakeCaptured()?.ExecutionPlan;

        Assert.NotNull(executed);
        Assert.Equal(expected, executed.Method);
        Assert.Equal(StreamingProtocol.Http, executed.Protocol);
        Assert.Equal("ts", executed.Container);
        Assert.True(LocalMediaPlanner.IsExecutableProgressive(executed));
        Assert.False(LocalMediaPlanner.IsExecutableHlsOutput(executed));
    }

    [Fact]
    public void A_progressive_output_follows_the_hls_rules_in_its_own_containers()
    {
        var plan = new PlaybackExecutionPlan(
            PlaybackMethod.Transcode,
            SourceId: "film",
            Container: "ts",
            Protocol: StreamingProtocol.Http,
            VideoStreamIndex: 0,
            VideoCodec: "h264",
            VideoBitrate: null,
            Resolution: null,
            VideoRange: null,
            AudioStreamIndex: 1,
            AudioCodec: "aac",
            AudioBitrate: null,
            AudioChannels: null,
            SubtitleStreamIndex: null,
            SubtitleFormat: null,
            SubtitleDelivery: null,
            TotalBitrate: null,
            Transforms: [TransformKind.RemuxContainer, TransformKind.TranscodeVideo, TransformKind.CopyAudio]);

        Assert.True(LocalMediaPlanner.IsExecutableProgressive(plan));
        Assert.True(LocalMediaPlanner.IsExecutableProgressive(plan with { Container = "mkv" }));
        Assert.False(LocalMediaPlanner.IsExecutableProgressive(plan with { Container = "mp4" }));
        Assert.False(LocalMediaPlanner.IsExecutableProgressive(plan with { Protocol = StreamingProtocol.Hls }));
        Assert.False(LocalMediaPlanner.IsExecutableProgressive(plan with { VideoCodec = "hevc" }));
        Assert.True(LocalMediaPlanner.IsExecutableProgressive(plan with
        {
            Container = "aac",
            VideoStreamIndex = null,
            VideoCodec = null,
            Transforms = [TransformKind.RemuxContainer, TransformKind.TranscodeAudio],
        }));
        Assert.False(LocalMediaPlanner.IsExecutableProgressive(plan with { Method = PlaybackMethod.DirectPlay }));
    }

    [Fact]
    public void A_seekable_remote_file_is_executed_by_the_session_never_played_as_it_is()
    {
        var (planner, store) = Planner(new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object);
        var remote = Source("film", "h264", 1, -1, "mp4");
        remote.Path = "http://media.example/film.mp4";
        remote.Protocol = MediaProtocol.Http;
        remote.IsRemote = true;
        remote.RunTimeTicks = TimeSpan.FromMinutes(90).Ticks;
        remote.MediaStreams = [.. remote.MediaStreams.Where(stream => stream.Type != MediaStreamType.Subtitle)];

        using var capture = store.BeginCapture();
        using var request = LocalMediaPlanner.BeginRequest(new LocalMediaRequest(Browser(hlsOutput: true), _declared));
        var plan = planner.PlanVideo(Options(remote));
        var executed = store.TakeCaptured()?.ExecutionPlan;

        // The browser plays H.264/AAC in MP4 as it is; a remote url is still not handed to it.
        Assert.NotEqual(PlayMethod.DirectPlay, plan?.PlayMethod);
        Assert.NotNull(executed);
        Assert.True(LocalMediaPlanner.IsExecutableSessionOutput(executed));
        Assert.True(LocalMediaPlanner.IsSeekableRemoteSource(remote));
    }

    [Theory]
    [InlineData("http://media.example/live", MediaProtocol.Http, 0L)]
    [InlineData("rtsp://camera.example/stream", MediaProtocol.Rtsp, 600_000_000L)]
    public void A_non_local_source_no_session_provider_reads_is_refused(string path, MediaProtocol protocol, long runTimeTicks)
    {
        var (planner, store) = Planner(new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object);
        var remote = Source("film", "h264", 1, -1, "ts");
        remote.Path = path;
        remote.Protocol = protocol;
        remote.IsRemote = true;
        remote.RunTimeTicks = runTimeTicks == 0 ? null : runTimeTicks;

        using var capture = store.BeginCapture();
        using var request = LocalMediaPlanner.BeginRequest(new LocalMediaRequest(Browser(hlsOutput: true), _declared));

        Assert.Null(planner.PlanVideo(Options(remote)));
        Assert.Null(store.TakeCaptured());
        Assert.False(LocalMediaPlanner.IsSeekableRemoteSource(remote));
    }

    [Theory]
    [InlineData(VideoType.Dvd)]
    [InlineData(VideoType.BluRay)]
    public void A_disc_folder_is_executed_by_the_session_never_played_as_it_is(VideoType videoType)
    {
        var (planner, store) = Planner(new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object);
        var disc = Source("disc", "h264", 1, -1, "mp4");
        disc.Path = "/media/Disc Probe";
        disc.VideoType = videoType;
        disc.MediaStreams = [.. disc.MediaStreams.Where(stream => stream.Type != MediaStreamType.Subtitle)];

        using var capture = store.BeginCapture();
        using var request = LocalMediaPlanner.BeginRequest(new LocalMediaRequest(Browser(hlsOutput: true), _declared));
        var plan = planner.PlanVideo(Options(disc));

        Assert.NotEqual(PlayMethod.DirectPlay, plan?.PlayMethod);
        Assert.True(LocalMediaPlanner.IsExecutableSessionOutput(store.TakeCaptured()?.ExecutionPlan));
        Assert.True(LocalMediaPlanner.IsLocalDiscSource(disc));
    }

    [Theory]
    [InlineData(IsoType.Dvd)]
    [InlineData(IsoType.BluRay)]
    public void A_disc_image_this_ffmpeg_opens_is_executed_by_the_session_never_played_as_it_is(IsoType isoType)
    {
        var (planner, store) = Planner(new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object, kind => kind == isoType);
        var image = DiscImage(isoType);

        using var capture = store.BeginCapture();
        using var request = LocalMediaPlanner.BeginRequest(new LocalMediaRequest(Browser(hlsOutput: true), _declared));
        var plan = planner.PlanVideo(Options(image));

        Assert.NotNull(plan);
        Assert.NotEqual(PlayMethod.DirectPlay, plan.PlayMethod);
        Assert.True(LocalMediaPlanner.IsExecutableSessionOutput(store.TakeCaptured()?.ExecutionPlan));
        Assert.True(LocalMediaPlanner.IsLocalDiscImageSource(image));
        Assert.False(LocalMediaPlanner.IsLocalFileSource(image));
    }

    [Theory]
    [InlineData(IsoType.Dvd)]
    [InlineData(IsoType.BluRay)]
    [InlineData(null)]
    public void A_disc_image_this_ffmpeg_cannot_open_is_refused_not_handed_to_the_legacy_planner(IsoType? isoType)
    {
        // The other kind opens; this one does not (or the scan never recognised it). The strict
        // inner planner proves the existing chain is not reached.
        var (planner, store) = Planner(new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object, kind => kind != isoType && isoType is not null);
        var image = DiscImage(isoType);

        using var capture = store.BeginCapture();
        using var request = LocalMediaPlanner.BeginRequest(new LocalMediaRequest(Browser(hlsOutput: true), _declared));

        Assert.Null(planner.PlanVideo(Options(image)));
        Assert.Null(store.TakeCaptured());
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

    private static (LocalMediaPlanner Planner, InMemoryV2PlanStore Store) Planner(IPlaybackSessionPlanner inner, Func<IsoType, bool>? canReadDiscImage = null)
    {
        var store = new InMemoryV2PlanStore();
        return (new LocalMediaPlanner(new PlaybackEngine(), store, NullLogger<LocalMediaPlanner>.Instance, canReadDiscImage: canReadDiscImage), store);
    }

    private static MediaSourceInfo DiscImage(IsoType? isoType)
    {
        var image = Source("image", "h264", 1, -1, "iso");
        image.Path = "/media/Disc Probe.iso";
        image.VideoType = VideoType.Iso;
        image.IsoType = isoType;
        image.MediaStreams = [.. image.MediaStreams.Where(stream => stream.Type != MediaStreamType.Subtitle)];
        return image;
    }

    private static MediaOptions Options(params MediaSourceInfo[] sources)
        => new()
        {
            ItemId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            MediaSources = sources,
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
