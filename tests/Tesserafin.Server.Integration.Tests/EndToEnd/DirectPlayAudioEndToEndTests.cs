using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Library;
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
/// Consolidation step 1, audio (docs/playback-path-inventory.md §7): a library audio track read from
/// a local file, in a format the client plays, is decided by the v2 engine alone and served from the
/// session's own file route. The host replaces the legacy planner - for audio too - and the
/// live-stream resolver with implementations that throw (<see cref="DirectPlayFilmEndToEndTests.LegacyFreeFactory"/>).
/// </summary>
/// <remarks>
/// Not <c>Category=Smoke</c>, and required by name in the hosted integration job, for the reasons
/// given on <see cref="DirectPlayFilmEndToEndTests"/>.
/// </remarks>
public sealed class DirectPlayAudioEndToEndTests : IClassFixture<DirectPlayFilmEndToEndTests.LegacyFreeFactory>, IAsyncLifetime
{
    private const int FixtureSeconds = 10;
    private const string Password = "audio-password";

    private static readonly MediaStream[] _streams =
    [
        new MediaStream { Index = 0, Type = MediaStreamType.Audio, Codec = "mp3", Channels = 2, SampleRate = 44100, IsDefault = true },
    ];

    private readonly DirectPlayFilmEndToEndTests.LegacyFreeFactory _factory;
    private HttpClient _admin = null!;
    private string _workDir = null!;
    private string _fixturePath = null!;
    private byte[] _fixtureBytes = null!;

    public DirectPlayAudioEndToEndTests(DirectPlayFilmEndToEndTests.LegacyFreeFactory factory)
    {
        _factory = factory;
    }

