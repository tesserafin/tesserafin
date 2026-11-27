using System;
using System.Linq;
using System.Text.Json;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Session;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Shadow;
using Xunit;

namespace Tesserafin.Api.Tests.Models.PlaybackSessionDtos;

/// <summary>
/// Unit tests for <see cref="PlaybackDiagnosticDetailMapper"/> (PR113): the no-diagnostic
/// degradation, the legacy/v2 <see cref="DiagnosticComparison"/> derivation, the
/// <c>Created</c>/<c>Updated</c> timeline, and - the §4.3-mandated filtering rule - that nothing
/// serialized ever leaks a file path, transcoding URL, or session token, even when the underlying
/// session's <see cref="MediaSourceInfo"/> carries them.
/// </summary>
public sealed class PlaybackDiagnosticDetailMapperTests
{
    [Fact]
    public void Map_NoDiagnostic_LeavesV2FieldsNullAndKeepsBaseFields()
    {
        var session = BuildSession();

        var detail = PlaybackDiagnosticDetailMapper.Map(session, diagnostic: null);

        Assert.Equal(session.Id.Value, detail.Id);
        Assert.Null(detail.RequestContext);
        Assert.Null(detail.Capabilities);
        Assert.Null(detail.SourceSnapshot);
        Assert.Null(detail.Reasoning);
        Assert.Null(detail.Comparison);
        Assert.Equal(2, detail.Timeline.Count);
    }

    [Fact]
    public void Map_WithDiagnostic_PopulatesV2FieldsAndComparison()
    {
        var session = BuildSession();
        var diagnostic = FakeShadowDiagnosticRecordFactory.Create();

        var detail = PlaybackDiagnosticDetailMapper.Map(session, diagnostic);

        Assert.Same(diagnostic.Context, detail.RequestContext);
        Assert.Same(diagnostic.Capabilities, detail.Capabilities);
        Assert.Same(diagnostic.Sources, detail.SourceSnapshot);
        Assert.Same(diagnostic.Decision.Reasoning, detail.Reasoning);
        Assert.NotNull(detail.Comparison);
        Assert.Equal(PlaybackMethod.DirectPlay, detail.Comparison!.LegacyMethod);
        Assert.Contains(ReasonCode.VideoCodecNotSupported, detail.Comparison.LegacyReasons);
        Assert.Equal(DivergenceClass.Equivalent, detail.Comparison.DivergenceClass);
        Assert.Equal("equivalent", detail.Comparison.DivergenceSummary);
        Assert.Equal(diagnostic.Decision.Method, detail.Comparison.V2Method);
        Assert.Same(diagnostic.Decision.Output, detail.Comparison.V2Output);
        Assert.Same(diagnostic.Decision.SelectedStreams, detail.Comparison.V2SelectedStreams);
        Assert.Same(diagnostic.Decision.Transforms, detail.Comparison.V2Transforms);
    }

    [Fact]
    public void Map_Timeline_ContainsOnlyCreatedAndUpdatedFromSession()
    {
        var createdAt = DateTimeOffset.UnixEpoch;
        var updatedAt = DateTimeOffset.UnixEpoch.AddMinutes(5);
        var session = BuildSession(createdAt, updatedAt);

        var detail = PlaybackDiagnosticDetailMapper.Map(session, diagnostic: null);

        Assert.Equal(
            new[] { ("Created", createdAt), ("Updated", updatedAt) },
            detail.Timeline.Select(e => (e.Stage, e.At)).ToArray());
    }

    /// <summary>
    /// PR113b: real, observed lifecycle events (ffmpeg launched, playback started/stopped) are
    /// appended after the always-present <c>Created</c>/<c>Updated</c> entries, in the order they
    /// were observed - never fabricated for a stage that was not actually passed in.
    /// </summary>
    [Fact]
    public void Map_WithEvents_AppendsThemAfterCreatedAndUpdated()
    {
        var createdAt = DateTimeOffset.UnixEpoch;
        var updatedAt = DateTimeOffset.UnixEpoch.AddMinutes(5);
        var session = BuildSession(createdAt, updatedAt);
        var ffmpegStartedAt = DateTimeOffset.UnixEpoch.AddSeconds(10);
        var playbackStartedAt = DateTimeOffset.UnixEpoch.AddSeconds(12);
        var events = new[]
        {
            new PlaybackLifecycleEvent("FfmpegStarted", ffmpegStartedAt),
            new PlaybackLifecycleEvent("PlaybackStarted", playbackStartedAt),
        };

        var detail = PlaybackDiagnosticDetailMapper.Map(session, diagnostic: null, events);

        Assert.Equal(
            new[]
            {
                ("Created", createdAt),
                ("Updated", updatedAt),
                ("FfmpegStarted", ffmpegStartedAt),
                ("PlaybackStarted", playbackStartedAt),
            },
            detail.Timeline.Select(e => (e.Stage, e.At)).ToArray());
    }

