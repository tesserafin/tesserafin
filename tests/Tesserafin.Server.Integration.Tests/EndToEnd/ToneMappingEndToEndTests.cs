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
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Engine;
using Tesserafin.Server.Integration.Tests.PlaybackCredentials;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// Consolidation step 1, HDR tone mapping (docs/playback-path-inventory.md §7): an HDR10 film for a
/// client that shows SDR only is decided by the v2 engine as a transcode that tone-maps to SDR, and
/// executed on the session's own HLS route from the retained plan. The legacy planner throws on
/// this host. What was produced is read from the FFmpeg command and probed off the segments.
/// </summary>
/// <remarks>
/// The executor tone-maps with the <c>tonemapx</c> filter, which the project's FFmpeg build (F0)
/// carries and a distribution FFmpeg does not. The legacy-free host runs the FFmpeg named by
/// <c>TESSERAFIN_PROJECT_FFMPEG</c> (the default FFmpeg otherwise); the fixture is encoded with the
/// default one, which has 10-bit HEVC. Without <c>tonemapx</c> the planner does not own the plan, so
/// the test is skipped rather than asserting a refusal of its own design.
/// </remarks>
public sealed class ToneMappingEndToEndTests : IClassFixture<DirectPlayFilmEndToEndTests.LegacyFreeFactory>, IAsyncLifetime
{
    private const int FixtureSeconds = 12;
    private const string Password = "tonemap-password";

    private readonly DirectPlayFilmEndToEndTests.LegacyFreeFactory _factory;
    private HttpClient _admin = null!;
    private string _workDir = null!;
    private string _logDirectory = null!;

    public ToneMappingEndToEndTests(DirectPlayFilmEndToEndTests.LegacyFreeFactory factory)
    {
        _factory = factory;
    }