    public async ValueTask InitializeAsync()
    {
        _admin = _factory.CreateClient();
        _admin.Timeout = TimeSpan.FromSeconds(90);
        var (accessToken, _) = await _factory.EnsureAuthenticatedAsync();
        _admin.DefaultRequestHeaders.AddAuthHeader(accessToken);

        _workDir = Directory.CreateTempSubdirectory("consol-audio-e2e-").FullName;
        _fixturePath = await EndToEndMediaFixtures.CreateMp3Async(_workDir, durationSeconds: FixtureSeconds);
        _fixtureBytes = await File.ReadAllBytesAsync(_fixturePath, TestContext.Current.CancellationToken);
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
    public async Task Audio_StartAndSeek_ServesRangesFromTheSessionFile()
    {
        var (client, userId) = await SignInAsync("audio-start");
        var itemId = SeedTrack();
        var session = await CreateSessionAsync(client, itemId, userId, "audio-start-ps");

        Assert.Equal(PlaybackMethod.DirectPlay, session.Method);
        Assert.Equal(MediaKind.Audio, session.Kind);
        Assert.Equal(PlaybackEngine.EngineVersion, session.DecisionVersion);

        var descriptor = await GetDescriptorAsync(client, session.Id);
        Assert.StartsWith(FormattableString.Invariant($"/Playback/Sessions/{session.Id:N}/File?"), descriptor.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiKey", descriptor.Url, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PlaybackEngine.EngineVersion, descriptor.ServedBy);
        Assert.Null(descriptor.FallbackReason);
        Assert.Equal("mp3", descriptor.Container);
        Assert.Equal("audio/mpeg", descriptor.MimeType);

        // Start, then a seek into the middle and to the end.
        await AssertRangeAsync(client, descriptor.Url, 0, 99);
        var middle = _fixtureBytes.Length / 2;
        await AssertRangeAsync(client, descriptor.Url, middle, middle + 4095);
        await AssertRangeAsync(client, descriptor.Url, _fixtureBytes.Length - 512, _fixtureBytes.Length - 1);
    }

    [Fact]
    public async Task AudioBook_StartAndSeek_ServesRangesFromTheSessionFile()
    {
        var (client, userId) = await SignInAsync("audiobook-start");
        var itemId = SeedTrack(audioBook: true);
        var session = await CreateSessionAsync(client, itemId, userId, "audiobook-start-ps");

        Assert.Equal(PlaybackMethod.DirectPlay, session.Method);
        Assert.Equal(MediaKind.Audio, session.Kind);
        Assert.Equal(PlaybackEngine.EngineVersion, session.DecisionVersion);

        var descriptor = await GetDescriptorAsync(client, session.Id);
        Assert.StartsWith(FormattableString.Invariant($"/Playback/Sessions/{session.Id:N}/File?"), descriptor.Url, StringComparison.Ordinal);
        await AssertRangeAsync(client, descriptor.Url, 0, 99);
        var middle = _fixtureBytes.Length / 2;
        await AssertRangeAsync(client, descriptor.Url, middle, middle + 4095);
    }

    [Fact]
    public async Task Audio_Stop_DeletesTheSessionAndRevokesItsCapability()
    {
        var (client, userId) = await SignInAsync("audio-stop");
        var itemId = SeedTrack();
        var session = await CreateSessionAsync(client, itemId, userId, "audio-stop-ps");
        var descriptor = await GetDescriptorAsync(client, session.Id);
        var capability = await MintMediaCapabilityAsync(client, itemId, "audio-stop-ps");

        using var anonymous = _factory.CreateClient();
        var withCapability = descriptor.Url + "&playbackCapability=" + Uri.EscapeDataString(capability);
        await AssertRangeAsync(anonymous, withCapability, 0, 9);

        using (var stop = await client.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{session.Id:N}"), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(anonymous, withCapability));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, descriptor.Url));
    }

    [Fact]
    public async Task Audio_AnotherUser_CanNeitherReadNorStopTheSession()
    {
        var (owner, ownerId) = await SignInAsync("audio-owner");
        var (intruder, _) = await SignInAsync("audio-intruder");
        var itemId = SeedTrack();
        var session = await CreateSessionAsync(owner, itemId, ownerId, "audio-owner-ps");
        var descriptor = await GetDescriptorAsync(owner, session.Id);

        using (var stream = await intruder.GetAsync(FormattableString.Invariant($"Playback/Sessions/{session.Id:N}/Stream"), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, stream.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(intruder, descriptor.Url));
        using (var stop = await intruder.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{session.Id:N}"), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, stop.StatusCode);
        }

        await AssertRangeAsync(owner, descriptor.Url, 0, 9);
    }

    private Guid SeedTrack(bool audioBook = false) =>
        LibraryItemSeeder.SeedAudio(
            _factory.Services.GetRequiredService<IItemWriteCommands>(),
            _factory.Services.GetRequiredService<IMediaStreamRepository>(),
            _fixturePath,
            "mp3",
            _streams,
            $"Consolidation audio {Guid.NewGuid()}",
            TimeSpan.FromSeconds(FixtureSeconds).Ticks,
            audioBook);

    private static async Task<PlaybackSessionResponse> CreateSessionAsync(HttpClient client, Guid itemId, Guid userId, string playSessionId)
    {
        var (capabilities, constraints) = EndToEndCapabilityPresets.AudioDirectPlay();
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

    private static async Task<string> MintMediaCapabilityAsync(HttpClient client, Guid itemId, string playSessionId)
    {
        using var response = await client.PostAsJsonAsync(
            "Playback/Capabilities",
            new { PlaySessionId = playSessionId, ItemId = itemId, MediaSourceId = itemId.ToString("N"), Scopes = new[] { "Media" } },
            JsonDefaults.Options,
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, FormattableString.Invariant($"POST Playback/Capabilities: {(int)response.StatusCode} {body}"));
        return JsonNode.Parse(body)!["Value"]!.GetValue<string>();
    }

    private async Task AssertRangeAsync(HttpClient client, string url, int from, int to)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(from, to);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.PartialContent, FormattableString.Invariant($"range {from}-{to} of '{url}': {(int)response.StatusCode}"));
        Assert.Equal(FormattableString.Invariant($"bytes {from}-{to}/{_fixtureBytes.Length}"), response.Content.Headers.ContentRange?.ToString());
        Assert.Equal(_fixtureBytes[from..(to + 1)], body);
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 9);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
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
