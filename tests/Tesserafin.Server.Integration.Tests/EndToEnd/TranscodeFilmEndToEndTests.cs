using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Persistence;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Engine;
using Tesserafin.Server.Integration.Tests.PlaybackCredentials;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// Consolidation step 1, third journey (docs/playback-path-inventory.md §6): a film that needs an
/// SDR transcode - an audio or a video stream the client does not decode, a bitrate or a
/// resolution ceiling, a stream copy the request forbids - is decided by the v2 engine alone and
/// executed from the retained plan through the session's own HLS route. Runs on the same host as
/// <see cref="DirectPlayFilmEndToEndTests"/>: the legacy planner (<c>StreamBuilder</c>) and the
/// live-stream resolver (the only caller of <c>PlaybackExecutionPlanAdapter.ToStreamInfo</c>) throw
/// when reached. FFmpeg is real: what was copied and what was encoded is read from the command the
/// server ran and from the segments it produced, not from the decision's label.
/// </summary>
/// <remarks>
/// Not <c>Category=Smoke</c>, and required by name in the hosted integration job, for the reasons
/// given on <see cref="DirectPlayFilmEndToEndTests"/>.
/// </remarks>
public sealed partial class TranscodeFilmEndToEndTests : IClassFixture<DirectPlayFilmEndToEndTests.LegacyFreeFactory>, IAsyncLifetime
{
    private const int FixtureSeconds = 12;
    private const string Password = "transcode-password";
    private const string Ac3 = "ac3";
    private const string Mpeg4 = "mpeg4";

    private readonly DirectPlayFilmEndToEndTests.LegacyFreeFactory _factory;
    private HttpClient _admin = null!;
    private string _workDir = null!;
    private ILibraryManager _libraryManager = null!;
    private IMediaStreamRepository _mediaStreamRepository = null!;
    private IServerConfigurationManager _configManager = null!;
    private ITranscodeManager _transcodeManager = null!;
    private IPlaybackSessionManager _sessionManager = null!;
    private string _logDirectory = null!;

    public TranscodeFilmEndToEndTests(DirectPlayFilmEndToEndTests.LegacyFreeFactory factory)
    {
        _factory = factory;
    }

    public async ValueTask InitializeAsync()
    {
        _admin = _factory.CreateClient();
        _admin.Timeout = TimeSpan.FromSeconds(120);
        var (accessToken, _) = await _factory.EnsureAuthenticatedAsync();
        _admin.DefaultRequestHeaders.AddAuthHeader(accessToken);
        _libraryManager = _factory.Services.GetRequiredService<ILibraryManager>();
        _mediaStreamRepository = _factory.Services.GetRequiredService<IMediaStreamRepository>();
        _configManager = _factory.Services.GetRequiredService<IServerConfigurationManager>();
        _transcodeManager = _factory.Services.GetRequiredService<ITranscodeManager>();
        _sessionManager = _factory.Services.GetRequiredService<IPlaybackSessionManager>();
        _logDirectory = _factory.Services.GetRequiredService<IApplicationPaths>().LogDirectoryPath;

        // Decided by the engine even with the global mode left at its Legacy default.
        _configManager.Configuration.PlaybackShadow.Mode = PlaybackEngineMode.Legacy;
        _workDir = Directory.CreateTempSubdirectory("consol-transcode-e2e-").FullName;
    }

