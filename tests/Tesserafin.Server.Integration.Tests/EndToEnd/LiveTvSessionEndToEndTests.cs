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
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Extensions.Json;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dto;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Engine;
using Tesserafin.Server.Integration.Tests.PlaybackCredentials;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// Consolidation step 1, Live TV (docs/playback-path-inventory.md §7): a channel of an M3U tuner
/// played through <c>Playback/Sessions</c> alone. The session opens the tuner stream, the v2
/// engine decides on that opened source, the session's live HLS route executes the plan reading
/// the stream through its provider, and the session's end closes the stream. The host replaces the
/// legacy planner with one that throws (<see cref="DirectPlayFilmEndToEndTests.LegacyFreeFactory"/>);
/// the tuner is a real HTTP server pacing a real MPEG-TS body (<see cref="SoftwareTuner"/>).
/// </summary>
/// <remarks>
/// Not <c>Category=Smoke</c>, and required by name in the hosted integration job, for the reasons
/// given on <see cref="DirectPlayFilmEndToEndTests"/>.
/// </remarks>
public sealed class LiveTvSessionEndToEndTests : IClassFixture<DirectPlayFilmEndToEndTests.LegacyFreeFactory>, IAsyncLifetime
{
    private const int FixtureSeconds = 90;
    private const string Password = "livetv-password";

    private readonly DirectPlayFilmEndToEndTests.LegacyFreeFactory _factory;
    private HttpClient _admin = null!;
    private string _workDir = null!;
    private SoftwareTuner _tuner = null!;

