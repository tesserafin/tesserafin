using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.TV;
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
/// Consolidation step 1, second journey (docs/playback-path-inventory.md §5): a film whose codecs
/// the client decodes but whose container it does not is remuxed - both streams copied - by the v2
/// engine's decision alone, and executed from the retained plan through the session's own HLS
/// route. Runs on the same host as <see cref="DirectPlayFilmEndToEndTests"/>: the legacy planner
/// (<c>StreamBuilder</c>) and the live-stream resolver (the only caller of
/// <c>PlaybackExecutionPlanAdapter.ToStreamInfo</c>) throw when reached, so a request of this
/// journey that still used the legacy chain fails with a 500. FFmpeg is real: the copy is proven
/// from the command the server ran.
/// </summary>
/// <remarks>
/// Not <c>Category=Smoke</c>, and required by name in the hosted integration job, for the reasons
/// given on <see cref="DirectPlayFilmEndToEndTests"/>.
/// </remarks>
public sealed partial class RemuxFilmEndToEndTests : IClassFixture<DirectPlayFilmEndToEndTests.LegacyFreeFactory>, IAsyncLifetime
{
    private const int FixtureSeconds = 18;
    private const string Password = "remux-password";

    private static readonly MediaStream[] _streams =
    [
        new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = EndToEndCapabilityPresets.FixtureVideoCodec, Width = EndToEndMediaFixtures.Width, Height = EndToEndMediaFixtures.Height, IsDefault = true },
        new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = EndToEndCapabilityPresets.FixtureAudioCodec, Channels = 2, IsDefault = true },
    ];

    private static readonly MediaStream[] _twoAudioStreams =
    [
        new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = EndToEndCapabilityPresets.FixtureVideoCodec, Width = EndToEndMediaFixtures.Width, Height = EndToEndMediaFixtures.Height, IsDefault = true },
        new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = EndToEndCapabilityPresets.FixtureAudioCodec, Channels = 2, Language = "fre" },
        new MediaStream { Index = 2, Type = MediaStreamType.Audio, Codec = EndToEndCapabilityPresets.FixtureAudioCodec, Channels = 2, Language = "eng", IsDefault = true },
    ];

    private readonly DirectPlayFilmEndToEndTests.LegacyFreeFactory _factory;
    private HttpClient _admin = null!;
    private string _workDir = null!;
    private string _fixturePath = null!;
    private ILibraryManager _libraryManager = null!;
    private IMediaStreamRepository _mediaStreamRepository = null!;
    private IServerConfigurationManager _configManager = null!;
    private ITranscodeManager _transcodeManager = null!;
    private IPlaybackSessionManager _sessionManager = null!;
    private string _logDirectory = null!;

    public RemuxFilmEndToEndTests(DirectPlayFilmEndToEndTests.LegacyFreeFactory factory)
    {
        _factory = factory;
    }

    public async ValueTask InitializeAsync()
    {
        _admin = _factory.CreateClient();
        _admin.Timeout = TimeSpan.FromSeconds(90);
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
        _workDir = Directory.CreateTempSubdirectory("consol-remux-e2e-").FullName;
        _fixturePath = await EndToEndMediaFixtures.CreateH264AacMkvAsync(_workDir, durationSeconds: FixtureSeconds, keyframeEverySecond: true);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemuxFilm_StartSeekStop_CopiesBothStreamsInOneSessionAndOneJob(bool episode)
    {
        const string PlaySession = "remux-start-ps";
        var (client, userId) = await SignInAsync($"remux-start-{episode}");
        var itemId = SeedFilm(_fixturePath, _streams, episode: episode);
        var session = await CreateSessionAsync(client, itemId, userId, PlaySession);

        // Decision: the engine's, and a remux into what the client declared first (fMP4 over HLS).
        Assert.Equal(PlaybackMethod.Remux, session.Method);
        Assert.Equal(PlaybackEngine.EngineVersion, session.DecisionVersion);
        Assert.Contains(TransformKind.RemuxContainer, session.Transforms);
        Assert.Contains(TransformKind.CopyVideo, session.Transforms);
        Assert.Contains(TransformKind.CopyAudio, session.Transforms);
        Assert.DoesNotContain(TransformKind.TranscodeVideo, session.Transforms);
        Assert.DoesNotContain(TransformKind.TranscodeAudio, session.Transforms);

        // Descriptor: minted from the retained plan; it names the session's route and nothing else.
        var descriptor = await GetDescriptorAsync(client, session.Id);
        Assert.StartsWith(FormattableString.Invariant($"/Playback/Sessions/{session.Id:N}/Hls/main.m3u8?"), descriptor.Url, StringComparison.Ordinal);
        Assert.Equal(StreamingProtocol.Hls, descriptor.Protocol);
        Assert.Equal("mp4", descriptor.Container);
        Assert.Contains("mpegurl", descriptor.MimeType, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PlaybackEngine.EngineVersion, descriptor.ServedBy);
        Assert.Null(descriptor.FallbackReason);
        foreach (var legacyParameter in new[] { "ApiKey", "VideoCodec", "AudioCodec", "Static", "TranscodeReasons", "SegmentContainer" })
        {
            Assert.DoesNotContain(legacyParameter, descriptor.Url, StringComparison.OrdinalIgnoreCase);
        }

        // Start: the playlist, the initialisation segment and the first segment.
        var playlist = await ReadPlaylistAsync(client, descriptor.Url);
        Assert.True(playlist.Segments.Count >= 3, FormattableString.Invariant($"expected several segments, got {playlist.Segments.Count}"));
        Assert.All(playlist.Segments.Append(playlist.Init), url => Assert.Contains(FormattableString.Invariant($"/Playback/Sessions/{session.Id:N}/Hls/hls1/main/"), url, StringComparison.Ordinal));
        var init = await GetBytesAsync(client, playlist.Init);
        var first = await GetBytesAsync(client, playlist.Segments[0]);

        // Seek: the last segment, then back to the middle.
        var last = await GetBytesAsync(client, playlist.Segments[^1]);
        var middle = await GetBytesAsync(client, playlist.Segments[playlist.Segments.Count / 2]);
        Assert.All(new[] { init, first, last, middle }, bytes => Assert.NotEmpty(bytes));

        // What was produced is fragmented MP4, not the Matroska source under another name.
        var produced = Path.Combine(_workDir, "produced.mp4");
        await File.WriteAllBytesAsync(produced, [.. init, .. first], TestContext.Current.CancellationToken);
        Assert.Contains("mp4", await EndToEndMediaFixtures.ProbeFormatNameAsync(produced), StringComparison.Ordinal);

        // FFmpeg copied: one job for the whole playback, start and seeks included, and its
        // command copies both streams. The server names the log by what the command does.
        var logs = FfmpegLogs(itemId);
        var log = Assert.Single(logs);
        Assert.StartsWith("FFmpeg.Remux-", Path.GetFileName(log), StringComparison.Ordinal);
        var command = await File.ReadAllTextAsync(log, TestContext.Current.CancellationToken);
        Assert.Contains("-codec:v:0 copy", command, StringComparison.Ordinal);
        Assert.Contains("-codec:a:0 copy", command, StringComparison.Ordinal);
        Assert.DoesNotContain("-codec:v:0 lib", command, StringComparison.Ordinal);
        Assert.DoesNotContain("-codec:a:0 aac", command, StringComparison.Ordinal);

        // One session, the one the client created, still holding the engine's decision.
        var tracked = Assert.Single(_sessionManager.GetAll(), s => string.Equals(s.PlaySessionId, PlaySession, StringComparison.Ordinal));
        Assert.Equal(session.Id, tracked.Id.Value);
        Assert.Null(tracked.Plan.StreamInfo);
        var job = _transcodeManager.GetTranscodingJob(PlaySession);
        Assert.NotNull(job);
        Assert.Equal(userId, job.UserId);
        var jobPlaylist = job.Path!;

        // Stop: the session, its job and its output all go.
        using (var stop = await client.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{session.Id:N}"), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, descriptor.Url));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, playlist.Segments[0]));
        Assert.Null(_transcodeManager.GetTranscodingJob(PlaySession));
        Assert.DoesNotContain(_sessionManager.GetAll(), s => string.Equals(s.PlaySessionId, PlaySession, StringComparison.Ordinal));
        await WaitUntilAsync(() => !File.Exists(jobPlaylist) && !Directory.EnumerateFiles(Path.GetDirectoryName(jobPlaylist)!, Path.GetFileNameWithoutExtension(jobPlaylist) + "*").Any(), "the job's output to be removed");
        Assert.Single(FfmpegLogs(itemId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemuxFilm_MediaElementCredential_ReadsOnlyWhileTheSessionLives(bool episode)
    {
        const string PlaySession = "remux-capability-ps";
        var (client, userId) = await SignInAsync($"remux-capability-{episode}");
        var itemId = SeedFilm(_fixturePath, _streams, episode: episode);
        var session = await CreateSessionAsync(client, itemId, userId, PlaySession);
        var descriptor = await GetDescriptorAsync(client, session.Id);
        var capability = await MintMediaCapabilityAsync(client, itemId, itemId.ToString("N"), PlaySession);

        // A media element sends no header: the capability in the playlist url is carried into
        // every segment url the playlist names.
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(anonymous, descriptor.Url));
        var playlist = await ReadPlaylistAsync(anonymous, descriptor.Url + "&playbackCapability=" + Uri.EscapeDataString(capability));
        Assert.Contains("playbackCapability=", playlist.Segments[0], StringComparison.Ordinal);
        Assert.NotEmpty(await GetBytesAsync(anonymous, playlist.Init));
        Assert.NotEmpty(await GetBytesAsync(anonymous, playlist.Segments[0]));

        using (var stop = await client.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{session.Id:N}"), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        }

        // Revoked with the session: the same urls are refused at authentication.
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(anonymous, playlist.Segments[0]));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(anonymous, playlist.Init));
    }

    [Fact]
    public async Task RemuxFilm_AnotherUser_CanNeitherReadNorStopNorTakeTheJob()
    {
        const string PlaySession = "remux-owner-ps";
        var (owner, ownerId) = await SignInAsync("remux-owner");
        var (intruder, intruderId) = await SignInAsync("remux-intruder");
        var itemId = SeedFilm(_fixturePath, _streams);
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

        // While the owner's job runs under this play session id, the intruder cannot even mint a
        // capability for it (#301): there is no credential to present to the session's route.
        using (var mint = await intruder.PostAsJsonAsync(
            "Playback/Capabilities",
            new { PlaySessionId = PlaySession, ItemId = itemId, MediaSourceId = itemId.ToString("N"), Scopes = new[] { "Media" } },
            JsonDefaults.Options,
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, mint.StatusCode);
        }

        // An administrator is not the owner either: the job a segment request starts belongs to
        // its caller, and only the owner's jobs end with the session.
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(_admin, playlist.Segments[0]));

        using (var stop = await intruder.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{session.Id:N}"), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, stop.StatusCode);
        }

        // A session of the intruder's own under the same play session id is a second session with
        // its own job; stopping it leaves the owner's playing.
        var intruderSession = await CreateSessionAsync(intruder, itemId, intruderId, PlaySession);
        Assert.NotEqual(session.Id, intruderSession.Id);
        using (var stop = await intruder.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{intruderSession.Id:N}"), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        }

        Assert.Equal(ownerId, _transcodeManager.GetTranscodingJob(PlaySession)?.UserId);
        Assert.NotEmpty(await GetBytesAsync(owner, playlist.Segments[^1]));
        Assert.Single(FfmpegLogs(itemId));
    }

    [Fact]
    public async Task RemuxFilm_AlternateVersionWithdrawn_StopsServingAndCannotBePlannedAgain()
    {
        const string Tag = "remux-version-withdrawn";
        var (client, userId) = await SignInAsync("remux-version");
        var (itemId, versionId) = await SeedFilmWithAlternateVersionAsync(versionTags: [Tag]);
        var session = await CreateSessionAsync(client, itemId, userId, "remux-version-ps", versionId.ToString("N"));
        Assert.Equal(PlaybackMethod.Remux, session.Method);

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

        // Nor can the withdrawn version be planned again.
        var (capabilities, constraints) = EndToEndCapabilityPresets.BrowserRemuxOverHls();
        using var replan = await client.PostAsJsonAsync(
            "Playback/Sessions",
            new CreatePlaybackSessionRequest(itemId, userId, capabilities, constraints, MediaSourceId: versionId.ToString("N"), PlaySessionId: "remux-version-ps-2"),
            JsonDefaults.Options,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, replan.StatusCode);
    }

    [Fact]
    public async Task RemuxFilm_VersionsWithDifferentTrackIndices_EachIsCopiedWithItsOwn()
    {
        var (client, userId) = await SignInAsync("remux-tracks");
        var (itemId, versionId) = await SeedFilmWithAlternateVersionAsync(versionTags: null);

        // The first version has one audio track (index 1); the second has two, and its default is
        // index 2. The client names no track: each version is planned with its own.
        var primary = await CreateSessionAsync(client, itemId, userId, "remux-tracks-1", itemId.ToString("N"));
        var version = await CreateSessionAsync(client, itemId, userId, "remux-tracks-2", versionId.ToString("N"));
        Assert.Equal(PlaybackMethod.Remux, primary.Method);
        Assert.Equal(PlaybackMethod.Remux, version.Method);
        Assert.Equal(1, primary.SelectedStreams.Audio);
        Assert.Equal(2, version.SelectedStreams.Audio);

        foreach (var (session, source, audio) in new[] { (primary, itemId, 1), (version, versionId, 2) })
        {
            var playlist = await ReadPlaylistAsync(client, (await GetDescriptorAsync(client, session.Id)).Url);
            Assert.NotEmpty(await GetBytesAsync(client, playlist.Init));
            Assert.NotEmpty(await GetBytesAsync(client, playlist.Segments[0]));
            var command = await File.ReadAllTextAsync(Assert.Single(FfmpegLogs(source)), TestContext.Current.CancellationToken);
            Assert.Contains(FormattableString.Invariant($"-map 0:{audio} "), command, StringComparison.Ordinal);
            Assert.Contains("-codec:a:0 copy", command, StringComparison.Ordinal);
            Assert.Contains("-codec:v:0 copy", command, StringComparison.Ordinal);
        }

        // An explicit choice of the client still wins over the version's default.
        var (capabilities, constraints) = EndToEndCapabilityPresets.BrowserRemuxOverHls();
        using var explicitChoice = await client.PostAsJsonAsync(
            "Playback/Sessions",
            new CreatePlaybackSessionRequest(itemId, userId, capabilities, constraints with { PreferredAudioStreamIndex = 1 }, MediaSourceId: versionId.ToString("N"), PlaySessionId: "remux-tracks-3"),
            JsonDefaults.Options,
            TestContext.Current.CancellationToken);
        var body = await explicitChoice.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(explicitChoice.IsSuccessStatusCode, body);
        Assert.Equal(1, JsonSerializer.Deserialize<PlaybackSessionResponse>(body, JsonDefaults.Options)!.SelectedStreams.Audio);
    }

    [Fact]
    public async Task RemuxFilm_Replanned_NeverServesThePreviousPlansOutput()
    {
        const string PlaySession = "remux-replan-ps";
        var (client, userId) = await SignInAsync("remux-replan");
        var (itemId, versionId) = await SeedFilmWithAlternateVersionAsync(versionTags: null);
        var source = versionId.ToString("N");
        var session = await CreateSessionAsync(client, itemId, userId, PlaySession, source);
        Assert.Equal(2, session.SelectedStreams.Audio);
        var playlist = await ReadPlaylistAsync(client, (await GetDescriptorAsync(client, session.Id)).Url);
        Assert.NotEmpty(await GetBytesAsync(client, playlist.Segments[0]));

        // Same session, same play session, another audio track.
        var (capabilities, constraints) = EndToEndCapabilityPresets.BrowserRemuxOverHls();
        using (var replan = await client.PutAsJsonAsync(
            FormattableString.Invariant($"Playback/Sessions/{session.Id:N}"),
            new ReplacePlaybackSessionRequest(itemId, userId, capabilities, constraints with { PreferredAudioStreamIndex = 1 }, MediaSourceId: source),
            JsonDefaults.Options,
            TestContext.Current.CancellationToken))
        {
            var body = await replan.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(replan.IsSuccessStatusCode, body);
            var replanned = JsonSerializer.Deserialize<PlaybackSessionResponse>(body, JsonDefaults.Options)!;
            Assert.Equal(session.Id, replanned.Id);
            Assert.Equal(PlaybackMethod.Remux, replanned.Method);
            Assert.Equal(1, replanned.SelectedStreams.Audio);
        }

        // The first job is gone with its output; what is served now was produced for the new plan.
        Assert.Null(_transcodeManager.GetTranscodingJob(PlaySession));
        var again = await ReadPlaylistAsync(client, (await GetDescriptorAsync(client, session.Id)).Url);
        Assert.NotEmpty(await GetBytesAsync(client, again.Init));
        Assert.NotEmpty(await GetBytesAsync(client, again.Segments[0]));
        var commands = new List<string>();
        foreach (var log in FfmpegLogs(versionId).Order(StringComparer.Ordinal))
        {
            commands.Add(await File.ReadAllTextAsync(log, TestContext.Current.CancellationToken));
        }

        Assert.Equal(2, commands.Count);
        Assert.Single(commands, command => command.Contains("-map 0:2 ", StringComparison.Ordinal));
        Assert.Single(commands, command => command.Contains("-map 0:1 ", StringComparison.Ordinal));
        Assert.All(commands, command => Assert.Contains("-codec:a:0 copy", command, StringComparison.Ordinal));
        Assert.Single(_sessionManager.GetAll(), s => string.Equals(s.PlaySessionId, PlaySession, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemuxFilm_PostedAgainUnderTheSamePlaySession_NeverServesThePreviousPlansOutput()
    {
        const string PlaySession = "remux-repost-ps";
        var (client, userId) = await SignInAsync("remux-repost");
        var (itemId, versionId) = await SeedFilmWithAlternateVersionAsync(versionTags: null);
        var source = versionId.ToString("N");
        var first = await CreateSessionAsync(client, itemId, userId, PlaySession, source);
        var playlist = await ReadPlaylistAsync(client, (await GetDescriptorAsync(client, first.Id)).Url);
        Assert.NotEmpty(await GetBytesAsync(client, playlist.Segments[0]));

        // A POST naming a play session that has a session replaces it in place.
        var (capabilities, constraints) = EndToEndCapabilityPresets.BrowserRemuxOverHls();
        using var repost = await client.PostAsJsonAsync(
            "Playback/Sessions",
            new CreatePlaybackSessionRequest(itemId, userId, capabilities, constraints with { PreferredAudioStreamIndex = 1 }, MediaSourceId: source, PlaySessionId: PlaySession),
            JsonDefaults.Options,
            TestContext.Current.CancellationToken);
        var body = await repost.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(repost.IsSuccessStatusCode, body);
        var second = JsonSerializer.Deserialize<PlaybackSessionResponse>(body, JsonDefaults.Options)!;
        Assert.Equal(1, second.SelectedStreams.Audio);

        Assert.Null(_transcodeManager.GetTranscodingJob(PlaySession));
        var again = await ReadPlaylistAsync(client, (await GetDescriptorAsync(client, second.Id)).Url);
        Assert.NotEmpty(await GetBytesAsync(client, again.Segments[0]));
        var logs = FfmpegLogs(versionId);
        Assert.Equal(2, logs.Count);
        var commands = new List<string>();
        foreach (var log in logs)
        {
            commands.Add(await File.ReadAllTextAsync(log, TestContext.Current.CancellationToken));
        }

        Assert.Single(commands, command => command.Contains("-map 0:1 ", StringComparison.Ordinal));
        Assert.Single(_sessionManager.GetAll(), s => string.Equals(s.PlaySessionId, PlaySession, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemuxFilm_ClientStreamOptionsAndForeignCapabilities_ChangeNothing()
    {
        const string PlaySession = "remux-options-ps";
        var (client, userId) = await SignInAsync("remux-options");
        var itemId = SeedFilm(_fixturePath, _streams);
        var otherItemId = SeedFilm(_fixturePath, _streams);
        var session = await CreateSessionAsync(client, itemId, userId, PlaySession);
        var descriptor = await GetDescriptorAsync(client, session.Id);

        // Codec conditions a client could add to the query are not read for a plan-built request:
        // the playlist and the segment are the copy the plan describes, not a refusal.
        var playlist = await ReadPlaylistAsync(client, descriptor.Url + "&h264-level=10&h264-profile=baseline&h264-videobitdepth=1&profile=baseline&level=10");
        Assert.NotEmpty(await GetBytesAsync(client, playlist.Segments[0]));
        var command = await File.ReadAllTextAsync(Assert.Single(FfmpegLogs(itemId)), TestContext.Current.CancellationToken);
        Assert.Contains("-codec:v:0 copy", command, StringComparison.Ordinal);

        // A capability minted for another item, or for another play session, opens nothing here.
        using var anonymous = _factory.CreateClient();
        var otherItem = await MintMediaCapabilityAsync(client, otherItemId, otherItemId.ToString("N"), PlaySession);
        var otherPlaySession = await MintMediaCapabilityAsync(client, itemId, itemId.ToString("N"), "another-play-session");
        Assert.NotEqual(HttpStatusCode.OK, await StatusAsync(anonymous, descriptor.Url + "&playbackCapability=" + Uri.EscapeDataString(otherItem)));
        Assert.NotEqual(HttpStatusCode.OK, await StatusAsync(anonymous, descriptor.Url + "&playbackCapability=" + Uri.EscapeDataString(otherPlaySession)));
    }

    /// <summary>
    /// The stop report stores the position, and a new session for the same film is a remux again
    /// whose playlist still describes the whole film: a resume is the player seeking in it, which
    /// the browser suite measures. Nothing server-side starts "from" a position on this route.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemuxFilm_StoppedPosition_IsStoredAndAResumedSessionServesFromIt(bool episode)
    {
        var configuration = _configManager.Configuration;
        var minResumeSeconds = configuration.MinResumeDurationSeconds;
        configuration.MinResumeDurationSeconds = 0;
        try
        {
            var (client, userId) = await SignInAsync($"remux-resume-{episode}");
            var itemId = SeedFilm(_fixturePath, _streams, episode: episode);
            var first = await CreateSessionAsync(client, itemId, userId, "remux-resume-1");
            var position = TimeSpan.FromSeconds(FixtureSeconds / 2).Ticks;

            await PostAsync(client, "Sessions/Playing", new { ItemId = itemId, MediaSourceId = itemId.ToString("N"), PlaySessionId = "remux-resume-1", PositionTicks = 0L, PlayMethod = "DirectStream" });
            await PostAsync(client, "Sessions/Playing/Stopped", new { ItemId = itemId, MediaSourceId = itemId.ToString("N"), PlaySessionId = "remux-resume-1", PositionTicks = position });
            using (var stop = await client.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{first.Id:N}"), TestContext.Current.CancellationToken))
            {
                // The stop report already ended a session nobody had read from; either answer
                // leaves nothing behind.
                Assert.Contains(stop.StatusCode, new[] { HttpStatusCode.NoContent, HttpStatusCode.NotFound });
            }

            var userData = await client.GetFromJsonAsync<UserItemDataDto>(FormattableString.Invariant($"UserItems/{itemId:N}/UserData"), JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal(position, userData!.PlaybackPositionTicks);

            // Resume: a new session, and the segment holding the stored position is served. The
            // playlist always describes the whole film; the player seeks in it.
            var resumed = await CreateSessionAsync(client, itemId, userId, "remux-resume-2");
            Assert.Equal(PlaybackMethod.Remux, resumed.Method);
            var playlist = await ReadPlaylistAsync(client, (await GetDescriptorAsync(client, resumed.Id, position)).Url);
            Assert.NotEmpty(await GetBytesAsync(client, playlist.Init));
            Assert.NotEmpty(await GetBytesAsync(client, playlist.Segments[playlist.Segments.Count / 2]));
        }
        finally
        {
            configuration.MinResumeDurationSeconds = minResumeSeconds;
        }
    }

    private Guid SeedFilm(string path, MediaStream[] streams, string[]? tags = null, bool episode = false) =>
        LibraryItemSeeder.SeedVideo(
            _libraryManager,
            _mediaStreamRepository,
            path,
            "mkv",
            streams,
            $"Consolidation remux film {Guid.NewGuid()}",
            runTimeTicks: TimeSpan.FromSeconds(FixtureSeconds).Ticks,
            tags: tags,
            video: episode ? new Episode { IndexNumber = 1, ParentIndexNumber = 1 } : null);

    private async Task<(Guid ItemId, Guid VersionId)> SeedFilmWithAlternateVersionAsync(string[]? versionTags)
    {
        var versionPath = await EndToEndMediaFixtures.CreateH264AacMkvAsync(
            _workDir,
            $"version-{Guid.NewGuid():N}.mkv",
            durationSeconds: FixtureSeconds,
            keyframeEverySecond: true,
            secondAudioTrack: true);
        var itemId = SeedFilm(_fixturePath, _streams);
        var versionId = SeedFilm(versionPath, _twoAudioStreams, versionTags);
        _libraryManager.UpsertLinkedChild(itemId, versionId, LinkedChildType.LinkedAlternateVersion);
        return (itemId, versionId);
    }

    private List<string> FfmpegLogs(Guid mediaSourceId)
        => Directory.Exists(_logDirectory)
            ? Directory.EnumerateFiles(_logDirectory, "FFmpeg.*.log")
                .Where(path => Path.GetFileName(path).Contains(mediaSourceId.ToString("N"), StringComparison.OrdinalIgnoreCase))
                .ToList()
            : [];

    private static async Task<PlaybackSessionResponse> CreateSessionAsync(HttpClient client, Guid itemId, Guid userId, string playSessionId, string? mediaSourceId = null)
    {
        var (capabilities, constraints) = EndToEndCapabilityPresets.BrowserRemuxOverHls();
        var request = new CreatePlaybackSessionRequest(itemId, userId, capabilities, constraints, MediaSourceId: mediaSourceId, PlaySessionId: playSessionId);
        using var response = await client.PostAsJsonAsync("Playback/Sessions", request, JsonDefaults.Options, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, FormattableString.Invariant($"POST Playback/Sessions: {(int)response.StatusCode} {body}"));
        return JsonSerializer.Deserialize<PlaybackSessionResponse>(body, JsonDefaults.Options)!;
    }

    private static async Task<PlaybackSessionStreamDescriptor> GetDescriptorAsync(HttpClient client, Guid sessionId, long startTimeTicks = 0)
    {
        using var response = await client.GetAsync(FormattableString.Invariant($"Playback/Sessions/{sessionId:N}/Stream?startTimeTicks={startTimeTicks}"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, FormattableString.Invariant($"GET .../Stream: {(int)response.StatusCode} {body}"));
        return JsonSerializer.Deserialize<PlaybackSessionStreamDescriptor>(body, JsonDefaults.Options)!;
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

    private static async Task PostAsync(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body, JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, FormattableString.Invariant($"POST {path}: {(int)response.StatusCode}"));
    }

    private async Task UpdatePolicyAsync(Guid userId, Action<JsonObject> change)
    {
        var policy = JsonNode.Parse(await _admin.GetStringAsync(FormattableString.Invariant($"Users/{userId}"), TestContext.Current.CancellationToken))!["Policy"]!.AsObject();
        change(policy);
        using var content = new StringContent(policy.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var saved = await _admin.PostAsync(FormattableString.Invariant($"Users/{userId}/Policy"), content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
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
        client.Timeout = TimeSpan.FromSeconds(90);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            AuthHelper.AuthHeaderName,
            MediaBoundaryFixture.AuthorizationHeader(deviceId, document.RootElement.GetProperty("AccessToken").GetString()));
        return (client, created.Id);
    }
}
