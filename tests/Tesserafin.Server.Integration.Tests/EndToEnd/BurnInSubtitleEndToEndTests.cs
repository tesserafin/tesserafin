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
/// Consolidation step 1, burnt-in subtitles (docs/playback-path-inventory.md §7): a library film
/// whose selected subtitle the client cannot render is decided by the v2 engine as a transcode
/// that burns that subtitle into the H.264 video, and executed on the session's own HLS route from
/// the retained plan. The host replaces the legacy planner with one that throws
/// (<see cref="DirectPlayFilmEndToEndTests.LegacyFreeFactory"/>). What was burnt in is read from
/// the FFmpeg command the server ran and from the segments it produced.
/// </summary>
/// <remarks>
/// Text subtitles only (an embedded ASS stream and an SRT sidecar): FFmpeg has no PGS encoder, so
/// a bitmap-subtitle fixture cannot be synthesized here. Not <c>Category=Smoke</c>, and required by
/// name in the hosted integration job, for the reasons given on <see cref="DirectPlayFilmEndToEndTests"/>.
/// </remarks>
public sealed class BurnInSubtitleEndToEndTests : IClassFixture<DirectPlayFilmEndToEndTests.LegacyFreeFactory>, IAsyncLifetime
{
    private const int FixtureSeconds = 12;
    private const int EmbeddedAss = 2;
    private const int Sidecar = 3;
    private const string Password = "burn-in-password";

    private readonly DirectPlayFilmEndToEndTests.LegacyFreeFactory _factory;
    private HttpClient _admin = null!;
    private string _workDir = null!;
    private string _logDirectory = null!;
    private ITranscodeManager _transcodeManager = null!;

    public BurnInSubtitleEndToEndTests(DirectPlayFilmEndToEndTests.LegacyFreeFactory factory)
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
        _transcodeManager = _factory.Services.GetRequiredService<ITranscodeManager>();
        _workDir = Directory.CreateTempSubdirectory("consol-burn-in-e2e-").FullName;
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
    public Task BurnIn_EmbeddedAss_IsBurntIntoTheEncodedVideoOnTheSessionHlsRoute() => BurnsInAsync(EmbeddedAss, "burn-ass");

    [Fact]
    public Task BurnIn_SrtSidecar_IsBurntIntoTheEncodedVideoOnTheSessionHlsRoute() => BurnsInAsync(Sidecar, "burn-srt");

    // Audit row S3 (decided 2026-11-27): a client that wants the subtitle embedded in the output,
    // which a session does not produce, and renders no external track for it: burnt in - never the
    // legacy planner.
    [Fact]
    public Task Subtitle_WantedEmbedded_IsBurntIntoTheEncodedVideo_NeverTheLegacyPlanner()
        => BurnsInAsync(Sidecar, "burn-embed", [new SubtitleCapability("subrip", SubtitleDeliveryMethod.Embed)]);

    // Audit row S3: a client that wants the subtitle as an HLS rendition and renders WebVTT beside the
    // stream: delivered as that external track, not burnt in - never the legacy planner.
    [Fact]
    public async Task Subtitle_WantedAsAnHlsRendition_IsDeliveredAsTheExternalTrackTheClientRenders()
    {
        var (client, userId) = await SignInAsync("subtitle-hls");
        var itemId = await SeedFilmAsync();
        using var response = await PostSessionAsync(client, itemId, userId, "subtitle-hls-ps", Sidecar, [new SubtitleCapability("subrip", SubtitleDeliveryMethod.Hls), new SubtitleCapability("vtt", SubtitleDeliveryMethod.External)], allowTranscoding: true);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, FormattableString.Invariant($"POST Playback/Sessions: {(int)response.StatusCode} {body}"));
        var session = JsonSerializer.Deserialize<PlaybackSessionResponse>(body, JsonDefaults.Options)!;

        Assert.Equal(PlaybackEngine.EngineVersion, session.DecisionVersion);
        Assert.DoesNotContain(TransformKind.BurnInSubtitle, session.Transforms);
        var descriptor = await GetDescriptorAsync(client, session.Id);
        Assert.Null(descriptor.FallbackReason);
        Assert.NotNull(descriptor.SubtitleUrl);

