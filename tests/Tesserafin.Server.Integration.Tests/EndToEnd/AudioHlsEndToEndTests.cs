using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
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
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Engine;
using Tesserafin.Server.Integration.Tests.PlaybackCredentials;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// Consolidation step 1, audio over HLS (docs/playback-path-inventory.md §7): a library audio track
/// read from a local file that the client does not play as it is - a codec it does not decode
/// (transcoded to AAC) or a codec it decodes in a container it does not (remuxed, the audio copied)
/// - is decided by the v2 engine alone and executed on the session's own HLS route, which builds an
/// audio-only encoding request from the retained plan. The host replaces the legacy planner and the live-stream resolver with
/// implementations that throw (<see cref="DirectPlayFilmEndToEndTests.LegacyFreeFactory"/>). What
/// the output is, is read from the FFmpeg command the server ran and from the segments it
/// produced, not from the decision's label.
/// </summary>
/// <remarks>
/// Not <c>Category=Smoke</c>, and required by name in the hosted integration job, for the reasons
/// given on <see cref="DirectPlayFilmEndToEndTests"/>.
/// </remarks>
public sealed class AudioHlsEndToEndTests : IClassFixture<DirectPlayFilmEndToEndTests.LegacyFreeFactory>, IAsyncLifetime
{
    private const int FixtureSeconds = 12;
    private const string Password = "audio-transcode-password";

    private readonly DirectPlayFilmEndToEndTests.LegacyFreeFactory _factory;
    private HttpClient _admin = null!;
    private string _workDir = null!;
    private ITranscodeManager _transcodeManager = null!;
    private IPlaybackSessionManager _sessionManager = null!;
    private string _logDirectory = null!;

    public AudioHlsEndToEndTests(DirectPlayFilmEndToEndTests.LegacyFreeFactory factory)
    {
        _factory = factory;
    }

