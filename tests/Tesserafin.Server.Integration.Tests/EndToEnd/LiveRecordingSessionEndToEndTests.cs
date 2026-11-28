using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Persistence;
using Tesserafin.Extensions.Json;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.LiveTv;
using Tesserafin.Model.MediaInfo;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Engine;
using Tesserafin.Server.Integration.Tests.PlaybackCredentials;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// A Live TV recording still being written, played through <c>Playback/Sessions</c> on the
/// legacy-free host: the session opens the recording as a stream it holds, the v2 engine decides on
/// it (never a direct play: the file is still growing), and ffmpeg reads it through its provider
/// (<c>pipe:0</c>) into the session's live HLS route - never through the <c>[Authorize]</c>d
/// <c>LiveTv/LiveRecordings</c> route, which ffmpeg cannot authenticate to.
/// </summary>
public sealed class LiveRecordingSessionEndToEndTests : IClassFixture<DirectPlayFilmEndToEndTests.LegacyFreeFactory>, IAsyncLifetime
{
    private const int FixtureSeconds = 60;
    private const int AlreadyRecordedSeconds = 12;
    private const string Password = "recording-password";

    private readonly DirectPlayFilmEndToEndTests.LegacyFreeFactory _factory;
    private readonly CancellationTokenSource _writing = new();
    private HttpClient _admin = null!;
    private string _workDir = null!;
    private Task _writer = Task.CompletedTask;

    public LiveRecordingSessionEndToEndTests(DirectPlayFilmEndToEndTests.LegacyFreeFactory factory)
    {
        _factory = factory;
    }

    public async ValueTask InitializeAsync()
    {
        _admin = _factory.CreateClient();
        _admin.Timeout = TimeSpan.FromSeconds(120);
        var (accessToken, _) = await _factory.EnsureAuthenticatedAsync();
        _admin.DefaultRequestHeaders.AddAuthHeader(accessToken);
        _workDir = Directory.CreateTempSubdirectory("consol-recording-e2e-").FullName;
    }