        using var stop = await client.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{session.Id:N}"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
    }

    // Audit row S3: the subtitle can only be embedded and the client forbids the transcode a burn-in
    // needs: refused with its reason - never the legacy planner, which throws on this host.
    [Fact]
    public async Task Subtitle_WantedEmbedded_WithTranscodingForbidden_IsRefusedWithItsReason()
    {
        var (client, userId) = await SignInAsync("subtitle-refused");
        var itemId = await SeedFilmAsync();
        using var response = await PostSessionAsync(client, itemId, userId, "subtitle-refused-ps", Sidecar, [new SubtitleCapability("subrip", SubtitleDeliveryMethod.Embed)], allowTranscoding: false);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, FormattableString.Invariant($"POST Playback/Sessions: {(int)response.StatusCode} {body}"));
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(nameof(LocalMediaRefusal.SubtitleDeliveryNotExecuted), problem.RootElement.GetProperty("reason").GetString());
    }

    private async Task BurnsInAsync(int subtitleIndex, string name, SubtitleCapability[]? subtitles = null)
    {
        var playSession = name + "-ps";
        var (client, userId) = await SignInAsync(name);
        var itemId = await SeedFilmAsync();
        var session = await CreateSessionAsync(client, itemId, userId, playSession, subtitleIndex, subtitles ?? []);

        // Decision: the engine's. The client renders no subtitle, so the selected one is burnt in,
        // which means encoding the video even though the client decodes the source's H.264.
        Assert.Equal(PlaybackMethod.Transcode, session.Method);
        Assert.Equal(PlaybackEngine.EngineVersion, session.DecisionVersion);
        Assert.Contains(TransformKind.BurnInSubtitle, session.Transforms);
        Assert.Contains(TransformKind.TranscodeVideo, session.Transforms);

        var descriptor = await GetDescriptorAsync(client, session.Id);
        Assert.StartsWith(FormattableString.Invariant($"/Playback/Sessions/{session.Id:N}/Hls/main.m3u8?"), descriptor.Url, StringComparison.Ordinal);
        Assert.Null(descriptor.FallbackReason);
        Assert.Null(descriptor.SubtitleUrl);

        var (init, segments) = await ReadPlaylistAsync(client, descriptor.Url);
        var produced = Path.Combine(_workDir, $"produced-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(produced, [.. await GetBytesAsync(client, init), .. await GetBytesAsync(client, segments[0])], TestContext.Current.CancellationToken);
        Assert.NotEmpty(await GetBytesAsync(client, segments[^1]));

        // What was produced: H.264 and AAC, and no subtitle stream - it is in the picture.
        var streams = (await EndToEndMediaFixtures.ProbeStreamsAsync(produced)).Select(line => string.Join(',', line.Split(',').Take(2))).ToList();
        Assert.Equal(["video,h264", "audio,aac"], streams);

        // What FFmpeg was told: encode the video with the subtitles filter over the planned stream.
        var commands = await FfmpegCommandsAsync(itemId);
        Assert.NotEmpty(commands);
        Assert.All(commands, command =>
        {
            Assert.Contains("subtitles=", command, StringComparison.Ordinal);
            Assert.DoesNotContain("-codec:v:0 copy", command, StringComparison.Ordinal);
            Assert.Matches(@"-codec:v:0 (libx264|h264_[a-z0-9]+)\b", command);
        });

        using (var stop = await client.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{session.Id:N}"), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        }

        Assert.Null(_transcodeManager.GetTranscodingJob(playSession));
    }

    private async Task<Guid> SeedFilmAsync()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_workDir, "film-" + Guid.NewGuid().ToString("N"))).FullName;
        var (film, sidecar) = await EndToEndMediaFixtures.CreateFilmWithTextSubtitlesAsync(directory, FixtureSeconds);
        MediaStream[] streams =
        [
            new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264", Width = 320, Height = 180, IsDefault = true },
            new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "aac", Channels = 2, IsDefault = true },
            new MediaStream { Index = EmbeddedAss, Type = MediaStreamType.Subtitle, Codec = "ass", Language = "eng" },
            new MediaStream { Index = Sidecar, Type = MediaStreamType.Subtitle, Codec = "subrip", Language = "eng", IsExternal = true, Path = sidecar },
        ];

        return LibraryItemSeeder.SeedVideo(
            _factory.Services.GetRequiredService<IItemWriteCommands>(),
            _factory.Services.GetRequiredService<IMediaStreamRepository>(),
            film,
            "mkv",
            streams,
            $"Consolidation burn-in {Guid.NewGuid()}",
            runTimeTicks: TimeSpan.FromSeconds(FixtureSeconds).Ticks);
    }

    private static Task<HttpResponseMessage> PostSessionAsync(HttpClient client, Guid itemId, Guid userId, string playSessionId, int subtitleIndex, SubtitleCapability[] subtitles, bool allowTranscoding)
    {
        var (capabilities, constraints) = EndToEndCapabilityPresets.BrowserRemuxOverHls();
        capabilities = capabilities with { Decode = capabilities.Decode with { SubtitleDelivery = subtitles } };
        constraints = constraints with { PreferredSubtitleStreamIndex = subtitleIndex, AllowTranscoding = allowTranscoding };
        var request = new CreatePlaybackSessionRequest(itemId, userId, capabilities, constraints, PlaySessionId: playSessionId);
        return client.PostAsJsonAsync("Playback/Sessions", request, JsonDefaults.Options, TestContext.Current.CancellationToken);
    }

    private static async Task<PlaybackSessionResponse> CreateSessionAsync(HttpClient client, Guid itemId, Guid userId, string playSessionId, int subtitleIndex, SubtitleCapability[] subtitles)
    {
        // A browser-like HLS client that renders no subtitle at all, or only the ones it declares.
        using var response = await PostSessionAsync(client, itemId, userId, playSessionId, subtitleIndex, subtitles, allowTranscoding: true);
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
