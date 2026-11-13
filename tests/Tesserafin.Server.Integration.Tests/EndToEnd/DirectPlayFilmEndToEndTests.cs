using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Persistence;
using Tesserafin.Extensions.Json;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Engine;
using Tesserafin.Server.Integration.Tests.PlaybackCredentials;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// Consolidation step 1, first journey (docs/playback-path-inventory.md §4): a film read from a
/// local file is decided by the v2 engine alone and served from the session's own file route.
/// The host these tests run on replaces the legacy planner (<c>StreamBuilder</c>) and the
/// live-stream resolver (the only caller of <c>PlaybackExecutionPlanAdapter.ToStreamInfo</c>) with
/// implementations that throw: any request of this journey that still reached the legacy chain
/// fails with a 500 instead of producing a visually identical result.
/// </summary>
/// <remarks>
/// Deliberately not <c>Category=Smoke</c>: the hosted integration job installs ffmpeg (needed only
/// to generate the fixture and to start the host) and runs these on every non-draft change, and
/// requires each of them by name, so a rename, skip or filter turns the check red.
/// </remarks>
public sealed class DirectPlayFilmEndToEndTests : IClassFixture<DirectPlayFilmEndToEndTests.LegacyFreeFactory>, IAsyncLifetime
{
    private const int FixtureSeconds = 10;
    private const string Password = "film-password";

