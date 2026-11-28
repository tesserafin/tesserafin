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
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Persistence;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Channels;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.MediaInfo;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Engine;
using Tesserafin.Server.Integration.Tests.PlaybackCredentials;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// Consolidation step 1, channel-plugin items (docs/playback-path-inventory.md §7): an item a channel
/// plugin supplies is read through the session like any other source of its kind (decided
/// 2026-11-20) - a remote file its plugin names is executed from its url into the session's own HLS
/// output, and a source no session provider reads is refused. The channel plugin is
/// <see cref="TestChannel"/>, registered on the legacy-free host, where the legacy planner throws.
/// </summary>
/// <remarks>
/// Not <c>Category=Smoke</c>, and required by name in the hosted integration job, for the reasons
/// given on <see cref="DirectPlayFilmEndToEndTests"/>.
/// </remarks>
public sealed class ChannelItemEndToEndTests : IClassFixture<DirectPlayFilmEndToEndTests.LegacyFreeFactory>, IAsyncLifetime
{
    private const string Password = "channel-password";
    private static readonly long _runTimeTicks = TimeSpan.FromSeconds(12).Ticks;

    private readonly DirectPlayFilmEndToEndTests.LegacyFreeFactory _factory;
    private HttpClient _admin = null!;
    private string _workDir = null!;
    private string _logDirectory = null!;
    private string _fixture = null!;
    private Guid _channelId;

    public ChannelItemEndToEndTests(DirectPlayFilmEndToEndTests.LegacyFreeFactory factory)
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
        _workDir = Directory.CreateTempSubdirectory("consol-channel-e2e-").FullName;
        _fixture = await EndToEndMediaFixtures.CreateH264AacMp4Async(_workDir, durationSeconds: 12);

        // The channel's library item, created the way a server start creates it for a plugin.
        var channels = await _factory.Services.GetRequiredService<IChannelManager>().GetChannelsInternalAsync(new ChannelQuery());
        _channelId = Assert.Single(channels.Items, channel => channel.Name == TestChannel.ChannelName).Id;
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
    public async Task ChannelItem_NamingARemoteFile_IsExecutedFromItsUrlIntoTheSessionHlsOutput()
    {
        await using var remote = StaticHttpFile.Start(_fixture);
        var (client, userId) = await SignInAsync("channel-remote");
        var itemId = Seed(new MediaSourceInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            Path = remote.Url,
            Protocol = MediaProtocol.Http,
            IsRemote = true,
            Container = "mp4",
            RunTimeTicks = _runTimeTicks,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            MediaStreams =
            [
                new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264", Width = EndToEndMediaFixtures.Width, Height = EndToEndMediaFixtures.Height, IsDefault = true },
                new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "aac", Channels = 2, IsDefault = true },
            ],
        });
        var session = await CreateSessionAsync(client, itemId, userId, "channel-remote-ps");

        Assert.NotEqual(PlaybackMethod.DirectPlay, session.Method);
        Assert.Equal(PlaybackEngine.EngineVersion, session.DecisionVersion);

        var descriptor = await GetDescriptorAsync(client, session.Id);
        Assert.StartsWith(FormattableString.Invariant($"/Playback/Sessions/{session.Id:N}/Hls/main.m3u8?"), descriptor.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", descriptor.Url, StringComparison.Ordinal);

        var (init, segments) = await ReadPlaylistAsync(client, descriptor.Url);
        var produced = Path.Combine(_workDir, $"channel-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(produced, [.. await GetBytesAsync(client, init), .. await GetBytesAsync(client, segments[0])], TestContext.Current.CancellationToken);
        Assert.Equal(["video,h264", "audio,aac"], (await EndToEndMediaFixtures.ProbeStreamsAsync(produced)).Select(line => string.Join(',', line.Split(',').Take(2))).ToList());

        // What FFmpeg read: the url the channel plugin named (its loopback port is this test's own).
        var commands = await FfmpegCommandsAsync(remote.Url);
        Assert.NotEmpty(commands);

        using var stop = await client.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{session.Id:N}"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChannelItem_WithASourceNoSessionProviderReads_IsRefusedExplicitly(bool requiresOpening)
    {
        // An endless stream, or a source that must be opened first: no provider opens channel sources.
        var (client, userId) = await SignInAsync(requiresOpening ? "channel-refused-open" : "channel-refused-endless");
        var itemId = Seed(new MediaSourceInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            Path = "http://127.0.0.1:1/endless",
            Protocol = MediaProtocol.Http,
            IsRemote = true,
            IsInfiniteStream = !requiresOpening,
            RunTimeTicks = requiresOpening ? _runTimeTicks : null,
            RequiresOpening = requiresOpening,
            OpenToken = requiresOpening ? "channel-open-token" : null,
            Container = "ts",
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            MediaStreams =
            [
                new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264", Width = EndToEndMediaFixtures.Width, Height = EndToEndMediaFixtures.Height, IsDefault = true },
                new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "aac", Channels = 2, IsDefault = true },
            ],
        });

        // On this host the legacy planner throws: a hand-over would be a 500, not this 422.
        var (capabilities, constraints) = EndToEndCapabilityPresets.BrowserRemuxOverHls();
        var request = new CreatePlaybackSessionRequest(itemId, userId, capabilities, constraints, PlaySessionId: requiresOpening ? "channel-refused-open-ps" : "channel-refused-endless-ps");
        using var response = await client.PostAsJsonAsync("Playback/Sessions", request, JsonDefaults.Options, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, FormattableString.Invariant($"POST Playback/Sessions: {(int)response.StatusCode} {body}"));
    }

    /// <summary>
    /// Seeds a channel-plugin item of <see cref="TestChannel"/> whose plugin serves <paramref name="source"/>.
    /// </summary>
    private Guid Seed(MediaSourceInfo source)
    {
        var externalId = "channel-item-" + Guid.NewGuid().ToString("N");
        _factory.Channel.Serve(externalId, source);
        return LibraryItemSeeder.SeedVideo(
            _factory.Services.GetRequiredService<IItemWriteCommands>(),
            _factory.Services.GetRequiredService<IMediaStreamRepository>(),
            _fixture,
            source.Container,
            source.MediaStreams,
            $"Consolidation channel item {externalId}",
            runTimeTicks: _runTimeTicks,
            configure: movie =>
            {
                movie.Path = null;
                movie.ChannelId = _channelId;
                movie.ExternalId = externalId;
            });
    }

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

    private async Task<List<string>> FfmpegCommandsAsync(string url)
    {
        var commands = new List<string>();
        if (!Directory.Exists(_logDirectory))
        {
            return commands;
        }

        foreach (var log in Directory.EnumerateFiles(_logDirectory, "FFmpeg.*.log").Order(StringComparer.Ordinal))
        {
            var text = await File.ReadAllTextAsync(log, TestContext.Current.CancellationToken);
            if (text.Contains(url, StringComparison.Ordinal))
            {
                commands.Add(text);
            }
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