    public async ValueTask InitializeAsync()
    {
        _admin = _factory.CreateClient();
        _admin.Timeout = TimeSpan.FromSeconds(120);
        var (accessToken, _) = await _factory.EnsureAuthenticatedAsync();
        _admin.DefaultRequestHeaders.AddAuthHeader(accessToken);
        _transcodeManager = _factory.Services.GetRequiredService<ITranscodeManager>();
        _sessionManager = _factory.Services.GetRequiredService<IPlaybackSessionManager>();
        _logDirectory = _factory.Services.GetRequiredService<IApplicationPaths>().LogDirectoryPath;

        // Decided by the engine even with the global mode left at its Legacy default.
        _factory.Services.GetRequiredService<IServerConfigurationManager>().Configuration.PlaybackShadow.Mode = PlaybackEngineMode.Legacy;
        _workDir = Directory.CreateTempSubdirectory("consol-audio-transcode-e2e-").FullName;
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
    public Task TranscodeAudio_WmaTheClientDoesNotDecode_EncodesAacOverTheSessionHlsRoute() => EncodesAacOverTheSessionHlsRouteAsync(withCoverArt: false, "audio-wma");

    [Fact]
    public Task TranscodeAudio_FlacWithCoverArt_EncodesAudioOnlyOverTheSessionHlsRoute() => EncodesAacOverTheSessionHlsRouteAsync(withCoverArt: true, "audio-flac-cover");

    [Fact]
    public async Task RemuxAudio_AacInMatroska_CopiesTheAudioOverTheSessionHlsRoute()
    {
        const string playSession = "audio-remux-ps";
        var (client, userId) = await SignInAsync("audio-remux");
        var itemId = await SeedAacInMatroskaAsync();
        var session = await CreateSessionAsync(client, itemId, userId, playSession, EndToEndCapabilityPresets.AudioRemuxOverHls());

        Assert.Equal(PlaybackMethod.Remux, session.Method);
        Assert.Equal(MediaKind.Audio, session.Kind);
        Assert.Equal(PlaybackEngine.EngineVersion, session.DecisionVersion);
        Assert.Equal([TransformKind.RemuxContainer, TransformKind.CopyAudio], session.Transforms);

        var descriptor = await GetDescriptorAsync(client, session.Id);
        Assert.StartsWith(FormattableString.Invariant($"/Playback/Sessions/{session.Id:N}/Hls/main.m3u8?"), descriptor.Url, StringComparison.Ordinal);
        Assert.Equal(StreamingProtocol.Hls, descriptor.Protocol);
        Assert.Equal(PlaybackEngine.EngineVersion, descriptor.ServedBy);
        Assert.Null(descriptor.FallbackReason);

        var playlist = await ReadPlaylistAsync(client, descriptor.Url);
        Assert.True(playlist.Segments.Count >= 2, FormattableString.Invariant($"expected several segments, got {playlist.Segments.Count}"));
        var init = await GetBytesAsync(client, playlist.Init);
        var first = await GetBytesAsync(client, playlist.Segments[0]);
        Assert.NotEmpty(await GetBytesAsync(client, playlist.Segments[^1]));
        Assert.Equal(["audio,aac"], await ProbeProducedAsync(init, first));

        // What FFmpeg was told: copy the audio, map no video, encode nothing.
        var commands = await FfmpegCommandsAsync(itemId);
        Assert.NotEmpty(commands);
        Assert.All(commands, command =>
        {
            Assert.Contains("-vn", command, StringComparison.Ordinal);
            Assert.Contains("-acodec copy", command, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"-acodec (aac|libfdk_aac)\b", command);
        });

        var tracked = Assert.Single(_sessionManager.GetAll(), s => string.Equals(s.PlaySessionId, playSession, StringComparison.Ordinal));
        Assert.Null(tracked.Plan.StreamInfo);
        var job = _transcodeManager.GetTranscodingJob(playSession);
        Assert.NotNull(job);
        Assert.Equal(userId, job.UserId);

        await StopAsync(client, session.Id);
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, descriptor.Url));
        Assert.Null(_transcodeManager.GetTranscodingJob(playSession));
    }

    [Fact]
    public async Task TranscodeAudio_ToATargetOtherThanAac_IsRefusedExplicitly()
    {
        var (client, userId) = await SignInAsync("audio-refused");
        var itemId = await SeedTrackAsync(withCoverArt: false);

        // A client whose only output is MP3 over HLS and that decodes no AAC: the only transcode is
        // to MP3, which a session does not execute. Refused - on this host the legacy planner throws,
        // so a hand-over would be a 500, not this 422.
        var (direct, constraints) = EndToEndCapabilityPresets.AudioDirectPlay();
        var mp3Only = direct with { OutputProfiles = [new PlaybackOutputProfile(MediaKind.Audio, StreamingProtocol.Hls, "ts", [], ["mp3"], null, null, null)] };
        var request = new CreatePlaybackSessionRequest(itemId, userId, mp3Only, constraints, PlaySessionId: "audio-refused-ps");
        using var response = await client.PostAsJsonAsync("Playback/Sessions", request, JsonDefaults.Options, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, FormattableString.Invariant($"POST Playback/Sessions: {(int)response.StatusCode} {body}"));
        Assert.DoesNotContain(_sessionManager.GetAll(), s => string.Equals(s.PlaySessionId, "audio-refused-ps", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TranscodeAudio_AnotherUser_CanNeitherReadNorStopNorTakeTheJob()
    {
        var (owner, ownerId) = await SignInAsync("audio-tc-owner");
        var (intruder, _) = await SignInAsync("audio-tc-intruder");
        var itemId = await SeedTrackAsync(withCoverArt: false);
        var session = await CreateSessionAsync(owner, itemId, ownerId, "audio-tc-owner-ps");
        var descriptor = await GetDescriptorAsync(owner, session.Id);
        var playlist = await ReadPlaylistAsync(owner, descriptor.Url);

        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(intruder, descriptor.Url));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(intruder, playlist.Segments[0]));
        using (var stop = await intruder.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{session.Id:N}"), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, stop.StatusCode);
        }

        // The owner still plays, and the job is the owner's.
        Assert.NotEmpty(await GetBytesAsync(owner, playlist.Segments[0]));
        var job = _transcodeManager.GetTranscodingJob("audio-tc-owner-ps");
        Assert.NotNull(job);
        Assert.Equal(ownerId, job.UserId);
        await StopAsync(owner, session.Id);
    }

    private async Task EncodesAacOverTheSessionHlsRouteAsync(bool withCoverArt, string name)
    {
        var playSession = name + "-ps";
        var (client, userId) = await SignInAsync(name);
        var itemId = await SeedTrackAsync(withCoverArt);
        var session = await CreateSessionAsync(client, itemId, userId, playSession);

        // Decision: the engine's, for audio, with no video stream even when the file carries a picture.
        Assert.Equal(PlaybackMethod.Transcode, session.Method);
        Assert.Equal(MediaKind.Audio, session.Kind);
        Assert.Equal(PlaybackEngine.EngineVersion, session.DecisionVersion);
        Assert.Contains(TransformKind.TranscodeAudio, session.Transforms);
        Assert.DoesNotContain(TransformKind.CopyVideo, session.Transforms);
        Assert.DoesNotContain(TransformKind.TranscodeVideo, session.Transforms);
        Assert.Equal("aac", session.Output.AudioCodec);

        // Descriptor: the session's own route; no encoding parameter and no credential in it.
        var descriptor = await GetDescriptorAsync(client, session.Id);
        Assert.StartsWith(FormattableString.Invariant($"/Playback/Sessions/{session.Id:N}/Hls/main.m3u8?"), descriptor.Url, StringComparison.Ordinal);
        Assert.Equal(StreamingProtocol.Hls, descriptor.Protocol);
        Assert.Equal("mp4", descriptor.Container);
        Assert.Equal(PlaybackEngine.EngineVersion, descriptor.ServedBy);
        Assert.Null(descriptor.FallbackReason);
        foreach (var legacyParameter in new[] { "ApiKey", "AudioCodec", "AudioBitrate", "TranscodeReasons", "SegmentContainer", "AudioStreamIndex" })
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

        // What was produced: AAC, and nothing else - no picture became a video track.
        Assert.Equal(["audio,aac"], await ProbeProducedAsync(init, first));

        // What FFmpeg was told: encode the audio to AAC, map no video. Every command of this
        // playback (a seek may restart the job) says the same.
        var commands = await FfmpegCommandsAsync(itemId);
        Assert.NotEmpty(commands);
        Assert.All(commands, command =>
        {
            Assert.Contains("-vn", command, StringComparison.Ordinal);
            Assert.DoesNotContain("-acodec copy", command, StringComparison.Ordinal);
            Assert.Matches(@"-acodec (aac|libfdk_aac)\b", command);
        });

        // One session, the one the client created, holding the engine's plan and no StreamInfo;
        // one job, its owner's.
        var tracked = Assert.Single(_sessionManager.GetAll(), s => string.Equals(s.PlaySessionId, playSession, StringComparison.Ordinal));
        Assert.Equal(session.Id, tracked.Id.Value);
        Assert.Null(tracked.Plan.StreamInfo);
        var job = _transcodeManager.GetTranscodingJob(playSession);
        Assert.NotNull(job);
        Assert.Equal(userId, job.UserId);
        var jobPlaylist = job.Path!;

        // Stop: the session, its job and its output all go.
        await StopAsync(client, session.Id);
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, descriptor.Url));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, playlist.Segments[0]));
        Assert.Null(_transcodeManager.GetTranscodingJob(playSession));
        Assert.DoesNotContain(_sessionManager.GetAll(), s => string.Equals(s.PlaySessionId, playSession, StringComparison.Ordinal));
        await WaitUntilAsync(() => !File.Exists(jobPlaylist) && !Directory.EnumerateFiles(Path.GetDirectoryName(jobPlaylist)!, Path.GetFileNameWithoutExtension(jobPlaylist) + "*").Any(), "the job's output to be removed");
    }

    private async Task<Guid> SeedTrackAsync(bool withCoverArt)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_workDir, "track-" + Guid.NewGuid().ToString("N"))).FullName;
        var path = await EndToEndMediaFixtures.CreateUndecodedAudioAsync(directory, withCoverArt, FixtureSeconds);
        MediaStream[] streams = withCoverArt
            ?
            [
                new MediaStream { Index = 0, Type = MediaStreamType.Audio, Codec = "flac", Channels = 2, SampleRate = 44100, IsDefault = true },
                new MediaStream { Index = 1, Type = MediaStreamType.EmbeddedImage, Codec = "png", Width = 64, Height = 64 },
            ]
            : [new MediaStream { Index = 0, Type = MediaStreamType.Audio, Codec = "wmav2", Channels = 2, SampleRate = 44100, BitRate = 128_000, IsDefault = true }];

        return LibraryItemSeeder.SeedAudio(
            _factory.Services.GetRequiredService<IItemWriteCommands>(),
            _factory.Services.GetRequiredService<IMediaStreamRepository>(),
            path,
            withCoverArt ? "flac" : "asf",
            streams,
            $"Consolidation audio transcode {Guid.NewGuid()}",
            TimeSpan.FromSeconds(FixtureSeconds).Ticks);
    }

    private async Task<Guid> SeedAacInMatroskaAsync()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_workDir, "track-" + Guid.NewGuid().ToString("N"))).FullName;
        var path = await EndToEndMediaFixtures.CreateAacInMatroskaAsync(directory, FixtureSeconds);
        return LibraryItemSeeder.SeedAudio(
            _factory.Services.GetRequiredService<IItemWriteCommands>(),
            _factory.Services.GetRequiredService<IMediaStreamRepository>(),
            path,
            "mka",
            [new MediaStream { Index = 0, Type = MediaStreamType.Audio, Codec = "aac", Channels = 2, SampleRate = 44100, BitRate = 128_000, IsDefault = true }],
            $"Consolidation audio remux {Guid.NewGuid()}",
            TimeSpan.FromSeconds(FixtureSeconds).Ticks);
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

    private async Task<IReadOnlyList<string>> ProbeProducedAsync(byte[] init, byte[] segment)
    {
        Assert.NotEmpty(init);
        Assert.NotEmpty(segment);
        var produced = Path.Combine(_workDir, $"produced-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(produced, [.. init, .. segment], TestContext.Current.CancellationToken);
        return (await EndToEndMediaFixtures.ProbeStreamsAsync(produced))
            .Select(line => string.Join(',', line.Split(',').Take(2)))
            .ToList();
    }

    private static Task<PlaybackSessionResponse> CreateSessionAsync(HttpClient client, Guid itemId, Guid userId, string playSessionId)
        => CreateSessionAsync(client, itemId, userId, playSessionId, EndToEndCapabilityPresets.AudioTranscodeOverHls());

    private static async Task<PlaybackSessionResponse> CreateSessionAsync(
        HttpClient client,
        Guid itemId,
        Guid userId,
        string playSessionId,
        (ClientCapabilities Capabilities, PlaybackConstraints Constraints) preset)
    {
        var (capabilities, constraints) = preset;
        var request = new CreatePlaybackSessionRequest(itemId, userId, capabilities, constraints, PlaySessionId: playSessionId);
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
