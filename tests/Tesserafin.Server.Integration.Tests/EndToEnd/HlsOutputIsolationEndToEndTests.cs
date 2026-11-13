using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Persistence;
using Tesserafin.Extensions.Json;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Playback.Decision;
using Tesserafin.Server.Integration.Tests.PlaybackCredentials;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// POLISH-2-R4, over HTTP: two users with their own tokens, a local file, a real ffmpeg, and the
/// routes a player uses. What a released transcode leaves on its output, and whose transcodes
/// <c>DELETE Videos/ActiveEncodings</c> reaches.
/// </summary>
/// <remarks>
/// The second user never holds the first user's credential. It sends the first user's URL - the
/// path, the device id, the play session id, the User-Agent - under its own token, from its own
/// device. That is all the server derives an output's name from, and all a stop selects by.
/// </remarks>
public sealed class HlsOutputIsolationEndToEndTests : IClassFixture<HlsIsolationApplicationFactory>, IAsyncLifetime
{
    private const string OwnerDevice = "p2r4-owner-device";
    private const string OtherDevice = "p2r4-other-device";
    private const string Player = "p2r4-player/1.0";

    // Several segments' worth: a segment is only served ahead of the transcode's end once the next one is there.
    private const int Seconds = 20;

    private static readonly byte[] _mark = Encoding.ASCII.GetBytes("\n-- POLISH-2-R4: on disk before the second user asked --\n");