    private static readonly MediaStream[] _streams =
    [
        new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = EndToEndCapabilityPresets.FixtureVideoCodec, Width = EndToEndMediaFixtures.Width, Height = EndToEndMediaFixtures.Height, IsDefault = true },
        new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = EndToEndCapabilityPresets.FixtureAudioCodec, Channels = 2, IsDefault = true },
    ];

    private readonly LegacyFreeFactory _factory;
    private HttpClient _admin = null!;
    private string _workDir = null!;
    private string _fixturePath = null!;
    private byte[] _fixtureBytes = null!;
    private ILibraryManager _libraryManager = null!;
    private IMediaStreamRepository _mediaStreamRepository = null!;
    private IServerConfigurationManager _configManager = null!;

    public DirectPlayFilmEndToEndTests(LegacyFreeFactory factory)
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

        // The film journey must not depend on the global engine mode: it is decided by the engine
        // even with the mode left at its Legacy default.
        _configManager.Configuration.PlaybackShadow.Mode = PlaybackEngineMode.Legacy;
        _workDir = Directory.CreateTempSubdirectory("consol-film-e2e-").FullName;
        _fixturePath = await EndToEndMediaFixtures.CreateH264AacMp4Async(_workDir, durationSeconds: FixtureSeconds);
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
    public async Task Film_StartAndSeek_ServesRangesFromTheSessionFile()
    {
        var (client, userId) = await SignInAsync("film-start");
        var itemId = SeedFilm();
        var session = await CreateSessionAsync(client, itemId, userId, "film-start-ps");

        Assert.Equal(PlaybackMethod.DirectPlay, session.Method);
        Assert.Equal(PlaybackEngine.EngineVersion, session.DecisionVersion);

        var descriptor = await GetDescriptorAsync(client, session.Id);
        Assert.StartsWith(FormattableString.Invariant($"/Playback/Sessions/{session.Id:N}/File?"), descriptor.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiKey", descriptor.Url, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PlaybackEngine.EngineVersion, descriptor.ServedBy);
        Assert.Null(descriptor.FallbackReason);
        Assert.Equal("mp4", descriptor.Container);
        Assert.Equal("video/mp4", descriptor.MimeType);

        // Start: the first range.
        await AssertRangeAsync(client, descriptor.Url, 0, 99);

        // Seek: a range in the middle and an open-ended tail.
        var middle = _fixtureBytes.Length / 2;
        await AssertRangeAsync(client, descriptor.Url, middle, middle + 4095);
        using (var tail = new HttpRequestMessage(HttpMethod.Get, descriptor.Url))
        {
            tail.Headers.Range = new RangeHeaderValue(_fixtureBytes.Length - 512, null);
            using var response = await client.SendAsync(tail, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            var body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
            Assert.Equal(_fixtureBytes[^512..], body);
        }

        // Length and type, without a body.
        using (var head = new HttpRequestMessage(HttpMethod.Head, descriptor.Url))
        {
            using var response = await client.SendAsync(head, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(_fixtureBytes.Length, response.Content.Headers.ContentLength);
            Assert.Equal("video/mp4", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("bytes", response.Headers.AcceptRanges);
        }
    }

    [Fact]
    public async Task Film_ParametersThatDoNotMatchTheSession_AreNotServed()
    {
        var (client, userId) = await SignInAsync("film-params");
        var itemId = SeedFilm();
        var otherItemId = SeedFilm();
        var session = await CreateSessionAsync(client, itemId, userId, "film-params-ps");
        var descriptor = await GetDescriptorAsync(client, session.Id);

        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, descriptor.Url.Replace(itemId.ToString("N"), otherItemId.ToString("N"), StringComparison.Ordinal)));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, descriptor.Url.Replace("film-params-ps", "another-ps", StringComparison.Ordinal)));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, FormattableString.Invariant($"/Playback/Sessions/{Guid.NewGuid():N}/File?itemId={itemId:N}&mediaSourceId={itemId:N}&playSessionId=film-params-ps")));
    }

    [Fact]
    public async Task Film_Stop_DeletesTheSessionAndRevokesItsCapability()
    {
        var (client, userId) = await SignInAsync("film-stop");
        var itemId = SeedFilm();
        var session = await CreateSessionAsync(client, itemId, userId, "film-stop-ps");
        var descriptor = await GetDescriptorAsync(client, session.Id);
        var capability = await MintMediaCapabilityAsync(client, itemId, "film-stop-ps");

        using var anonymous = _factory.CreateClient();
        var withCapability = descriptor.Url + "&playbackCapability=" + Uri.EscapeDataString(capability);
        await AssertRangeAsync(anonymous, withCapability, 0, 9);

        using (var stop = await client.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{session.Id:N}"), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(anonymous, withCapability));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, descriptor.Url));
        using var stream = await client.GetAsync(FormattableString.Invariant($"Playback/Sessions/{session.Id:N}/Stream"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, stream.StatusCode);
    }

    [Fact]
    public async Task Film_AnotherUser_CanNeitherReadNorStopTheSession()
    {
        var (owner, ownerId) = await SignInAsync("film-owner");
        var (intruder, _) = await SignInAsync("film-intruder");
        var itemId = SeedFilm();
        var session = await CreateSessionAsync(owner, itemId, ownerId, "film-owner-ps");
        var descriptor = await GetDescriptorAsync(owner, session.Id);

        using (var stream = await intruder.GetAsync(FormattableString.Invariant($"Playback/Sessions/{session.Id:N}/Stream"), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, stream.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(intruder, descriptor.Url));

        // The intruder's own capability, minted for the same item and play session id, is bound to
        // the intruder: the owner check still refuses it.
        var intruderCapability = await MintMediaCapabilityAsync(intruder, itemId, "film-owner-ps");
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(anonymous, descriptor.Url + "&playbackCapability=" + Uri.EscapeDataString(intruderCapability)));

        using (var stop = await intruder.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{session.Id:N}"), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, stop.StatusCode);
        }

        await AssertRangeAsync(owner, descriptor.Url, 0, 9);
    }

    [Fact]
    public async Task Film_AccessWithdrawnAfterPlanning_StopsServing()
    {
        const string Tag = "film-withdrawn";
        var (client, userId) = await SignInAsync("film-withdrawn");
        var itemId = SeedFilm(tags: [Tag]);
        var session = await CreateSessionAsync(client, itemId, userId, "film-withdrawn-ps");
        var descriptor = await GetDescriptorAsync(client, session.Id);
        await AssertRangeAsync(client, descriptor.Url, 0, 9);

        await UpdatePolicyAsync(userId, policy => policy["BlockedTags"] = new JsonArray(Tag));

        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, descriptor.Url));
    }

    [Fact]
    public async Task Film_StoppedPosition_IsStoredAndAResumedSessionServesFromIt()
    {
        var configuration = _configManager.Configuration;
        var minResumeSeconds = configuration.MinResumeDurationSeconds;
        configuration.MinResumeDurationSeconds = 0;
        try
        {
            var (client, userId) = await SignInAsync("film-resume");
            var itemId = SeedFilm();
            var first = await CreateSessionAsync(client, itemId, userId, "film-resume-1");
            var position = TimeSpan.FromSeconds(FixtureSeconds / 2).Ticks;

            await PostAsync(client, "Sessions/Playing", new { ItemId = itemId, MediaSourceId = itemId.ToString("N"), PlaySessionId = "film-resume-1", PositionTicks = 0L, PlayMethod = "DirectPlay" });
            await PostAsync(client, "Sessions/Playing/Stopped", new { ItemId = itemId, MediaSourceId = itemId.ToString("N"), PlaySessionId = "film-resume-1", PositionTicks = position });
            using (var stop = await client.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{first.Id:N}"), TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
            }

            var userData = await client.GetFromJsonAsync<UserItemDataDto>(FormattableString.Invariant($"UserItems/{itemId:N}/UserData"), JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal(position, userData!.PlaybackPositionTicks);

            var resumed = await CreateSessionAsync(client, itemId, userId, "film-resume-2");
            Assert.Equal(PlaybackMethod.DirectPlay, resumed.Method);
            var descriptor = await GetDescriptorAsync(client, resumed.Id, position);
            await AssertRangeAsync(client, descriptor.Url, _fixtureBytes.Length / 2, (_fixtureBytes.Length / 2) + 1023);
        }
        finally
        {
            configuration.MinResumeDurationSeconds = minResumeSeconds;
        }
    }

    [Theory]
    [InlineData("None", false)]
    [InlineData("Default", true)]
    public async Task Film_UserSubtitlePreference_DecidesWhenTheClientNamesNoTrack(string userSubtitleMode, bool expectSubtitle)
    {
        var (client, userId) = await SignInAsync("film-subs-" + userSubtitleMode.ToLowerInvariant());
        await UpdateConfigurationAsync(userId, configuration => configuration["SubtitleMode"] = userSubtitleMode);
        var subtitlePath = EndToEndMediaFixtures.CreateExternalSrt(_workDir, $"film-{userSubtitleMode}.srt");
        var itemId = LibraryItemSeeder.SeedVideo(
            _libraryManager,
            _mediaStreamRepository,
            _fixturePath,
            "mp4",
            [.. _streams, new MediaStream { Index = 2, Type = MediaStreamType.Subtitle, Codec = "srt", IsExternal = true, Path = subtitlePath, Language = "eng" }],
            $"Consolidation film subtitles {Guid.NewGuid()}",
            runTimeTicks: TimeSpan.FromSeconds(FixtureSeconds).Ticks);

        // What the Web client declares: an External srt capability, subtitle mode Default, no index.
        var (capabilities, constraints) = EndToEndCapabilityPresets.DirectPlayWithExternalSubtitle(subtitleStreamIndex: 2);
        constraints = constraints with { PreferredSubtitleStreamIndex = null, SubtitleMode = SubtitlePlaybackMode.Default };
        var request = new CreatePlaybackSessionRequest(itemId, userId, capabilities, constraints, PlaySessionId: "film-subs-" + userSubtitleMode);
        using var response = await client.PostAsJsonAsync("Playback/Sessions", request, JsonDefaults.Options, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, body);
        var session = JsonSerializer.Deserialize<PlaybackSessionResponse>(body, JsonDefaults.Options)!;

        Assert.Equal(PlaybackMethod.DirectPlay, session.Method);
        if (expectSubtitle)
        {
            Assert.Equal(2, session.SelectedStreams.Subtitle?.Index);
        }
        else
        {
            Assert.Null(session.SelectedStreams.Subtitle);
        }
    }

    private async Task UpdateConfigurationAsync(Guid userId, Action<JsonObject> change)
    {
        var configuration = JsonNode.Parse(await _admin.GetStringAsync(FormattableString.Invariant($"Users/{userId}"), TestContext.Current.CancellationToken))!["Configuration"]!.AsObject();
        change(configuration);
        using var content = new StringContent(configuration.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var saved = await _admin.PostAsync(FormattableString.Invariant($"Users/Configuration?userId={userId}"), content, TestContext.Current.CancellationToken);
        Assert.True(saved.IsSuccessStatusCode, FormattableString.Invariant($"POST Users/Configuration: {(int)saved.StatusCode}"));
    }

    private Guid SeedFilm(string[]? tags = null) =>
        LibraryItemSeeder.SeedVideo(
            _libraryManager,
            _mediaStreamRepository,
            _fixturePath,
            "mp4",
            _streams,
            $"Consolidation film {Guid.NewGuid()}",
            runTimeTicks: TimeSpan.FromSeconds(FixtureSeconds).Ticks,
            tags: tags);

    private static async Task<PlaybackSessionResponse> CreateSessionAsync(HttpClient client, Guid itemId, Guid userId, string playSessionId)
    {
        var (capabilities, constraints) = EndToEndCapabilityPresets.DirectPlay();
        var request = new CreatePlaybackSessionRequest(itemId, userId, capabilities, constraints, PlaySessionId: playSessionId);
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

    /// <summary>
    /// The e2e host with the legacy chain made unreachable for this journey: the planner the film
    /// planner wraps and the live-stream resolver both throw.
    /// </summary>
    public sealed class LegacyFreeFactory : E2eApplicationFactory
    {
        /// <inheritdoc/>
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IPlaybackSessionPlanner>(provider => new EngineFilmPlanner(
                    new LegacyChainReached(),
                    provider.GetRequiredService<IPlaybackEngine>(),
                    provider.GetRequiredService<IV2PlanStore>(),
                    provider.GetRequiredService<ILogger<EngineFilmPlanner>>()));
                services.AddSingleton<IPlaybackLiveStreamResolver, LegacyChainReached>();
            });
        }
    }

    private sealed class LegacyChainReached : IPlaybackSessionPlanner, IPlaybackLiveStreamResolver
    {
        public PlaybackPlan? PlanAudio(MediaOptions options) => throw new InvalidOperationException("The legacy planner (StreamBuilder) was reached.");

        public PlaybackPlan? PlanVideo(MediaOptions options) => throw new InvalidOperationException("The legacy planner (StreamBuilder) was reached.");

        public StreamInfo Resolve(
            PlaybackSessionId sessionId,
            StreamInfo legacyStreamInfo,
            MediaSourceInfo mediaSource,
            DeviceProfile profile,
            Guid itemId,
            string? deviceId,
            string playSessionId,
            long startTimeTicks,
            bool alwaysBurnInSubtitleWhenTranscoding)
            => throw new InvalidOperationException("The live-stream resolver (ToStreamInfo) was reached.");
    }
}