    public async ValueTask DisposeAsync()
    {
        await _writing.CancelAsync();
        try
        {
            await _writer;
        }
        catch (OperationCanceledException)
        {
        }

        _writing.Dispose();
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
    public async Task RecordingBeingWritten_PlaysFromTheSessionLiveRoute_ThroughItsProvider()
    {
        const string PlaySession = "recording-ps";
        var (itemId, recordingId) = await StartRecordingAsync();
        var (owner, ownerId) = await SignInAsync("recording-owner");
        var (intruder, _) = await SignInAsync("recording-intruder");
        var mediaSources = _factory.Services.GetRequiredService<IMediaSourceManager>();
        var liveStreams = _factory.Services.GetRequiredService<PlaybackSessionLiveStreams>();

        // Decision: the engine's, on the recording the session opened; a growing file is never
        // handed to the client as it is.
        var session = await CreateSessionAsync(owner, itemId, ownerId, PlaySession);
        Assert.Equal(PlaybackEngine.EngineVersion, session.DecisionVersion);
        Assert.Contains(session.Method, new[] { PlaybackMethod.Remux, PlaybackMethod.Transcode });
        var held = liveStreams.Get(new PlaybackSessionId(session.Id));
        Assert.NotNull(held);
        Assert.Equal(recordingId, mediaSources.GetLiveStreamInfo(held)?.OriginalStreamId);

        var descriptor = await GetDescriptorAsync(owner, session.Id);
        Assert.StartsWith(FormattableString.Invariant($"/Playback/Sessions/{session.Id:N}/Hls/live.m3u8?"), descriptor.Url, StringComparison.Ordinal);
        Assert.Equal(StreamingProtocol.Hls, descriptor.Protocol);
        Assert.DoesNotContain("LiveRecordings", descriptor.Url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ApiKey", descriptor.Url, StringComparison.OrdinalIgnoreCase);

        // The live playlist ffmpeg writes from the growing file, every segment on the session's route.
        var segments = await ReadLivePlaylistAsync(owner, descriptor.Url);
        Assert.All(segments, uri => Assert.Contains(FormattableString.Invariant($"/Playback/Sessions/{session.Id:N}/Hls/hls/"), uri, StringComparison.Ordinal));
        var media = await GetBytesAsync(owner, segments.Last());
        var init = segments.Count > 1 && segments[0].Contains("-1.", StringComparison.Ordinal) ? await GetBytesAsync(owner, segments[0]) : [];
        var produced = Path.Combine(_workDir, $"produced-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(produced, [.. init, .. media], TestContext.Current.CancellationToken);
        var streams = (await EndToEndMediaFixtures.ProbeStreamsAsync(produced)).Select(line => string.Join(',', line.Split(',').Take(2))).ToList();
        Assert.Contains("video,h264", streams);
        Assert.Contains("audio,aac", streams);

        // The Web client's own path: the media source the descriptor names is one the item lists, and
        // a media-element credential minted for it reads the live playlist and a segment on its own.
        var mediaSourceId = System.Web.HttpUtility.ParseQueryString(new Uri("http://host" + descriptor.Url).Query)["mediaSourceId"]!;
        Assert.Equal(itemId.ToString("N"), mediaSourceId);
        using (var item = await owner.GetAsync(FormattableString.Invariant($"Items/{itemId:N}?userId={ownerId:N}"), TestContext.Current.CancellationToken))
        {
            var itemBody = await item.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(item.IsSuccessStatusCode, FormattableString.Invariant($"GET Items/{itemId:N}: {(int)item.StatusCode} {itemBody}"));
            using var document = JsonDocument.Parse(itemBody);
            Assert.Contains(
                document.RootElement.GetProperty("MediaSources").EnumerateArray(),
                source => source.GetProperty("Id").GetString() == mediaSourceId);
        }

        var capability = await MintMediaCapabilityAsync(owner, itemId, mediaSourceId, PlaySession);
        using (var anonymous = _factory.CreateClient())
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(anonymous, descriptor.Url));
            var credentialed = await ReadLivePlaylistAsync(anonymous, descriptor.Url + "&playbackCapability=" + Uri.EscapeDataString(capability));
            Assert.All(credentialed.Where(uri => !uri.Contains("-1.", StringComparison.Ordinal)), uri => Assert.Contains("playbackCapability=", uri, StringComparison.Ordinal));
            Assert.NotEmpty(await GetBytesAsync(anonymous, credentialed.Last()));
        }

        // Another user reaches neither the playlist nor a segment.
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(intruder, descriptor.Url));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(intruder, segments.Last()));

        // A re-plan keeps the recording stream the session holds.
        var replanned = await ReplaceSessionAsync(owner, session.Id, itemId, ownerId);
        Assert.Contains(replanned.Method, new[] { PlaybackMethod.Remux, PlaybackMethod.Transcode });
        Assert.Equal(held, liveStreams.Get(new PlaybackSessionId(session.Id)));