    private static readonly MediaStream[] _streams =
    [
        new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = EndToEndCapabilityPresets.FixtureVideoCodec, Width = EndToEndMediaFixtures.Width, Height = EndToEndMediaFixtures.Height, IsDefault = true },
        new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = EndToEndCapabilityPresets.FixtureAudioCodec, Channels = 2, IsDefault = true },
    ];

    private readonly HlsIsolationApplicationFactory _factory;
    private readonly ConcurrentQueue<TranscodingJob> _started = new();
    private HttpClient _owner = null!;
    private HttpClient _other = null!;
    private Guid _ownerId;
    private Guid _otherId;
    private Guid _item;
    private string _workDir = null!;
    private string _transcodes = null!;
    private ITranscodeManager _transcodeManager = null!;
    private FailingRemovals _fileSystem = null!;

    public HlsOutputIsolationEndToEndTests(HlsIsolationApplicationFactory factory)
    {
        _factory = factory;
    }

    public async ValueTask InitializeAsync()
    {
        var (adminToken, _) = await _factory.EnsureAuthenticatedAsync();
        using var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.AddAuthHeader(adminToken);

        // Two ordinary accounts, each signed in on its own device.
        var suffix = Guid.NewGuid().ToString("N")[..8];
        (_owner, _ownerId) = await SignInAsync(admin, "p2r4-owner-" + suffix, OwnerDevice + suffix);
        (_other, _otherId) = await SignInAsync(admin, "p2r4-other-" + suffix, OtherDevice + suffix);

        _workDir = Directory.CreateTempSubdirectory("tesserafin-p2r4-e2e-").FullName;
        var fixture = await EndToEndMediaFixtures.CreateH264AacMp4Async(_workDir, durationSeconds: Seconds);
        // These assertions exercise the legacy Videos routes, including a foreign caller
        // creating its own job from a released URL. A session-owned film/episode URL instead
        // refuses that caller; its isolation is covered by RemuxFilmEndToEndTests.
        _item = LibraryItemSeeder.SeedVideo(
            _factory.Services.GetRequiredService<ILibraryManager>(),
            _factory.Services.GetRequiredService<IMediaStreamRepository>(),
            fixture,
            "mp4",
            _streams,
            "POLISH-2-R4 fixture " + suffix,
            Seconds * TimeSpan.TicksPerSecond,
            film: false);

        _transcodes = _factory.Services.GetRequiredService<IServerConfigurationManager>().GetTranscodePath();
        _fileSystem = (FailingRemovals)_factory.Services.GetRequiredService<IFileSystem>();
        _transcodeManager = _factory.Services.GetRequiredService<ITranscodeManager>();
        _transcodeManager.TranscodingJobStarted += OnStarted;
    }

    public ValueTask DisposeAsync()
    {
        _transcodeManager.TranscodingJobStarted -= OnStarted;
        _fileSystem.FailDeferredRemovalsUnder = null;
        _owner.Dispose();
        _other.Dispose();
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The first user plays and stops. The removal of its files fails, so they stay, with no job
    /// on the output. The second user then sends the first user's segment URL.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task SegmentUrlOfAReleasedTranscode_ReplayedByAnotherUser_IsNotAnsweredFromWhatWasLeft()
    {
        var playSession = "p2r4-residual-" + Guid.NewGuid().ToString("N");
        var segmentUrls = await PlayToSegmentsAsync(_owner, _ownerId, playSession);
        Assert.True(segmentUrls.Count >= 3, "the fixture is too short to be several segments");
        var segmentUrl = segmentUrls[0];
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(_owner, segmentUrl)).Status);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(_owner, segmentUrls[1])).Status);
        var job = Assert.Single(_started, j => j.PlaySessionId == playSession);
        var segmentFile = Directory.GetFiles(_transcodes, Path.GetFileNameWithoutExtension(job.Path!) + "0.*").Single();

        // While its job is registered the first user's output is refused to anybody else.
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(_other, segmentUrl)).Status);

        // Stopped by its owner, through the route a player uses; and the removal fails.
        _fileSystem.FailDeferredRemovalsUnder = _transcodes;
        var failedBefore = _fileSystem.Failed;
        using (var stopped = await _owner.DeleteAsync(FormattableString.Invariant($"Videos/ActiveEncodings?deviceId={OwnerDevice}&playSessionId={playSession}"), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, stopped.StatusCode);
        }

        await EventuallyAsync(() => _fileSystem.Failed > failedBefore, "the removal of the released transcode's files was never attempted");
        Assert.Null(_transcodeManager.GetTranscodingJob(job.Path!, TranscodingJobType.Hls));
        Assert.True(File.Exists(segmentFile), "the released transcode's segment is not on disk: this run shows nothing");

        // An encoder is deterministic: the same command writes the same bytes, and a second
        // transcode could not be told from the first by its output. So the file the first user's
        // transcode left is marked, where it lies, before the second user asks.
        await File.AppendAllBytesAsync(segmentFile, _mark, TestContext.Current.CancellationToken);

        var replay = await GetAsync(_other, segmentUrl);

        Assert.False(
            replay.Body.AsSpan().IndexOf(_mark) >= 0,
            FormattableString.Invariant($"another user's replay was answered {(int)replay.Status} with {replay.Body.Length} bytes carrying the mark: the file the first user's transcode left on disk"));
        Assert.Equal(HttpStatusCode.OK, replay.Status);
        Assert.Equal(0x47, replay.Body[0]);

        // Its own transcode, on an output that is now its own.
        var successor = Assert.Single(_started, j => j.PlaySessionId == playSession && !ReferenceEquals(j, job));
        Assert.Equal(job.Path, successor.Path);
        Assert.Equal(_otherId, successor.UserId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(_owner, segmentUrl)).Status);
    }

    /// <summary>
    /// Two users' transcodes under one play session id, registered in either order, and the first
    /// user's <c>DELETE Videos/ActiveEncodings</c>.
    /// </summary>
    /// <param name="callersFirst">Whether the caller's own job is the first one registered.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Theory(Timeout = 120_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StopOnAPlaySessionTwoUsersNamed_StopsTheCallersTranscodeAndOnlyThat(bool callersFirst)
    {
        // The second user borrows the play session id for a playback of its own: another
        // User-Agent, so another output, and a job of its own under the same id.
        var playSession = "p2r4-stop-" + Guid.NewGuid().ToString("N");
        TranscodingJob mine, theirs;
        if (callersFirst)
        {
            mine = await PlayAsync(_owner, _ownerId, playSession, Player);
            theirs = await PlayAsync(_other, _otherId, playSession, "another-player/2.0");
        }
        else
        {
            theirs = await PlayAsync(_other, _otherId, playSession, "another-player/2.0");
            mine = await PlayAsync(_owner, _ownerId, playSession, Player);
        }

        Assert.NotEqual(mine.Path, theirs.Path);
        Assert.Equal(_ownerId, mine.UserId);
        Assert.Equal(_otherId, theirs.UserId);

        using (var stopped = await _owner.DeleteAsync(FormattableString.Invariant($"Videos/ActiveEncodings?deviceId={OwnerDevice}&playSessionId={playSession}"), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, stopped.StatusCode);
        }

        Assert.Same(theirs, _transcodeManager.GetTranscodingJob(theirs.Path!, TranscodingJobType.Hls));
        Assert.Null(_transcodeManager.GetTranscodingJob(mine.Path!, TranscodingJobType.Hls));

        // Again, and with what is not the caller's: the same answer, and nothing more is stopped.
        foreach (var query in new[] { $"deviceId={OwnerDevice}&playSessionId={playSession}", $"deviceId={theirs.DeviceId}&playSessionId={playSession}" })
        {
            using var again = await _owner.DeleteAsync("Videos/ActiveEncodings?" + query, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        }

        Assert.Same(theirs, _transcodeManager.GetTranscodingJob(theirs.Path!, TranscodingJobType.Hls));

        // Its owner can still stop it.
        using (var theirStop = await _other.DeleteAsync(FormattableString.Invariant($"Videos/ActiveEncodings?deviceId=x&playSessionId={playSession}"), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, theirStop.StatusCode);
        }

        Assert.Null(_transcodeManager.GetTranscodingJob(theirs.Path!, TranscodingJobType.Hls));
    }

    /// <summary>
    /// The route's two identifiers are required: a request without one is refused before anything is selected.
    /// </summary>
    /// <param name="query">The query string.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Theory]
    [InlineData("")]
    [InlineData("?deviceId=a-device")]
    [InlineData("?playSessionId=a-play-session")]
    [InlineData("?deviceId=a-device&playSessionId=")]
    public async Task StopWithoutBothIdentifiers_IsABadRequest(string query)
    {
        using var response = await _owner.DeleteAsync("Videos/ActiveEncodings" + query, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task EventuallyAsync(Func<bool> condition, string otherwise)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, otherwise);
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<(HttpStatusCode Status, byte[] Body)> GetAsync(HttpClient client, string url, string? userAgent = Player)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    private static List<string> Uris(string manifest, string baseUrl)
        => manifest.Split('\n')
            .Select(l => l.Trim('\r', ' '))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => Uri.TryCreate(l, UriKind.Absolute, out _) ? l : baseUrl[..(baseUrl.LastIndexOf('/') + 1)] + l)
            .ToList();

    private void OnStarted(object? sender, TranscodingJob job) => _started.Enqueue(job);

    private async Task<(HttpClient Client, Guid UserId)> SignInAsync(HttpClient admin, string name, string deviceId)
    {
        const string Password = "p2r4-password";
        UserDto created;
        using (var response = await admin.PostAsJsonAsync("/Users/New", new { Name = name, Password }, JsonDefaults.Options, TestContext.Current.CancellationToken))
        {
            response.EnsureSuccessStatusCode();
            created = (await response.Content.ReadFromJsonAsync<UserDto>(JsonDefaults.Options, TestContext.Current.CancellationToken))!;
        }

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

    /// <summary>
    /// Plans a transcode and follows its playlists to the URLs of its segments, as a player does.
    /// </summary>
    private async Task<List<string>> PlayToSegmentsAsync(HttpClient client, Guid userId, string playSession)
    {
        var (capabilities, constraints) = EndToEndCapabilityPresets.TranscodeHls();
        using var planned = await client.PostAsJsonAsync(
            "Playback/Sessions",
            new CreatePlaybackSessionRequest(_item, userId, capabilities, constraints, MediaSourceId: null, PlaySessionId: playSession),
            JsonDefaults.Options,
            TestContext.Current.CancellationToken);
        var body = await planned.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(planned.IsSuccessStatusCode, $"POST Playback/Sessions: {(int)planned.StatusCode} {body}");
        var session = JsonSerializer.Deserialize<PlaybackSessionResponse>(body, JsonDefaults.Options)!;
        Assert.Equal(PlaybackMethod.Transcode, session.Method);

        var descriptor = (await client.GetFromJsonAsync<PlaybackSessionStreamDescriptor>($"Playback/Sessions/{session.Id}/Stream", JsonDefaults.Options, TestContext.Current.CancellationToken))!;
        var master = await GetAsync(client, descriptor.Url);
        Assert.Equal(HttpStatusCode.OK, master.Status);
        var variantUrl = Uris(Encoding.UTF8.GetString(master.Body), descriptor.Url)[0];
        var variant = await GetAsync(client, variantUrl);
        Assert.Equal(HttpStatusCode.OK, variant.Status);
        return Uris(Encoding.UTF8.GetString(variant.Body), variantUrl);
    }

    /// <summary>
    /// Starts a transcode under <paramref name="playSession"/> and returns its job.
    /// </summary>
    private async Task<TranscodingJob> PlayAsync(HttpClient client, Guid userId, string playSession, string userAgent)
    {
        var known = _started.ToArray();
        var segmentUrl = (await PlayToSegmentsAsync(client, userId, playSession))[0];
        var segment = await GetAsync(client, segmentUrl, userAgent);
        Assert.True(segment.Status == HttpStatusCode.OK, $"GET {segmentUrl}: {(int)segment.Status} {Encoding.UTF8.GetString(segment.Body)}");
        return Assert.Single(_started, j => j.PlaySessionId == playSession && !known.Contains(j));
    }
}
