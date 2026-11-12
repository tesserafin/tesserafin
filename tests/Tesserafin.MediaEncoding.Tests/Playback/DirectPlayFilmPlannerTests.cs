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
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Playback;

/// <summary>
/// #404 review P1: the user's own track preferences, resolved onto the source by
/// <c>MediaSourceManager</c>, fill whatever the client did not choose; an explicit choice still wins.
/// </summary>
public sealed class DirectPlayFilmPlannerTests
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

        var constraints = DirectPlayFilmPlanner.WithUserDefaults(_declared, source);

        Assert.Equal(3, constraints.PreferredAudioStreamIndex);
        Assert.Equal(-1, constraints.PreferredSubtitleStreamIndex);
    }

    [Fact]
    public void A_user_with_no_default_subtitle_gets_none_whatever_mode_the_client_declared()
    {
        var source = new MediaSourceInfo { DefaultSubtitleStreamIndex = null };

        Assert.Equal(-1, DirectPlayFilmPlanner.WithUserDefaults(_declared, source).PreferredSubtitleStreamIndex);
    }

    [Fact]
    public void An_explicit_client_choice_wins_over_the_user_default()
    {
        var source = new MediaSourceInfo { DefaultAudioStreamIndex = 3, DefaultSubtitleStreamIndex = -1 };

        var constraints = DirectPlayFilmPlanner.WithUserDefaults(
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
        var planner = new DirectPlayFilmPlanner(
            new Mock<IPlaybackSessionPlanner>(MockBehavior.Strict).Object,
            new PlaybackEngine(),
            store,
            NullLogger<DirectPlayFilmPlanner>.Instance);

        using var capture = store.BeginCapture();
        using var request = DirectPlayFilmPlanner.BeginRequest(new DirectPlayFilmRequest(capabilities, _declared));
        var plan = planner.PlanVideo(options);
        var record = store.TakeCaptured();

        Assert.NotNull(plan);
        Assert.NotNull(record);
        Assert.Equal("second", record.Decision.SelectedSource);
        Assert.Equal(2, record.Decision.SelectedStreams.Audio);
        Assert.Equal(4, record.Decision.SelectedStreams.Subtitle?.Index);
    }

    private static MediaSourceInfo Source(string id, string videoCodec, int audioIndex, int subtitleIndex)
        => new()
        {
            Id = id,
            Path = id + ".mp4",
            Protocol = MediaProtocol.File,
            VideoType = VideoType.VideoFile,
            Container = "mp4",
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