        // The stop releases the stream and the session's routes; the recording itself goes on.
        using (var stop = await owner.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{session.Id:N}"), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        }

        await WaitUntilAsync(() => mediaSources.GetLiveStreamInfo(held) is null, "the recording stream to be released");
        Assert.Null(liveStreams.Get(new PlaybackSessionId(session.Id)));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(owner, descriptor.Url));
        Assert.False(_writer.IsCompleted, "the recording must still be being written");
    }

    /// <summary>
    /// Starts a recording the way the recordings manager does for the session journey: a file in
    /// the library that is still growing, and an active recording naming that path. The first
    /// seconds are on disk at once; the rest arrives at the stream's own rate.
    /// </summary>
    private async Task<(Guid ItemId, string RecordingId)> StartRecordingAsync()
    {
        var fixture = await EndToEndMediaFixtures.CreateTunerTransportStreamAsync(_workDir, FixtureSeconds);
        var bytes = await File.ReadAllBytesAsync(fixture, TestContext.Current.CancellationToken);
        var probed = await _factory.Services.GetRequiredService<IMediaEncoder>().GetMediaInfo(
            new MediaInfoRequest
            {
                MediaSource = new MediaSourceInfo { Path = fixture, Protocol = MediaProtocol.File },
                MediaType = DlnaProfileType.Video,
            },
            TestContext.Current.CancellationToken);

        var recording = Path.Combine(_workDir, $"recording-{Guid.NewGuid():N}.ts");
        var bytesPerSecond = bytes.Length / FixtureSeconds;
        var head = bytesPerSecond * AlreadyRecordedSeconds;
        var output = new FileStream(recording, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        await output.WriteAsync(bytes.AsMemory(0, head), TestContext.Current.CancellationToken);
        await output.FlushAsync(TestContext.Current.CancellationToken);
        _writer = Task.Run(
            async () =>
            {
                await using (output)
                {
                    var written = head;
                    var chunk = bytesPerSecond / 5;
                    while (written < bytes.Length)
                    {
                        await Task.Delay(200, _writing.Token);
                        var count = Math.Min(chunk, bytes.Length - written);
                        await output.WriteAsync(bytes.AsMemory(written, count), _writing.Token);
                        await output.FlushAsync(_writing.Token);
                        written += count;
                    }

                    // A recording stays open until its timer ends.
                    await Task.Delay(Timeout.Infinite, _writing.Token);
                }
            },
            CancellationToken.None);

        var itemId = LibraryItemSeeder.SeedVideo(
            _factory.Services.GetRequiredService<IItemWriteCommands>(),
            _factory.Services.GetRequiredService<IMediaStreamRepository>(),
            recording,
            "ts",
            [.. probed.MediaStreams.Where(stream => stream.Type is MediaStreamType.Video or MediaStreamType.Audio)],
            $"Consolidation recording {Guid.NewGuid()}",
            runTimeTicks: TimeSpan.FromSeconds(AlreadyRecordedSeconds).Ticks,
            kind: SeededVideoKind.HomeVideo);

        var recordingId = Guid.NewGuid().ToString("N");
        var active = _factory.Services.GetRequiredService<ActiveRecordings>();
        Assert.True(active.TryAdd(recordingId, new ActiveRecordingInfo
        {
            Id = recordingId,
            Path = recording,
            Timer = new TimerInfo
            {
                Id = recordingId,
                Status = RecordingStatus.InProgress,
                StartDate = DateTime.UtcNow.AddSeconds(-AlreadyRecordedSeconds),
                EndDate = DateTime.UtcNow.AddHours(1),
            },
            CancellationTokenSource = new CancellationTokenSource(),
        }));
        return (itemId, recordingId);
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

    private static async Task<PlaybackSessionResponse> ReplaceSessionAsync(HttpClient client, Guid sessionId, Guid itemId, Guid userId)
    {
        var (capabilities, constraints) = EndToEndCapabilityPresets.BrowserRemuxOverHls();
        var request = new ReplacePlaybackSessionRequest(itemId, userId, capabilities, constraints);
        using var response = await client.PutAsJsonAsync(FormattableString.Invariant($"Playback/Sessions/{sessionId:N}"), request, JsonDefaults.Options, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, FormattableString.Invariant($"PUT Playback/Sessions: {(int)response.StatusCode} {body}"));
        return JsonSerializer.Deserialize<PlaybackSessionResponse>(body, JsonDefaults.Options)!;
    }

    private static async Task<string> MintMediaCapabilityAsync(HttpClient client, Guid itemId, string mediaSourceId, string playSessionId)
    {
        using var response = await client.PostAsJsonAsync(
            "Playback/Capabilities",
            new { PlaySessionId = playSessionId, ItemId = itemId, MediaSourceId = mediaSourceId, Scopes = new[] { "Media" } },
            JsonDefaults.Options,
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, FormattableString.Invariant($"POST Playback/Capabilities: {(int)response.StatusCode} {body}"));
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("Value").GetString()!;
    }

    private static async Task<PlaybackSessionStreamDescriptor> GetDescriptorAsync(HttpClient client, Guid sessionId)
    {
        using var response = await client.GetAsync(FormattableString.Invariant($"Playback/Sessions/{sessionId:N}/Stream?startTimeTicks=0"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, FormattableString.Invariant($"GET .../Stream: {(int)response.StatusCode} {body}"));
        return JsonSerializer.Deserialize<PlaybackSessionStreamDescriptor>(body, JsonDefaults.Options)!;
    }

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