    public ValueTask DisposeAsync()
    {
        _admin.Dispose();
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (IOException)
        {
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task TranscodeFilm_AudioTheClientDoesNotDecode_CopiesVideoAndEncodesAudioInOneSessionAndOneJob()
    {
        const string PlaySession = "transcode-audio-ps";
        var (client, userId) = await SignInAsync("transcode-audio");
        var itemId = await SeedFilmAsync("libx264", Ac3);
        var session = await CreateSessionAsync(client, itemId, userId, PlaySession);

        // Decision: the engine's. Only the audio is the problem, so only the audio is encoded.
        Assert.Equal(PlaybackMethod.Transcode, session.Method);
        Assert.Equal(PlaybackEngine.EngineVersion, session.DecisionVersion);
        Assert.Equal([TransformKind.RemuxContainer, TransformKind.CopyVideo, TransformKind.TranscodeAudio], session.Transforms);
        Assert.Equal("h264", session.Output.VideoCodec);
        Assert.Equal("aac", session.Output.AudioCodec);

        // Descriptor: the session's own route; no encoding parameter and no credential in it.
        var descriptor = await GetDescriptorAsync(client, session.Id);
        Assert.StartsWith(FormattableString.Invariant($"/Playback/Sessions/{session.Id:N}/Hls/main.m3u8?"), descriptor.Url, StringComparison.Ordinal);
        Assert.Equal(StreamingProtocol.Hls, descriptor.Protocol);
        Assert.Equal("mp4", descriptor.Container);
        Assert.Equal(PlaybackEngine.EngineVersion, descriptor.ServedBy);
        Assert.Null(descriptor.FallbackReason);
        foreach (var legacyParameter in new[] { "ApiKey", "VideoCodec", "AudioCodec", "VideoBitrate", "AudioBitrate", "MaxWidth", "TranscodeReasons", "SegmentContainer", "AudioStreamIndex" })
        {
            Assert.DoesNotContain(legacyParameter, descriptor.Url, StringComparison.OrdinalIgnoreCase);
        }

        // Start, then a seek to the end and back to the middle.
        var playlist = await ReadPlaylistAsync(client, descriptor.Url);
        Assert.True(playlist.Segments.Count >= 2, FormattableString.Invariant($"expected several segments, got {playlist.Segments.Count}"));
        var init = await GetBytesAsync(client, playlist.Init);
        var first = await GetBytesAsync(client, playlist.Segments[0]);
        Assert.NotEmpty(await GetBytesAsync(client, playlist.Segments[^1]));
        Assert.NotEmpty(await GetBytesAsync(client, playlist.Segments[playlist.Segments.Count / 2]));

        // What was produced: H.264 as it was, and AAC where the source had AC-3.
        Assert.Equal(["video,h264", "audio,aac"], await ProbeProducedAsync(init, first, withWidth: false));

        // What FFmpeg was told: copy the video, encode the audio. Every command of this playback
        // (a seek may restart the job) says the same.
        var commands = await FfmpegCommandsAsync(itemId);
        Assert.NotEmpty(commands);
        Assert.All(commands, command =>
        {
            Assert.Contains("-codec:v:0 copy", command, StringComparison.Ordinal);
            Assert.DoesNotContain("-codec:a:0 copy", command, StringComparison.Ordinal);
            Assert.Matches(AudioEncodedToAac(), command);
        });

        // One session, the one the client created, holding the engine's plan and no StreamInfo;
        // one job, its owner's.
        var tracked = Assert.Single(_sessionManager.GetAll(), s => string.Equals(s.PlaySessionId, PlaySession, StringComparison.Ordinal));
        Assert.Equal(session.Id, tracked.Id.Value);
        Assert.Null(tracked.Plan.StreamInfo);
        var job = _transcodeManager.GetTranscodingJob(PlaySession);
        Assert.NotNull(job);
        Assert.Equal(userId, job.UserId);
        var jobPlaylist = job.Path!;

        // Stop: the session, its job and its output all go.
        await StopAsync(client, session.Id);
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, descriptor.Url));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, playlist.Segments[0]));
        Assert.Null(_transcodeManager.GetTranscodingJob(PlaySession));
        Assert.DoesNotContain(_sessionManager.GetAll(), s => string.Equals(s.PlaySessionId, PlaySession, StringComparison.Ordinal));
        await WaitUntilAsync(() => !File.Exists(jobPlaylist) && !Directory.EnumerateFiles(Path.GetDirectoryName(jobPlaylist)!, Path.GetFileNameWithoutExtension(jobPlaylist) + "*").Any(), "the job's output to be removed");
    }

    [Fact]
    public async Task TranscodeFilm_VideoTheClientDoesNotDecode_EncodesH264AndCopiesAudio()
    {
        var (client, userId) = await SignInAsync("transcode-video");
        var itemId = await SeedFilmAsync(Mpeg4, "aac");
        var session = await CreateSessionAsync(client, itemId, userId, "transcode-video-ps");

        Assert.Equal(PlaybackMethod.Transcode, session.Method);
        Assert.Equal([TransformKind.RemuxContainer, TransformKind.TranscodeVideo, TransformKind.CopyAudio], session.Transforms);
        Assert.Equal("h264", session.Output.VideoCodec);

        var playlist = await ReadPlaylistAsync(client, (await GetDescriptorAsync(client, session.Id)).Url);
        var produced = await ProbeProducedAsync(await GetBytesAsync(client, playlist.Init), await GetBytesAsync(client, playlist.Segments[0]), withWidth: false);
        Assert.Equal(["video,h264", "audio,aac"], produced);

        var command = Assert.Single(await FfmpegCommandsAsync(itemId));
        Assert.DoesNotContain("-codec:v:0 copy", command, StringComparison.Ordinal);
        Assert.Matches(VideoEncodedToH264(), command);
        Assert.Contains("-codec:a:0 copy", command, StringComparison.Ordinal);
        await StopAsync(client, session.Id);
    }

    [Fact]
    public async Task TranscodeFilm_BitrateAndResolutionCeilings_AreThePlansAndReachTheEncoder()
    {
        var (client, userId) = await SignInAsync("transcode-ceilings");
        var itemId = await SeedFilmAsync("libx264", "aac", videoBitrate: 2_000_000, audioBitrate: 128_000);

        // A bitrate ceiling under the source's: the video is encoded, the audio still copied, and
        // the encoder is given what is left of the ceiling once the copied audio is counted.
        var (capabilities, constraints) = EndToEndCapabilityPresets.BrowserRemuxOverHls();
        var capped = await CreateSessionAsync(client, itemId, userId, "transcode-bitrate-ps", capabilities, constraints with { MaxBitrate = 400_000 });
        Assert.Equal(PlaybackMethod.Transcode, capped.Method);
        Assert.Contains(TransformKind.TranscodeVideo, capped.Transforms);
        Assert.Contains(TransformKind.CopyAudio, capped.Transforms);
        Assert.Equal(400_000, capped.Output.TotalBitrate);

        var playlist = await ReadPlaylistAsync(client, (await GetDescriptorAsync(client, capped.Id)).Url);
        Assert.NotEmpty(await GetBytesAsync(client, playlist.Segments[0]));
        var command = Assert.Single(await FfmpegCommandsAsync(itemId));
        var maxrate = int.Parse(MaxRate().Match(command).Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.InRange(maxrate, 1, 400_000 - 128_000);
        Assert.Contains("-codec:a:0 copy", command, StringComparison.Ordinal);
        await StopAsync(client, capped.Id);

        // A resolution ceiling the client declares for the codec it decodes: the plan carries it and
        // what is produced is no wider.
        var limited = capabilities with
        {
            Decode = capabilities.Decode with
            {
                VideoCodecs = [new VideoCodecCapability("h264", [], null, null, [], new Resolution(160, 120), null)],
            },
        };
        var scaled = await CreateSessionAsync(client, itemId, userId, "transcode-resolution-ps", limited, constraints);
        Assert.Equal(PlaybackMethod.Transcode, scaled.Method);
        Assert.Equal(new Resolution(160, 120), scaled.Output.Resolution);
        playlist = await ReadPlaylistAsync(client, (await GetDescriptorAsync(client, scaled.Id)).Url);
        var produced = await ProbeProducedAsync(await GetBytesAsync(client, playlist.Init), await GetBytesAsync(client, playlist.Segments[0]), withWidth: true);
        var width = int.Parse(Assert.Single(produced, line => line.StartsWith("video,h264,", StringComparison.Ordinal)).Split(',')[2], CultureInfo.InvariantCulture);
        Assert.InRange(width, 2, 160);
        await StopAsync(client, scaled.Id);
    }

    [Fact]
    public async Task TranscodeFilm_AudioTrackChanged_ReplansTheSameSessionAndLeavesNoJobBehind()
    {
        const string PlaySession = "transcode-track-ps";
        var (client, userId) = await SignInAsync("transcode-track");
        var itemId = await SeedFilmAsync("libx264", Ac3, secondAudioTrack: true);
        var (capabilities, constraints) = EndToEndCapabilityPresets.BrowserRemuxOverHls();

        // The user's default is the second track (index 2).
        var session = await CreateSessionAsync(client, itemId, userId, PlaySession);
        Assert.Equal(2, session.SelectedStreams.Audio);
        var playlist = await ReadPlaylistAsync(client, (await GetDescriptorAsync(client, session.Id)).Url);
        Assert.NotEmpty(await GetBytesAsync(client, playlist.Segments[0]));
        var firstJob = _transcodeManager.GetTranscodingJob(PlaySession);
        Assert.NotNull(firstJob);

        // Same session, same play session, the other track.
        using (var replan = await client.PutAsJsonAsync(
            FormattableString.Invariant($"Playback/Sessions/{session.Id:N}"),
            new ReplacePlaybackSessionRequest(itemId, userId, capabilities, constraints with { PreferredAudioStreamIndex = 1 }),
            JsonDefaults.Options,
            TestContext.Current.CancellationToken))
        {
            var body = await replan.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(replan.IsSuccessStatusCode, body);
            var replanned = JsonSerializer.Deserialize<PlaybackSessionResponse>(body, JsonDefaults.Options)!;
            Assert.Equal(session.Id, replanned.Id);
            Assert.Equal(PlaybackMethod.Transcode, replanned.Method);
            Assert.Equal(1, replanned.SelectedStreams.Audio);
        }

        // The first plan's job is gone; what is served now was produced for the new plan.
        Assert.Null(_transcodeManager.GetTranscodingJob(PlaySession));
        var again = await ReadPlaylistAsync(client, (await GetDescriptorAsync(client, session.Id)).Url);
        Assert.NotEmpty(await GetBytesAsync(client, again.Init));
        Assert.NotEmpty(await GetBytesAsync(client, again.Segments[0]));
        var commands = await FfmpegCommandsAsync(itemId);
        Assert.Equal(2, commands.Count);
        Assert.Single(commands, command => command.Contains("-map 0:2 ", StringComparison.Ordinal));
        Assert.Single(commands, command => command.Contains("-map 0:1 ", StringComparison.Ordinal));
        Assert.All(commands, command => Assert.Contains("-codec:v:0 copy", command, StringComparison.Ordinal));
        Assert.Single(_sessionManager.GetAll(), s => string.Equals(s.PlaySessionId, PlaySession, StringComparison.Ordinal));
        Assert.NotNull(_transcodeManager.GetTranscodingJob(PlaySession));

        await StopAsync(client, session.Id);
        Assert.Null(_transcodeManager.GetTranscodingJob(PlaySession));
    }

    [Fact]
    public async Task TranscodeFilm_RemuxTheExecutorCannotCopy_IsRefusedThenReplannedAsATranscodeOfThatStream()
    {
        const string PlaySession = "transcode-refused-remux-ps";
        var (client, userId) = await SignInAsync("transcode-refused-remux");

        // Annex-B H.264 in AVI: the engine sees two decodable codecs in an undeclared container and
        // plans a remux; the executor knows that bitstream cannot be copied into MP4 segments.
        var itemId = await SeedFilmAsync("libx264", "aac", avi: true);
        var (capabilities, constraints) = EndToEndCapabilityPresets.BrowserRemuxOverHls();
        var session = await CreateSessionAsync(client, itemId, userId, PlaySession);
        Assert.Equal(PlaybackMethod.Remux, session.Method);
        var descriptor = await GetDescriptorAsync(client, session.Id);

        // Refused, not silently encoded: the executor does not re-decide.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await StatusAsync(client, descriptor.Url));
        Assert.Empty(await FfmpegCommandsAsync(itemId));
        Assert.Null(_transcodeManager.GetTranscodingJob(PlaySession));

        // The client forbids the copy that failed; the same session is planned again by the engine
        // as a transcode of the video only, and is served.
        using (var replan = await client.PutAsJsonAsync(
            FormattableString.Invariant($"Playback/Sessions/{session.Id:N}"),
            new ReplacePlaybackSessionRequest(itemId, userId, capabilities, constraints with { AllowVideoStreamCopy = false }),
            JsonDefaults.Options,
            TestContext.Current.CancellationToken))
        {
            var body = await replan.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(replan.IsSuccessStatusCode, body);
            var replanned = JsonSerializer.Deserialize<PlaybackSessionResponse>(body, JsonDefaults.Options)!;
            Assert.Equal(session.Id, replanned.Id);
            Assert.Equal(PlaybackMethod.Transcode, replanned.Method);
            Assert.Equal([TransformKind.RemuxContainer, TransformKind.TranscodeVideo, TransformKind.CopyAudio], replanned.Transforms);
        }

        var playlist = await ReadPlaylistAsync(client, (await GetDescriptorAsync(client, session.Id)).Url);
        Assert.NotEmpty(await GetBytesAsync(client, playlist.Init));
        Assert.NotEmpty(await GetBytesAsync(client, playlist.Segments[0]));
        var command = Assert.Single(await FfmpegCommandsAsync(itemId));
        Assert.Matches(VideoEncodedToH264(), command);
        Assert.Contains("-codec:a:0 copy", command, StringComparison.Ordinal);
        Assert.Single(_sessionManager.GetAll(), s => string.Equals(s.PlaySessionId, PlaySession, StringComparison.Ordinal));
        await StopAsync(client, session.Id);
    }

    [Fact]
    public async Task TranscodeFilm_AnotherUser_CanNeitherReadNorStopNorTakeTheJob()
    {
        const string PlaySession = "transcode-owner-ps";
        var (owner, ownerId) = await SignInAsync("transcode-owner");
        var (intruder, _) = await SignInAsync("transcode-intruder");
        var itemId = await SeedFilmAsync("libx264", Ac3);
        var session = await CreateSessionAsync(owner, itemId, ownerId, PlaySession);
        var descriptor = await GetDescriptorAsync(owner, session.Id);
        var playlist = await ReadPlaylistAsync(owner, descriptor.Url);
        Assert.NotEmpty(await GetBytesAsync(owner, playlist.Segments[0]));

        using (var stream = await intruder.GetAsync(FormattableString.Invariant($"Playback/Sessions/{session.Id:N}/Stream"), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, stream.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(intruder, descriptor.Url));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(intruder, playlist.Segments[0]));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(_admin, playlist.Segments[0]));
        using (var mint = await intruder.PostAsJsonAsync(
            "Playback/Capabilities",
            new { PlaySessionId = PlaySession, ItemId = itemId, MediaSourceId = itemId.ToString("N"), Scopes = new[] { "Media" } },
            JsonDefaults.Options,
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, mint.StatusCode);
        }

        using (var stop = await intruder.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{session.Id:N}"), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, stop.StatusCode);
        }

        Assert.Equal(ownerId, _transcodeManager.GetTranscodingJob(PlaySession)?.UserId);
        Assert.NotEmpty(await GetBytesAsync(owner, playlist.Segments[^1]));
        await StopAsync(owner, session.Id);
    }

    [Fact]
    public async Task TranscodeFilm_MediaElementCredential_ReadsOnlyWhileTheSessionLives()
    {
        const string PlaySession = "transcode-capability-ps";
        var (client, userId) = await SignInAsync("transcode-capability");
        var itemId = await SeedFilmAsync("libx264", Ac3);
        var session = await CreateSessionAsync(client, itemId, userId, PlaySession);
        var descriptor = await GetDescriptorAsync(client, session.Id);
        var capability = await MintMediaCapabilityAsync(client, itemId, itemId.ToString("N"), PlaySession);

        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(anonymous, descriptor.Url));
        var playlist = await ReadPlaylistAsync(anonymous, descriptor.Url + "&playbackCapability=" + Uri.EscapeDataString(capability));
        Assert.Contains("playbackCapability=", playlist.Segments[0], StringComparison.Ordinal);
        Assert.NotEmpty(await GetBytesAsync(anonymous, playlist.Init));
        Assert.NotEmpty(await GetBytesAsync(anonymous, playlist.Segments[0]));

        await StopAsync(client, session.Id);
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(anonymous, playlist.Segments[0]));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(anonymous, playlist.Init));
    }

    [Fact]
    public async Task TranscodeFilm_AlternateVersionWithdrawn_StopsServingAndCannotBePlannedAgain()
    {
        const string Tag = "transcode-version-withdrawn";
        var (client, userId) = await SignInAsync("transcode-version");
        var itemId = await SeedFilmAsync("libx264", Ac3);
        var versionId = await SeedFilmAsync("libx264", Ac3, tags: [Tag]);
        _factory.Services.GetRequiredService<IItemWriteCommands>().UpsertLinkedChild(itemId, versionId, LinkedChildType.LinkedAlternateVersion, TestContext.Current.CancellationToken);

        var (capabilities, constraints) = EndToEndCapabilityPresets.BrowserRemuxOverHls();
        var session = await CreateSessionAsync(client, itemId, userId, "transcode-version-ps", capabilities, constraints, versionId.ToString("N"));
        Assert.Equal(PlaybackMethod.Transcode, session.Method);
        var descriptor = await GetDescriptorAsync(client, session.Id);
        Assert.Contains(FormattableString.Invariant($"mediaSourceId={versionId:N}"), descriptor.Url, StringComparison.Ordinal);
        var playlist = await ReadPlaylistAsync(client, descriptor.Url);
        Assert.NotEmpty(await GetBytesAsync(client, playlist.Segments[0]));

        // The version is withdrawn; the film itself stays visible.
        await UpdatePolicyAsync(userId, policy => policy["BlockedTags"] = new JsonArray(Tag));
        using (var film = await client.GetAsync(FormattableString.Invariant($"Items/{itemId:N}?userId={userId:N}"), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, film.StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, descriptor.Url));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, playlist.Segments[0]));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, playlist.Init));

        using var replan = await client.PostAsJsonAsync(
            "Playback/Sessions",
            new CreatePlaybackSessionRequest(itemId, userId, capabilities, constraints, MediaSourceId: versionId.ToString("N"), PlaySessionId: "transcode-version-ps-2"),
            JsonDefaults.Options,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, replan.StatusCode);
        await StopAsync(client, session.Id);
    }

    [Fact]
    public async Task TranscodeFilm_EncodingOptionsInTheQuery_ChangeNothing()
    {
        var (client, userId) = await SignInAsync("transcode-options");
        var itemId = await SeedFilmAsync(Mpeg4, "aac");
        var session = await CreateSessionAsync(client, itemId, userId, "transcode-options-ps");
        var descriptor = await GetDescriptorAsync(client, session.Id);

        // Everything the legacy route would have read from the address: none of it is an input.
        const string Options = "&VideoCodec=hevc&AudioCodec=mp3&VideoBitRate=50000&AudioBitRate=8000&MaxWidth=32&MaxHeight=32&Width=32"
            + "&AudioStreamIndex=7&VideoStreamIndex=7&AllowVideoStreamCopy=true&EnableAutoStreamCopy=true&SubtitleMethod=Encode&SubtitleStreamIndex=3"
            + "&SegmentContainer=ts&StartTimeTicks=0&h264-level=10&h264-profile=baseline&profile=baseline&level=10&maxWidth=32";
        var playlist = await ReadPlaylistAsync(client, descriptor.Url + Options);
        var produced = await ProbeProducedAsync(await GetBytesAsync(client, playlist.Init), await GetBytesAsync(client, playlist.Segments[0]), withWidth: true);
        Assert.Equal([FormattableString.Invariant($"video,h264,{EndToEndMediaFixtures.Width}"), "audio,aac"], produced);

        var command = Assert.Single(await FfmpegCommandsAsync(itemId));
        Assert.Matches(VideoEncodedToH264(), command);
        Assert.Contains("-codec:a:0 copy", command, StringComparison.Ordinal);
        Assert.DoesNotContain("-codec:v:0 libx265", command, StringComparison.Ordinal);
        Assert.DoesNotContain("-codec:a:0 libmp3lame", command, StringComparison.Ordinal);
        Assert.DoesNotContain("-maxrate 50000", command, StringComparison.Ordinal);
        await StopAsync(client, session.Id);
    }

    /// <summary>
    /// #413: revoking a device ends the playback sessions it holds, with the cleanup of an explicit
    /// stop - session, job and output - instead of leaving them to the 6 h TTL. The same user's
    /// session on another device plays on.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task TranscodeFilm_DeviceRevoked_EndsItsSessionJobAndOutput_AndOnlyThatDevicesOnes()
    {
        const string Revoked = "transcode-revoked-ps";
        const string Kept = "transcode-kept-ps";
        var (phone, userId) = await SignInAsync("transcode-revoke");
        var tablet = await SignInAgainAsync("transcode-revoke", "tablet-transcode-revoke");
        var itemId = await SeedFilmAsync("libx264", Ac3);

        var revoked = await CreateSessionAsync(phone, itemId, userId, Revoked);
        var kept = await CreateSessionAsync(tablet, itemId, userId, Kept);
        var revokedPlaylist = await ReadPlaylistAsync(phone, (await GetDescriptorAsync(phone, revoked.Id)).Url);
        var keptPlaylist = await ReadPlaylistAsync(tablet, (await GetDescriptorAsync(tablet, kept.Id)).Url);
        Assert.NotEmpty(await GetBytesAsync(phone, revokedPlaylist.Segments[0]));
        Assert.NotEmpty(await GetBytesAsync(tablet, keptPlaylist.Segments[0]));
        var job = _transcodeManager.GetTranscodingJob(Revoked);
        Assert.NotNull(job);
        var jobPlaylist = job.Path!;

        using (var revoke = await _admin.DeleteAsync("Devices?id=device-transcode-revoke", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        }

        // The revocation's answer already finds the session gone, not hours later.
        Assert.DoesNotContain(_sessionManager.GetAll(), s => s.Id.Value.Equals(revoked.Id));
        await WaitUntilAsync(() => _transcodeManager.GetTranscodingJob(Revoked) is null, "the revoked device's job to stop");
        await WaitUntilAsync(
            () => !File.Exists(jobPlaylist) && !Directory.EnumerateFiles(Path.GetDirectoryName(jobPlaylist)!, Path.GetFileNameWithoutExtension(jobPlaylist) + "*").Any(),
            "the revoked device's output to be removed");
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(phone, revokedPlaylist.Segments[0]));

        // The same user on the other device plays on, from the same server state.
        Assert.Contains(_sessionManager.GetAll(), s => s.Id.Value.Equals(kept.Id));
        Assert.NotNull(_transcodeManager.GetTranscodingJob(Kept));
        Assert.NotEmpty(await GetBytesAsync(tablet, keptPlaylist.Segments[^1]));
        await StopAsync(tablet, kept.Id);
    }

    [GeneratedRegex(@"-codec:a:0 (aac|libfdk_aac|aac_at)\b")]
    private static partial Regex AudioEncodedToAac();

    [GeneratedRegex(@"-codec:v:0 (libx264|h264_[a-z0-9]+)\b")]
    private static partial Regex VideoEncodedToH264();

    [GeneratedRegex(@"-maxrate (\d+)")]
    private static partial Regex MaxRate();

    private async Task<Guid> SeedFilmAsync(
        string videoEncoder,
        string audioEncoder,
        bool secondAudioTrack = false,
        bool avi = false,
        int? videoBitrate = null,
        int? audioBitrate = null,
        string[]? tags = null)
    {
        var path = await EndToEndMediaFixtures.CreateFilmAsync(
            _workDir,
            $"film-{Guid.NewGuid():N}.{(avi ? "avi" : "mkv")}",
            FixtureSeconds,
            videoEncoder,
            audioEncoder,
            secondAudioTrack,
            avi ? "avi" : "matroska");

        var audioCodec = string.Equals(audioEncoder, Ac3, StringComparison.Ordinal) ? Ac3 : "aac";
        List<MediaStream> streams =
        [
            new MediaStream
            {
                Index = 0,
                Type = MediaStreamType.Video,
                Codec = string.Equals(videoEncoder, Mpeg4, StringComparison.Ordinal) ? Mpeg4 : "h264",
                Width = EndToEndMediaFixtures.Width,
                Height = EndToEndMediaFixtures.Height,
                IsDefault = true,
                IsAVC = avi ? false : null,
                BitRate = videoBitrate,
            },
            new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = audioCodec, Channels = 2, Language = "fre", IsDefault = !secondAudioTrack, BitRate = audioBitrate },
        ];
        if (secondAudioTrack)
        {
            streams.Add(new MediaStream { Index = 2, Type = MediaStreamType.Audio, Codec = audioCodec, Channels = 2, Language = "eng", IsDefault = true });
        }

        return LibraryItemSeeder.SeedVideo(
            _factory.Services.GetRequiredService<IItemWriteCommands>(),
            _mediaStreamRepository,
            path,
            avi ? "avi" : "mkv",
            streams,
            $"Consolidation transcode film {Guid.NewGuid()}",
            runTimeTicks: TimeSpan.FromSeconds(FixtureSeconds).Ticks,
            tags: tags);
    }

    private async Task<List<string>> FfmpegCommandsAsync(Guid mediaSourceId)
    {
        var commands = new List<string>();
        if (!Directory.Exists(_logDirectory))
        {
            return commands;
        }

        foreach (var log in Directory.EnumerateFiles(_logDirectory, "FFmpeg.*.log")
            .Where(path => Path.GetFileName(path).Contains(mediaSourceId.ToString("N"), StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal))
        {
            commands.Add(await File.ReadAllTextAsync(log, TestContext.Current.CancellationToken));
        }

        return commands;
    }

    /// <summary>
    /// Probes what the server produced: the initialisation segment followed by a media segment is
    /// a playable fragmented MP4.
    /// </summary>
    private async Task<IReadOnlyList<string>> ProbeProducedAsync(byte[] init, byte[] segment, bool withWidth)
    {
        Assert.NotEmpty(init);
        Assert.NotEmpty(segment);
        var produced = Path.Combine(_workDir, $"produced-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(produced, [.. init, .. segment], TestContext.Current.CancellationToken);
        var streams = await EndToEndMediaFixtures.ProbeStreamsAsync(produced);
        return withWidth
            ? streams
            : streams.Select(line => string.Join(',', line.Split(',').Take(2))).ToList();
    }

    private static Task<PlaybackSessionResponse> CreateSessionAsync(HttpClient client, Guid itemId, Guid userId, string playSessionId)
    {
        var (capabilities, constraints) = EndToEndCapabilityPresets.BrowserRemuxOverHls();
        return CreateSessionAsync(client, itemId, userId, playSessionId, capabilities, constraints);
    }

    private static async Task<PlaybackSessionResponse> CreateSessionAsync(
        HttpClient client,
        Guid itemId,
        Guid userId,
        string playSessionId,
        ClientCapabilities capabilities,
        PlaybackConstraints constraints,
        string? mediaSourceId = null)
    {
        var request = new CreatePlaybackSessionRequest(itemId, userId, capabilities, constraints, MediaSourceId: mediaSourceId, PlaySessionId: playSessionId);
        using var response = await client.PostAsJsonAsync("Playback/Sessions", request, JsonDefaults.Options, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, FormattableString.Invariant($"POST Playback/Sessions: {(int)response.StatusCode} {body}"));
        return JsonSerializer.Deserialize<PlaybackSessionResponse>(body, JsonDefaults.Options)!;
    }

    private static async Task<PlaybackSessionStreamDescriptor> GetDescriptorAsync(HttpClient client, Guid sessionId)
    {
        using var response = await client.GetAsync(FormattableString.Invariant($"Playback/Sessions/{sessionId:N}/Stream?startTimeTicks=0"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, FormattableString.Invariant($"GET .../Stream: {(int)response.StatusCode} {body}"));
        return JsonSerializer.Deserialize<PlaybackSessionStreamDescriptor>(body, JsonDefaults.Options)!;
    }

    private static async Task StopAsync(HttpClient client, Guid sessionId)
    {
        using var stop = await client.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{sessionId:N}"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
    }

    /// <summary>
    /// Reads the media playlist and resolves what it names against its own url, as a player does.
    /// </summary>
    private static async Task<(string Init, List<string> Segments)> ReadPlaylistAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, FormattableString.Invariant($"GET '{url}': {(int)response.StatusCode} {body}"));
        Assert.StartsWith("#EXTM3U", body, StringComparison.Ordinal);

        var directory = url[..(url.IndexOf('?', StringComparison.Ordinal) is var query and >= 0 ? query : url.Length)];
        directory = directory[..(directory.LastIndexOf('/') + 1)];
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var map = Assert.Single(lines, line => line.StartsWith("#EXT-X-MAP:URI=\"", StringComparison.Ordinal));
        var init = directory + map["#EXT-X-MAP:URI=\"".Length..map.LastIndexOf('"')];
        var segments = lines.Where(line => !line.StartsWith('#')).Select(line => directory + line).ToList();
        Assert.NotEmpty(segments);
        Assert.All(segments.Append(init), segment => Assert.Contains("/Playback/Sessions/", segment, StringComparison.Ordinal));
        return (init, segments);
    }

    private static async Task<byte[]> GetBytesAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, FormattableString.Invariant($"GET '{url}': {(int)response.StatusCode} {System.Text.Encoding.UTF8.GetString(body.AsSpan(0, Math.Min(body.Length, 300)))}"));
        return body;
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (var attempt = 0; attempt < 150; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        Assert.Fail("Timed out waiting for " + what + ".");
    }

    private static async Task<string> MintMediaCapabilityAsync(HttpClient client, Guid itemId, string mediaSourceId, string playSessionId)
    {
        using var response = await client.PostAsJsonAsync(
            "Playback/Capabilities",
            new { PlaySessionId = playSessionId, ItemId = itemId, MediaSourceId = mediaSourceId, Scopes = new[] { "Media" } },
            JsonDefaults.Options,
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, FormattableString.Invariant($"POST Playback/Capabilities: {(int)response.StatusCode} {body}"));
        return JsonNode.Parse(body)!["Value"]!.GetValue<string>();
    }

    private async Task UpdatePolicyAsync(Guid userId, Action<JsonObject> change)
    {
        var policy = JsonNode.Parse(await _admin.GetStringAsync(FormattableString.Invariant($"Users/{userId}"), TestContext.Current.CancellationToken))!["Policy"]!.AsObject();
        change(policy);
        using var content = new StringContent(policy.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var saved = await _admin.PostAsync(FormattableString.Invariant($"Users/{userId}/Policy"), content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
    }

    /// <summary>
    /// Signs an existing user in again from another device: a second token, a second device.
    /// </summary>
    private async Task<HttpClient> SignInAgainAsync(string name, string deviceId)
    {
        using var anonymous = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, MediaBoundaryFixture.AuthorizationHeader(deviceId, null));
        request.Content = JsonContent.Create(new { Username = name, Pw = Password }, options: JsonDefaults.Options);
        using var authenticated = await anonymous.SendAsync(request, TestContext.Current.CancellationToken);
        authenticated.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await authenticated.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var client = _factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(120);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            AuthHelper.AuthHeaderName,
            MediaBoundaryFixture.AuthorizationHeader(deviceId, document.RootElement.GetProperty("AccessToken").GetString()));
        return client;
    }

    private async Task<(HttpClient Client, Guid UserId)> SignInAsync(string name)
    {
        UserDto created;
        using (var response = await _admin.PostAsJsonAsync("/Users/New", new { Name = name, Password }, JsonDefaults.Options, TestContext.Current.CancellationToken))
        {
            response.EnsureSuccessStatusCode();
            created = (await response.Content.ReadFromJsonAsync<UserDto>(JsonDefaults.Options, TestContext.Current.CancellationToken))!;
        }

        var deviceId = "device-" + name;
        using var anonymous = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, MediaBoundaryFixture.AuthorizationHeader(deviceId, null));
        request.Content = JsonContent.Create(new { Username = name, Pw = Password }, options: JsonDefaults.Options);
        using var authenticated = await anonymous.SendAsync(request, TestContext.Current.CancellationToken);
        authenticated.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await authenticated.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var client = _factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(120);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            AuthHelper.AuthHeaderName,
            MediaBoundaryFixture.AuthorizationHeader(deviceId, document.RootElement.GetProperty("AccessToken").GetString()));
        return (client, created.Id);
    }
}