    public LiveTvSessionEndToEndTests(DirectPlayFilmEndToEndTests.LegacyFreeFactory factory)
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
        _workDir = Directory.CreateTempSubdirectory("consol-livetv-e2e-").FullName;
        var fixture = await EndToEndMediaFixtures.CreateTunerTransportStreamAsync(_workDir, FixtureSeconds);
        _tuner = SoftwareTuner.Start(fixture, FixtureSeconds);
    }

    public async ValueTask DisposeAsync()
    {
        await _tuner.DisposeAsync();
        _admin.Dispose();
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task LiveTvChannel_PlaysFromTheSessionLiveRoute_OnOneTunerStream_ClosedByTheStop()
    {
        const string PlaySession = "livetv-ps";
        var channelId = await RegisterTunerAndFindChannelAsync();
        var (owner, ownerId) = await SignInAsync("livetv-owner");
        var (intruder, _) = await SignInAsync("livetv-intruder");

        // Decision: the engine's, on the opened tuner stream; an infinite source is never played as it is.
        var session = await CreateSessionAsync(owner, channelId, ownerId, PlaySession);
        Assert.Equal(PlaybackEngine.EngineVersion, session.DecisionVersion);
        Assert.Contains(session.Method, new[] { PlaybackMethod.Remux, PlaybackMethod.Transcode });
        Assert.Equal(1, _tuner.StreamsOpened);

        var descriptor = await GetDescriptorAsync(owner, session.Id);
        Assert.StartsWith(FormattableString.Invariant($"/Playback/Sessions/{session.Id:N}/Hls/live.m3u8?"), descriptor.Url, StringComparison.Ordinal);
        Assert.Equal(StreamingProtocol.Hls, descriptor.Protocol);
        Assert.Null(descriptor.FallbackReason);
        Assert.DoesNotContain("LiveStreamFiles", descriptor.Url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ApiKey", descriptor.Url, StringComparison.OrdinalIgnoreCase);

        // The live playlist ffmpeg writes, every segment uri on this session's own route.
        var segments = await ReadLivePlaylistAsync(owner, descriptor.Url);
        Assert.All(segments, uri => Assert.Contains(FormattableString.Invariant($"/Playback/Sessions/{session.Id:N}/Hls/hls/"), uri, StringComparison.Ordinal));
        var media = await GetBytesAsync(owner, segments.Last());
        var init = segments.Count > 1 && segments[0].Contains("-1.", StringComparison.Ordinal) ? await GetBytesAsync(owner, segments[0]) : [];
        var produced = Path.Combine(_workDir, $"produced-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(produced, [.. init, .. media], TestContext.Current.CancellationToken);
        var streams = (await EndToEndMediaFixtures.ProbeStreamsAsync(produced)).Select(line => string.Join(',', line.Split(',').Take(2))).ToList();
        Assert.Contains("video,h264", streams);
        Assert.Contains("audio,aac", streams);

        // Another user reaches neither the playlist nor a segment.
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(intruder, descriptor.Url));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(intruder, segments.Last()));

        // A re-plan keeps the tuner stream the session holds: no second open.
        var replanned = await ReplaceSessionAsync(owner, session.Id, channelId, ownerId);
        Assert.Contains(replanned.Method, new[] { PlaybackMethod.Remux, PlaybackMethod.Transcode });
        Assert.Equal(1, _tuner.StreamsOpened);

        // The stop closes the tuner stream and the session's routes.
        using (var stop = await owner.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{session.Id:N}"), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        }

        await WaitUntilAsync(() => _tuner.OpenStreams == 0, "the tuner stream to be closed");
        Assert.Null(_factory.Services.GetRequiredService<PlaybackSessionLiveStreams>().Get(new PlaybackSessionId(session.Id)));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(owner, descriptor.Url));
    }

    private async Task<Guid> RegisterTunerAndFindChannelAsync()
    {
        using (var added = await _admin.PostAsJsonAsync(
            "LiveTv/TunerHosts",
            new
            {
                Url = _tuner.PlaylistUrl,
                Type = "m3u",
                FriendlyName = "E2E Software Tuner",
                ImportFavoritesOnly = false,
                AllowHWTranscoding = false,
                AllowStreamSharing = true,
                EnableStreamLooping = false,
                TunerCount = 3,
                IgnoreDts = true,
                ReadAtNativeFramerate = false,
            },
            JsonDefaults.Options,
            TestContext.Current.CancellationToken))
        {
            var body = await added.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(added.IsSuccessStatusCode, FormattableString.Invariant($"POST LiveTv/TunerHosts: {(int)added.StatusCode} {body}"));
        }

        for (var attempt = 0; attempt < 120; attempt++)
        {
            using var response = await _admin.GetAsync("LiveTv/Channels", TestContext.Current.CancellationToken);
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                var channel = document.RootElement.GetProperty("Items").EnumerateArray()
                    .FirstOrDefault(item => item.GetProperty("Name").GetString() == "E2E One");
                if (channel.ValueKind == JsonValueKind.Object)
                {
                    return Guid.Parse(channel.GetProperty("Id").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        Assert.Fail("The tuner's channel was never indexed.");
        return Guid.Empty;
    }

    private static async Task<PlaybackSessionResponse> CreateSessionAsync(HttpClient client, Guid channelId, Guid userId, string playSessionId)
    {
        var (capabilities, constraints) = EndToEndCapabilityPresets.BrowserRemuxOverHls();
        var request = new CreatePlaybackSessionRequest(channelId, userId, capabilities, constraints, PlaySessionId: playSessionId);
        using var response = await client.PostAsJsonAsync("Playback/Sessions", request, JsonDefaults.Options, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, FormattableString.Invariant($"POST Playback/Sessions: {(int)response.StatusCode} {body}"));
        return JsonSerializer.Deserialize<PlaybackSessionResponse>(body, JsonDefaults.Options)!;
    }

    private static async Task<PlaybackSessionResponse> ReplaceSessionAsync(HttpClient client, Guid sessionId, Guid channelId, Guid userId)
    {
        var (capabilities, constraints) = EndToEndCapabilityPresets.BrowserRemuxOverHls();
        var request = new ReplacePlaybackSessionRequest(channelId, userId, capabilities, constraints);
        using var response = await client.PutAsJsonAsync(FormattableString.Invariant($"Playback/Sessions/{sessionId:N}"), request, JsonDefaults.Options, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, FormattableString.Invariant($"PUT Playback/Sessions: {(int)response.StatusCode} {body}"));
        return JsonSerializer.Deserialize<PlaybackSessionResponse>(body, JsonDefaults.Options)!;
    }

    private static async Task<PlaybackSessionStreamDescriptor> GetDescriptorAsync(HttpClient client, Guid sessionId)
    {
        using var response = await client.GetAsync(FormattableString.Invariant($"Playback/Sessions/{sessionId:N}/Stream?startTimeTicks=0"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, FormattableString.Invariant($"GET .../Stream: {(int)response.StatusCode} {body}"));
        return JsonSerializer.Deserialize<PlaybackSessionStreamDescriptor>(body, JsonDefaults.Options)!;
    }

    /// <summary>
    /// Reads the live playlist and resolves its uris against its own url, as a player does: the
    /// initialisation segment first when there is one, then the media segments.
    /// </summary>
    private static async Task<List<string>> ReadLivePlaylistAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, FormattableString.Invariant($"GET '{url}': {(int)response.StatusCode} {body}"));
        Assert.StartsWith("#EXTM3U", body, StringComparison.Ordinal);

        var directory = url[..url.IndexOf('?', StringComparison.Ordinal)];
        directory = directory[..(directory.LastIndexOf('/') + 1)];
        var uris = new List<string>();
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("#EXT-X-MAP:URI=\"", StringComparison.Ordinal))
            {
                uris.Add(directory + line["#EXT-X-MAP:URI=\"".Length..line.LastIndexOf('"')]);
            }
            else if (!line.StartsWith('#'))
            {
                uris.Add(directory + line);
            }
        }

        Assert.True(uris.Count > 0, "the live playlist names no segment: " + body);
        return uris;
    }

    private static async Task<byte[]> GetBytesAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, FormattableString.Invariant($"GET '{url}': {(int)response.StatusCode}"));
        Assert.NotEmpty(body);
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
