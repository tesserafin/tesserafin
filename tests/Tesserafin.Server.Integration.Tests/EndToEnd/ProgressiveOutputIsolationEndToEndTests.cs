using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http.Json;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Persistence;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Server.Integration.Tests.PlaybackCredentials;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// tesserafin#293, over HTTP: the progressive stream route, two users with their own tokens, a
/// local file and a real ffmpeg. The second user sends the first user's url - path, device id,
/// play session id, User-Agent - under its own token, from its own device.
/// </summary>
public sealed class ProgressiveOutputIsolationEndToEndTests : IClassFixture<HeldFfmpegApplicationFactory>, IAsyncLifetime
{
    private const string OwnerDevice = "p293-owner-device";
    private const string OtherDevice = "p293-other-device";
    private const string Player = "p293-player/1.0";
    private const int Seconds = 20;

    private static readonly byte[] _mark = Encoding.ASCII.GetBytes("\n-- #293: on disk before the second user asked --\n");

    private static readonly MediaStream[] _streams =
    [
        new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = EndToEndCapabilityPresets.FixtureVideoCodec, Width = EndToEndMediaFixtures.Width, Height = EndToEndMediaFixtures.Height, IsDefault = true },
        new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = EndToEndCapabilityPresets.FixtureAudioCodec, Channels = 2, IsDefault = true },
    ];

    private readonly HeldFfmpegApplicationFactory _factory;
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

    public ProgressiveOutputIsolationEndToEndTests(HeldFfmpegApplicationFactory factory)
    {
        _factory = factory;
    }

    public async ValueTask InitializeAsync()
    {
        var (adminToken, _) = await _factory.EnsureAuthenticatedAsync();
        using var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.AddAuthHeader(adminToken);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        (_owner, _ownerId) = await SignInAsync(admin, "p293-owner-" + suffix, OwnerDevice + suffix);
        (_other, _otherId) = await SignInAsync(admin, "p293-other-" + suffix, OtherDevice + suffix);

        _workDir = Directory.CreateTempSubdirectory("tesserafin-p293-e2e-").FullName;
        var fixture = await EndToEndMediaFixtures.CreateH264AacMp4Async(_workDir, durationSeconds: Seconds);
        _item = LibraryItemSeeder.SeedVideo(
            _factory.Services.GetRequiredService<IItemWriteCommands>(),
            _factory.Services.GetRequiredService<IMediaStreamRepository>(),
            fixture,
            "mp4",
            _streams,
            "#293 fixture " + suffix,
            Seconds * TimeSpan.TicksPerSecond);

        _transcodes = _factory.Services.GetRequiredService<IServerConfigurationManager>().GetTranscodePath();
        _fileSystem = (FailingRemovals)_factory.Services.GetRequiredService<IFileSystem>();
        _transcodeManager = _factory.Services.GetRequiredService<ITranscodeManager>();
        _transcodeManager.TranscodingJobStarted += OnStarted;
    }

    public ValueTask DisposeAsync()
    {
        _transcodeManager.TranscodingJobStarted -= OnStarted;
        _fileSystem.FailDeferredRemovalsUnder = null;
        File.Delete(_factory.HoldFile);
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
    /// The first user's transcode is running and the first user is still reading it.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task UrlOfARunningTranscode_ReplayedByAnotherUser_IsRefused()
    {
        var playSession = "p293-active-" + Guid.NewGuid().ToString("N");
        var url = StreamUrl(playSession);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", Player);
        using var owned = await _owner.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, owned.StatusCode);
        await using var reading = await owned.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0x47, reading.ReadByte());
        var job = Assert.Single(_started, j => j.PlaySessionId == playSession);
        Assert.Equal(_ownerId, job.UserId);

        var replay = await GetAsync(_other, url);
        using var head = new HttpRequestMessage(HttpMethod.Head, url);
        head.Headers.TryAddWithoutValidation("User-Agent", Player);
        using var headReplay = await _other.SendAsync(head, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, replay.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, headReplay.StatusCode);
        Assert.Single(_started, j => j.PlaySessionId == playSession);
    }

    /// <summary>
    /// The first user's transcode is released and the removal of its file fails, so the file stays
    /// with no job on it. The second user then sends the first user's url.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    /// <param name="audio">Whether the audio route is asked rather than the video route. The audio
    /// start does not wait for ffmpeg before opening the file, so only there is a residual left in
    /// place, rather than removed, read before ffmpeg has overwritten it.</param>
    [Theory(Timeout = 120_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UrlOfAReleasedTranscode_ReplayedByAnotherUser_IsNotAnsweredFromWhatWasLeft(bool audio)
    {
        var playSession = "p293-residual-" + Guid.NewGuid().ToString("N");
        var url = audio ? AudioUrl(playSession) : StreamUrl(playSession);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(_owner, url)).Status);
        var job = Assert.Single(_started, j => j.PlaySessionId == playSession);
        var outputFile = job.Path!;

        _fileSystem.FailDeferredRemovalsUnder = _transcodes;
        var failedBefore = _fileSystem.Failed;
        using (var stopped = await _owner.DeleteAsync(FormattableString.Invariant($"Videos/ActiveEncodings?deviceId={OwnerDevice}&playSessionId={playSession}"), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, stopped.StatusCode);
        }

        await EventuallyAsync(() => _fileSystem.Failed > failedBefore, "the removal of the released transcode's file was never attempted");
        Assert.Null(_transcodeManager.GetTranscodingJob(outputFile, TranscodingJobType.Progressive));
        Assert.True(File.Exists(outputFile), "the released transcode's file is not on disk: this run shows nothing");

        // An encoder is deterministic, so what the first user's transcode left is marked where it lies.
        await File.AppendAllBytesAsync(outputFile, _mark, TestContext.Current.CancellationToken);

        var replay = await GetAsync(_other, url);

        Assert.False(
            replay.Body.AsSpan().IndexOf(_mark) >= 0,
            FormattableString.Invariant($"another user's replay was answered {(int)replay.Status} with {replay.Body.Length} bytes carrying the mark: the file the first user's transcode left on disk"));
        Assert.Equal(HttpStatusCode.OK, replay.Status);
        Assert.NotEmpty(replay.Body);

        // Its own transcode, on an output that is now its own.
        var successor = Assert.Single(_started, j => j.PlaySessionId == playSession && !ReferenceEquals(j, job));
        Assert.Equal(outputFile, successor.Path);
        Assert.Equal(_otherId, successor.UserId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(_owner, url)).Status);
    }

    /// <summary>
    /// As above, with the second user's ffmpeg held before it starts: the server has decided what to
    /// serve, and ffmpeg has not yet overwritten what was left. Every byte left is the mark, so not
    /// one of them may reach the second user, and none may be on disk while ffmpeg is held.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    /// <param name="audio">Whether the audio route is asked rather than the video route.</param>
    [Theory(Timeout = 120_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UrlOfAReleasedTranscode_ReplayedWhileTheNewFfmpegIsHeld_IsServedNoByteOfWhatWasLeft(bool audio)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "ffmpeg is held by a shell script");
        var playSession = "p293-held-" + Guid.NewGuid().ToString("N");
        var url = audio ? AudioUrl(playSession) : StreamUrl(playSession);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(_owner, url)).Status);
        var job = Assert.Single(_started, j => j.PlaySessionId == playSession);
        var outputFile = job.Path!;

        _fileSystem.FailDeferredRemovalsUnder = _transcodes;
        var failedBefore = _fileSystem.Failed;
        using (var stopped = await _owner.DeleteAsync(FormattableString.Invariant($"Videos/ActiveEncodings?deviceId={OwnerDevice}&playSessionId={playSession}"), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, stopped.StatusCode);
        }

        await EventuallyAsync(() => _fileSystem.Failed > failedBefore, "the removal of the released transcode's file was never attempted");
        Assert.Null(_transcodeManager.GetTranscodingJob(outputFile, TranscodingJobType.Progressive));
        var left = new FileInfo(outputFile).Length;
        Assert.True(left > 0, "the released transcode left nothing on disk: this run shows nothing");
        await File.WriteAllBytesAsync(outputFile, Enumerable.Repeat(_mark, (int)(left / _mark.Length) + 1).SelectMany(m => m).ToArray(), TestContext.Current.CancellationToken);

        Task<(HttpStatusCode Status, byte[] Body)> replaying;
        bool leftOnDiskWhileHeld;
        await File.WriteAllBytesAsync(_factory.HoldFile, [], TestContext.Current.CancellationToken);
        try
        {
            replaying = GetAsync(_other, url);
            await EventuallyAsync(() => _started.Any(j => j.PlaySessionId == playSession && !ReferenceEquals(j, job)), "the second user's transcode was never started");

            // Held long enough for the video route's own second of waiting to have passed too.
            await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            leftOnDiskWhileHeld = File.Exists(outputFile);
        }
        finally
        {
            File.Delete(_factory.HoldFile);
        }

        var replay = await replaying;

        Assert.False(
            replay.Body.AsSpan().IndexOf(_mark) >= 0,
            FormattableString.Invariant($"another user's replay was answered {(int)replay.Status} with {replay.Body.Length} bytes carrying the mark: the file the first user's transcode left on disk, served while the new ffmpeg was held"));
        Assert.False(leftOnDiskWhileHeld, "while the new ffmpeg was held, what the first user's transcode left was still on disk");
        Assert.Equal(HttpStatusCode.OK, replay.Status);
        Assert.NotEmpty(replay.Body);
        Assert.Equal(_otherId, Assert.Single(_started, j => j.PlaySessionId == playSession && !ReferenceEquals(j, job)).UserId);
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

    private static async Task<(HttpStatusCode Status, byte[] Body)> GetAsync(HttpClient client, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", Player);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    private string StreamUrl(string playSession)
        => FormattableString.Invariant(
            $"Videos/{_item}/stream.ts?mediaSourceId={_item:N}&deviceId={OwnerDevice}&playSessionId={playSession}&videoCodec=h264&audioCodec=aac&videoBitRate=1500000&audioBitRate=128000&static=false");

    private string AudioUrl(string playSession)
        => FormattableString.Invariant(
            $"Audio/{_item}/stream.mp3?mediaSourceId={_item:N}&deviceId={OwnerDevice}&playSessionId={playSession}&audioCodec=mp3&audioBitRate=128000&static=false");

    private void OnStarted(object? sender, TranscodingJob job) => _started.Enqueue(job);

    private async Task<(HttpClient Client, Guid UserId)> SignInAsync(HttpClient admin, string name, string deviceId)
    {
        const string Password = "p293-password";
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
}