    /// <summary>
    /// A stage that was never observed must simply be absent - never defaulted to some
    /// approximated timestamp. Covered separately from <see cref="Map_Timeline_ContainsOnlyCreatedAndUpdatedFromSession"/>
    /// to make explicit that this holds even when the caller passes an explicit empty list, not
    /// only the default <see langword="null"/>.
    /// </summary>
    [Fact]
    public void Map_WithEmptyEvents_TimelineStillOnlyHasCreatedAndUpdated()
    {
        var session = BuildSession();

        var detail = PlaybackDiagnosticDetailMapper.Map(session, diagnostic: null, events: []);

        Assert.Equal(2, detail.Timeline.Count);
    }

    [Fact]
    public void Map_LegacyVectorNonViable_FallsBackToTranscodeMethod()
    {
        var session = BuildSession();
        var nonViableDiagnostic = FakeShadowDiagnosticRecordFactory.Create() with
        {
            LegacyVector = FakeShadowDiagnosticRecordFactory.Create().LegacyVector with { Method = null },
        };

        var detail = PlaybackDiagnosticDetailMapper.Map(session, nonViableDiagnostic);

        Assert.Equal(PlaybackMethod.Transcode, detail.Comparison!.LegacyMethod);
    }

    /// <summary>
    /// §4.3's filtering rule, tested as PR113 requires: never a file path, transcoding URL, session
    /// token, or API key in a diagnostic response. This session's <see cref="MediaSourceInfo"/>
    /// deliberately carries all three secrets a raw <see cref="PlaybackSession"/> would leak (see
    /// the old, replaced controller behavior) - <see cref="PlaybackDiagnosticDetail"/> must not
    /// reference <see cref="MediaSourceInfo"/>/<see cref="StreamInfo"/> at all, so none of them can
    /// reach the serialized JSON.
    /// </summary>
    [Fact]
    public void Map_SerializedDetail_NeverLeaksPathTokenOrTranscodingUrl()
    {
        const string secretPath = "/var/lib/reefin/media/super-secret-movie.mkv";
        const string secretToken = "OPENTOKEN-super-secret-abc123";
        const string secretTranscodingUrl = "http://localhost:8096/secret-transcode-url/master.m3u8";

        var mediaSource = new MediaSourceInfo
        {
            Id = "source-1",
            Container = "mkv",
            Path = secretPath,
            OpenToken = secretToken,
            TranscodingUrl = secretTranscodingUrl,
            MediaStreams =
            [
                new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
                new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "aac", IsDefault = true },
            ],
        };
        var streamInfo = new StreamInfo
        {
            DeviceProfile = new DeviceProfile(),
            PlayMethod = PlayMethod.DirectPlay,
            Container = "mkv",
            AudioStreamIndex = 1,
            MediaSource = mediaSource,
        };
        var session = BuildSession(streamInfo: streamInfo);
        var diagnostic = FakeShadowDiagnosticRecordFactory.Create();

        var detail = PlaybackDiagnosticDetailMapper.Map(session, diagnostic);
        var json = JsonSerializer.Serialize(detail);

        Assert.DoesNotContain(secretPath, json, StringComparison.Ordinal);
        Assert.DoesNotContain(secretToken, json, StringComparison.Ordinal);
        Assert.DoesNotContain(secretTranscodingUrl, json, StringComparison.Ordinal);
        Assert.Null(detail.Reasoning?.Detail);
    }

    private static PlaybackSession BuildSession(DateTimeOffset? createdAt = null, DateTimeOffset? updatedAt = null, StreamInfo? streamInfo = null)
        => new(
            PlaybackSessionId.NewId(),
            PlaybackMediaKind.Video,
            null,
            null,
            new PlaybackPlan(PlayMethod.DirectPlay, default),
            createdAt ?? DateTimeOffset.UnixEpoch,
            updatedAt ?? DateTimeOffset.UnixEpoch.AddMinutes(1));
}
