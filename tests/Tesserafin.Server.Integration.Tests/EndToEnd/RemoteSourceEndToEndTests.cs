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
/// Consolidation step 1, non-local sources (docs/playback-path-inventory.md §7, step 5): a library
/// item whose source is a remote file over HTTP (a remote url, a <c>.strm</c> target) is executed by
/// the session from that url - remuxed or transcoded into its own HLS output, never handed to the
/// client as it is - and a source no session provider reads is refused. The legacy planner throws on
/// this host.
/// </summary>
/// <remarks>
/// Not <c>Category=Smoke</c>, and required by name in the hosted integration job, for the reasons
/// given on <see cref="DirectPlayFilmEndToEndTests"/>.
/// </remarks>
public sealed class RemoteSourceEndToEndTests : IClassFixture<DirectPlayFilmEndToEndTests.LegacyFreeFactory>, IAsyncLifetime
{
    private const string Password = "remote-password";

    private static readonly MediaStream[] _streams =
    [
        new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264", Width = EndToEndMediaFixtures.Width, Height = EndToEndMediaFixtures.Height, IsDefault = true },
        new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "aac", Channels = 2, IsDefault = true },
    ];

    private readonly DirectPlayFilmEndToEndTests.LegacyFreeFactory _factory;
    private HttpClient _admin = null!;
    private string _workDir = null!;
    private string _logDirectory = null!;
    private string _fixture = null!;

    public RemoteSourceEndToEndTests(DirectPlayFilmEndToEndTests.LegacyFreeFactory factory)
    {
        _factory = factory;
    }

    public async ValueTask InitializeAsync()
    {
        _admin = _factory.CreateClient();
        _admin.Timeout = TimeSpan.FromSeconds(120);
        var (accessToken, _) = await _factory.EnsureAuthenticatedAsync();
        _admin.DefaultRequestHeaders.AddAuthHeader(accessToken);
        _logDirectory = _factory.Services.GetRequiredService<IApplicationPaths>().LogDirectoryPath;
        _workDir = Directory.CreateTempSubdirectory("consol-remote-e2e-").FullName;
        _fixture = await EndToEndMediaFixtures.CreateH264AacMp4Async(_workDir, durationSeconds: 12);
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
    public async Task RemoteHttpFile_IsRemuxedFromItsUrlIntoTheSessionHlsOutput_NeverHandedToTheClient()
    {
        await using var remote = StaticHttpFile.Start(_fixture);
        var (client, userId) = await SignInAsync("remote-file");
        var itemId = Seed(remote.Url);
        var session = await CreateSessionAsync(client, itemId, userId, "remote-file-ps");

        // The client plays H.264/AAC in MP4 as it is; a remote url is still not given to it.
        Assert.NotEqual(PlaybackMethod.DirectPlay, session.Method);
        Assert.Equal(PlaybackEngine.EngineVersion, session.DecisionVersion);

        var descriptor = await GetDescriptorAsync(client, session.Id);
        Assert.StartsWith(FormattableString.Invariant($"/Playback/Sessions/{session.Id:N}/Hls/main.m3u8?"), descriptor.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", descriptor.Url, StringComparison.Ordinal);

        var (init, segments) = await ReadPlaylistAsync(client, descriptor.Url);
        Assert.True(segments.Count >= 2, FormattableString.Invariant($"expected several segments, got {segments.Count}"));
        var produced = Path.Combine(_workDir, $"remote-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(produced, [.. await GetBytesAsync(client, init), .. await GetBytesAsync(client, segments[0])], TestContext.Current.CancellationToken);
        Assert.NotEmpty(await GetBytesAsync(client, segments[^1]));
        Assert.Equal(["video,h264", "audio,aac"], (await EndToEndMediaFixtures.ProbeStreamsAsync(produced)).Select(line => string.Join(',', line.Split(',').Take(2))).ToList());

        // What FFmpeg read: the remote url itself.
        var commands = await FfmpegCommandsAsync(itemId);
        Assert.NotEmpty(commands);
        Assert.All(commands, command => Assert.Contains(remote.Url, command, StringComparison.Ordinal));

        using var stop = await client.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{session.Id:N}"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
    }

    [Fact]
    public async Task ASourceNoSessionProviderReads_IsRefusedExplicitly()
    {
        var (client, userId) = await SignInAsync("remote-refused");
        var itemId = Seed("rtsp://127.0.0.1:1/stream");

        // On this host the legacy planner throws: a hand-over would be a 500, not this 422.
        var (capabilities, constraints) = EndToEndCapabilityPresets.BrowserRemuxOverHls();
        var request = new CreatePlaybackSessionRequest(itemId, userId, capabilities, constraints, PlaySessionId: "remote-refused-ps");
        using var response = await client.PostAsJsonAsync("Playback/Sessions", request, JsonDefaults.Options, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, FormattableString.Invariant($"POST Playback/Sessions: {(int)response.StatusCode} {body}"));
    }

    private Guid Seed(string url)
        => LibraryItemSeeder.SeedVideo(
            _factory.Services.GetRequiredService<IItemWriteCommands>(),
            _factory.Services.GetRequiredService<IMediaStreamRepository>(),
            _fixture,
            "mp4",
            _streams,
            $"Consolidation remote {Guid.NewGuid()}",
            runTimeTicks: TimeSpan.FromSeconds(12).Ticks,
            configure: movie => movie.Path = url);

    private static async Task<PlaybackSessionResponse> CreateSessionAsync(HttpClient client, Guid itemId, Guid userId, string playSessionId)
    {
        var (capabilities, constraints) = EndToEndCapabilityPresets.BrowserRemuxOverHls();
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

    private static async Task<(string Init, List<string> Segments)> ReadPlaylistAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, FormattableString.Invariant($"GET '{url}': {(int)response.StatusCode} {body}"));
        var directory = url[..url.IndexOf('?', StringComparison.Ordinal)];
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