    public async ValueTask InitializeAsync()
    {
        _admin = _factory.CreateClient();
        _admin.Timeout = TimeSpan.FromSeconds(120);
        var (accessToken, _) = await _factory.EnsureAuthenticatedAsync();
        _admin.DefaultRequestHeaders.AddAuthHeader(accessToken);
        _factory.Services.GetRequiredService<IServerConfigurationManager>().Configuration.PlaybackShadow.Mode = PlaybackEngineMode.Legacy;
        _logDirectory = _factory.Services.GetRequiredService<IApplicationPaths>().LogDirectoryPath;
        _workDir = Directory.CreateTempSubdirectory("consol-tonemap-e2e-").FullName;
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
    public async Task ToneMapping_Hdr10ForAnSdrClient_IsToneMappedToSdrOnTheSessionHlsRoute()
    {
        Assert.SkipUnless(
            _factory.Services.GetRequiredService<IMediaEncoder>().SupportsFilter("tonemapx"),
            "The server's FFmpeg has no tonemapx filter; set TESSERAFIN_PROJECT_FFMPEG to the project's FFmpeg build.");

        const string PlaySession = "tonemap-ps";
        var (client, userId) = await SignInAsync("tonemap");
        var itemId = await SeedHdrFilmAsync();
        var session = await CreateSessionAsync(client, itemId, userId, PlaySession);

        Assert.Equal(PlaybackMethod.Transcode, session.Method);
        Assert.Equal(PlaybackEngine.EngineVersion, session.DecisionVersion);
        Assert.Contains(TransformKind.Tonemap, session.Transforms);
        Assert.Contains(TransformKind.TranscodeVideo, session.Transforms);
        Assert.Equal("h264", session.Output.VideoCodec);

        var descriptor = await GetDescriptorAsync(client, session.Id);
        Assert.StartsWith(FormattableString.Invariant($"/Playback/Sessions/{session.Id:N}/Hls/main.m3u8?"), descriptor.Url, StringComparison.Ordinal);
        Assert.Null(descriptor.FallbackReason);

        var (init, segments) = await ReadPlaylistAsync(client, descriptor.Url);
        var produced = Path.Combine(_workDir, $"produced-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(produced, [.. await GetBytesAsync(client, init), .. await GetBytesAsync(client, segments[0])], TestContext.Current.CancellationToken);
        Assert.NotEmpty(await GetBytesAsync(client, segments[^1]));

        // What was produced: 8-bit H.264 that no longer carries the PQ transfer of the source.
        var streams = (await EndToEndMediaFixtures.ProbeStreamsAsync(produced)).Select(line => string.Join(',', line.Split(',').Take(2))).ToList();
        Assert.Equal(["video,h264", "audio,aac"], streams);
        var (pixelFormat, transfer) = await EndToEndMediaFixtures.ProbeVideoColourAsync(produced);
        Assert.Equal("yuv420p", pixelFormat);
        Assert.NotEqual("smpte2084", transfer);

        // What FFmpeg was told: tone-map with tonemapx on an H.264 encode.
        var commands = await FfmpegCommandsAsync(itemId);
        Assert.NotEmpty(commands);
        Assert.All(commands, command =>
        {
            Assert.Contains("tonemapx=", command, StringComparison.Ordinal);
            Assert.Matches(@"-codec:v:0 (libx264|h264_[a-z0-9]+)\b", command);
        });

        using var stop = await client.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{session.Id:N}"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
    }

    // Audit row S2 (decided 2026-11-27): a client that declares HDR10 for H.264 would get an HDR10
    // H.264 encode, which a session does not produce. It also accepts SDR for H.264, so the session
    // tone-maps to SDR instead, and is executed on its own HLS route - never the legacy planner.
    [Fact]
    public async Task HdrOutput_ForAClientThatAlsoAcceptsSdr_IsToneMappedToSdrOnTheSessionHlsRoute()
    {
        Assert.SkipUnless(
            _factory.Services.GetRequiredService<IMediaEncoder>().SupportsFilter("tonemapx"),
            "The server's FFmpeg has no tonemapx filter; set TESSERAFIN_PROJECT_FFMPEG to the project's FFmpeg build.");

        var (client, userId) = await SignInAsync("hdr-output-sdr");
        var itemId = await SeedHdrFilmAsync();
        var session = await CreateSessionAsync(client, itemId, userId, "hdr-output-sdr-ps", ["SDR", "HDR10"]);

        Assert.Equal(PlaybackMethod.Transcode, session.Method);
        Assert.Contains(TransformKind.Tonemap, session.Transforms);
        Assert.Equal("h264", session.Output.VideoCodec);

        var descriptor = await GetDescriptorAsync(client, session.Id);
        Assert.StartsWith(FormattableString.Invariant($"/Playback/Sessions/{session.Id:N}/Hls/main.m3u8?"), descriptor.Url, StringComparison.Ordinal);
        var (init, segments) = await ReadPlaylistAsync(client, descriptor.Url);
        var produced = Path.Combine(_workDir, $"produced-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(produced, [.. await GetBytesAsync(client, init), .. await GetBytesAsync(client, segments[0])], TestContext.Current.CancellationToken);
        var (pixelFormat, transfer) = await EndToEndMediaFixtures.ProbeVideoColourAsync(produced);
        Assert.Equal("yuv420p", pixelFormat);
        Assert.NotEqual("smpte2084", transfer);
        Assert.All(await FfmpegCommandsAsync(itemId), command => Assert.Contains("tonemapx=", command, StringComparison.Ordinal));

        using var stop = await client.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{session.Id:N}"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
    }

    private async Task<Guid> SeedHdrFilmAsync()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_workDir, "film-" + Guid.NewGuid().ToString("N"))).FullName;
        var film = await EndToEndMediaFixtures.CreateHdr10FilmAsync(directory, FixtureSeconds);
        MediaStream[] streams =
        [
            new MediaStream
            {
                Index = 0,
                Type = MediaStreamType.Video,
                Codec = "hevc",
                Profile = "Main 10",
                Width = 320,
                Height = 180,
                BitDepth = 10,
                PixelFormat = "yuv420p10le",
                ColorPrimaries = "bt2020",
                ColorTransfer = "smpte2084",
                ColorSpace = "bt2020nc",
                IsDefault = true,
            },
            new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "aac", Channels = 2, IsDefault = true },
        ];

        return LibraryItemSeeder.SeedVideo(
            _factory.Services.GetRequiredService<IItemWriteCommands>(),
            _factory.Services.GetRequiredService<IMediaStreamRepository>(),
            film,
            "mkv",
            streams,
            $"Consolidation HDR10 {Guid.NewGuid()}",
            runTimeTicks: TimeSpan.FromSeconds(FixtureSeconds).Ticks);
    }

    private static async Task<PlaybackSessionResponse> CreateSessionAsync(HttpClient client, Guid itemId, Guid userId, string playSessionId, string[]? h264Ranges = null)
    {
        // A browser-like HLS client that decodes H.264 and HEVC but shows SDR only.
        var (capabilities, constraints) = EndToEndCapabilityPresets.BrowserRemuxOverHls();
        capabilities = capabilities with
        {
            Decode = capabilities.Decode with
            {
                VideoCodecs =
                [
                    new VideoCodecCapability("h264", [], null, null, h264Ranges ?? ["SDR"], null, null),
                    new VideoCodecCapability("hevc", [], null, null, ["SDR"], null, null),
                ],
            },
        };
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
