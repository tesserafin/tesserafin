using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Dto;
using Tesserafin.Playback.Decision;
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
}
