using System;
using System.Collections.Generic;
using System.Globalization;
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
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.MediaInfo;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Engine;
using Tesserafin.Server.Integration.Tests.PlaybackCredentials;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// Consolidation step 1, local disc images (docs/playback-path-inventory.md §7): a DVD or Blu-ray
/// ISO is opened by the session's executor itself - the <c>dvdvideo</c> demuxer, the
/// <c>bluray</c> protocol - transcoded or remuxed into the session's own HLS output, and never
/// handed to the client as it is (owner decision 2026-11-25). The legacy planner throws on this host.
/// </summary>
/// <remarks>
/// <para>
/// The images come from <c>ci/disc-image-fixtures.sh</c>: 200 s, one solid colour per 10 s band.
/// Each position is proved by the colour of the first frame the session serves for it, not only
/// by a playlist entry: start, a seek, the stopped position stored and a resumed session reading
/// from it. The start of a band is avoided by up to one GOP: an MPEG-TS input seek (a Blu-ray
/// image is one) lands on the next keyframe after the target, on a plain <c>.m2ts</c> as well
/// (measured on FFmpeg 6.1 and 7.1).
/// </para>
/// <para>
/// Skipped unless <c>TESSERAFIN_DISC_IMAGE_FIXTURES</c> names the fixture directory and the
/// server's FFmpeg (<c>TESSERAFIN_PROJECT_FFMPEG</c>) opens the image's kind: a distribution
/// FFmpeg 6.1 has no <c>dvdvideo</c> demuxer, and the hosted runner has neither the fixtures nor
/// the project's FFmpeg yet. Not required by name in the hosted job for that reason.
/// </para>
/// </remarks>
public sealed class DiscImageEndToEndTests : IClassFixture<DirectPlayFilmEndToEndTests.LegacyFreeFactory>, IAsyncLifetime
{
    private const string Password = "disc-image-password";
    private const int BandSeconds = 10;

    // ci/disc-image-fixtures.sh COLOURS, in order, as CSS/X11 names them.
    private static readonly (string Name, int R, int G, int B)[] _bands =
    [
        ("red", 255, 0, 0), ("lime", 0, 255, 0), ("blue", 0, 0, 255), ("yellow", 255, 255, 0),
        ("cyan", 0, 255, 255), ("magenta", 255, 0, 255), ("white", 255, 255, 255), ("gray", 128, 128, 128),
        ("maroon", 128, 0, 0), ("olive", 128, 128, 0), ("green", 0, 128, 0), ("purple", 128, 0, 128),
        ("teal", 0, 128, 128), ("navy", 0, 0, 128), ("orange", 255, 165, 0), ("pink", 255, 192, 203),
        ("brown", 165, 42, 42), ("black", 0, 0, 0), ("silver", 192, 192, 192), ("gold", 255, 215, 0),
    ];

    private readonly DirectPlayFilmEndToEndTests.LegacyFreeFactory _factory;
    private HttpClient _admin = null!;
    private string _workDir = null!;
    private string _logDirectory = null!;

    public DiscImageEndToEndTests(DirectPlayFilmEndToEndTests.LegacyFreeFactory factory)
    {
        _factory = factory;
    }

    public async ValueTask InitializeAsync()
    {
        _admin = _factory.CreateClient();
        _admin.Timeout = TimeSpan.FromSeconds(180);
        var (accessToken, _) = await _factory.EnsureAuthenticatedAsync();
        _admin.DefaultRequestHeaders.AddAuthHeader(accessToken);
        _factory.Services.GetRequiredService<IServerConfigurationManager>().Configuration.PlaybackShadow.Mode = PlaybackEngineMode.Legacy;
        _logDirectory = _factory.Services.GetRequiredService<IApplicationPaths>().LogDirectoryPath;
        _workDir = Directory.CreateTempSubdirectory("consol-disc-image-e2e-").FullName;
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

    [Theory]
    [InlineData(IsoType.Dvd)]
    [InlineData(IsoType.BluRay)]
    public async Task DiscImage_PlaysFromTheSession_AtThePositionsItIsAskedFor_ForItsOwnerOnly(IsoType isoType)
    {
        var image = RequireImage(isoType);
        var name = isoType == IsoType.Dvd ? "iso-dvd" : "iso-bd";
        var configuration = _factory.Services.GetRequiredService<IServerConfigurationManager>().Configuration;
        var minResumeSeconds = configuration.MinResumeDurationSeconds;
        configuration.MinResumeDurationSeconds = 0;
        try
        {
            // The scan's probe, through the server's own FFmpeg: a DVD image is only readable by
            // naming the dvdvideo demuxer, so these streams are the proof the probe opens it.
            var source = new MediaSourceInfo { Path = image, Protocol = MediaProtocol.File, VideoType = VideoType.Iso, IsoType = isoType };
            var probed = await _factory.Services.GetRequiredService<IMediaEncoder>().GetMediaInfo(
                new MediaInfoRequest { MediaSource = source, MediaType = DlnaProfileType.Video },
                TestContext.Current.CancellationToken);
            Assert.Contains(probed.MediaStreams, stream => stream.Type == MediaStreamType.Video);
            Assert.Contains(probed.MediaStreams, stream => stream.Type == MediaStreamType.Audio);
            Assert.InRange(TimeSpan.FromTicks(probed.RunTimeTicks ?? 0).TotalSeconds, 195, 205);

            var (owner, ownerId) = await SignInAsync(name + "-owner");
            var (intruder, _) = await SignInAsync(name + "-intruder");
            var itemId = LibraryItemSeeder.SeedVideo(
                _factory.Services.GetRequiredService<IItemWriteCommands>(),
                _factory.Services.GetRequiredService<IMediaStreamRepository>(),
                image,
                "iso",
                [.. probed.MediaStreams.Where(stream => stream.Type is MediaStreamType.Video or MediaStreamType.Audio)],
                $"Consolidation {isoType} image {Guid.NewGuid()}",
                runTimeTicks: probed.RunTimeTicks!.Value,
                configure: movie =>
                {
                    movie.VideoType = VideoType.Iso;
                    movie.IsoType = isoType;
                });

            // Decision: the engine's, never a direct play of the image.
            var session = await CreateSessionAsync(owner, itemId, ownerId, name + "-ps1");
            Assert.NotEqual(PlaybackMethod.DirectPlay, session.Method);
            Assert.Equal(PlaybackEngine.EngineVersion, session.DecisionVersion);
            var descriptor = await GetDescriptorAsync(owner, session.Id, 0);
            Assert.StartsWith(FormattableString.Invariant($"/Playback/Sessions/{session.Id:N}/Hls/main.m3u8?"), descriptor.Url, StringComparison.Ordinal);

            // Start, then a seek: the colour the session serves is the band of the position.
            var playlist = await ReadPlaylistAsync(owner, descriptor.Url);
            Assert.InRange(playlist.Segments[^1].Start, 180, 205);
            await AssertServesBandAsync(owner, playlist, 0);
            await AssertServesBandAsync(owner, playlist, 65);
            await AssertServesBandAsync(owner, playlist, 125);

            // What FFmpeg read: the image through its own reader, not a concatenation.
            var commands = await FfmpegCommandsAsync(itemId);
            Assert.NotEmpty(commands);
            Assert.All(commands, command =>
            {
                Assert.Contains(isoType == IsoType.Dvd ? "-f dvdvideo -i \"" + image + "\"" : "-i bluray:\"" + image + "\"", command, StringComparison.Ordinal);
                Assert.DoesNotContain("-f concat", command, StringComparison.Ordinal);
            });

            // Another user can neither read nor stop the owner's session.
            Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(intruder, descriptor.Url));
            Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(intruder, playlist.Segments[0].Url));
            using (var stop = await intruder.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{session.Id:N}"), TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.Forbidden, stop.StatusCode);
            }

            // Stop at 125 s: the position is stored, and the session and its output go.
            var position = TimeSpan.FromSeconds(125).Ticks;
            await PostAsync(owner, "Sessions/Playing", new { ItemId = itemId, MediaSourceId = itemId.ToString("N"), PlaySessionId = name + "-ps1", PositionTicks = 0L, PlayMethod = "Transcode" });
            await PostAsync(owner, "Sessions/Playing/Stopped", new { ItemId = itemId, MediaSourceId = itemId.ToString("N"), PlaySessionId = name + "-ps1", PositionTicks = position });
            using (var stop = await owner.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{session.Id:N}"), TestContext.Current.CancellationToken))
            {
                Assert.Contains(stop.StatusCode, new[] { HttpStatusCode.NoContent, HttpStatusCode.NotFound });
            }

            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(owner, descriptor.Url));
            var userData = await owner.GetFromJsonAsync<UserItemDataDto>(FormattableString.Invariant($"UserItems/{itemId:N}/UserData"), JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal(position, userData!.PlaybackPositionTicks);

            // Resume: a new session read from the stored position serves that position's band.
            var resumed = await CreateSessionAsync(owner, itemId, ownerId, name + "-ps2");
            var resumedPlaylist = await ReadPlaylistAsync(owner, (await GetDescriptorAsync(owner, resumed.Id, position)).Url);
            await AssertServesBandAsync(owner, resumedPlaylist, 125);
            using (var stop = await owner.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{resumed.Id:N}"), TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
            }
        }
        finally
        {
            configuration.MinResumeDurationSeconds = minResumeSeconds;
        }
    }

    private string RequireImage(IsoType isoType)
    {
        var directory = Environment.GetEnvironmentVariable("TESSERAFIN_DISC_IMAGE_FIXTURES");
        var image = string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, isoType == IsoType.Dvd ? "dvd.iso" : "bd.iso");
        Assert.SkipWhen(image is null || !File.Exists(image), "Set TESSERAFIN_DISC_IMAGE_FIXTURES to the output of ci/disc-image-fixtures.sh.");
        Assert.SkipUnless(
            _factory.Services.GetRequiredService<IMediaEncoder>().SupportsDiscImage(isoType),
            FormattableString.Invariant($"The server's FFmpeg does not open a {isoType} image; set TESSERAFIN_PROJECT_FFMPEG to the project's FFmpeg build."));
        return Path.GetFullPath(image!);
    }

    /// <summary>
    /// Reads the first segment that starts at or after <paramref name="seconds"/> and at least one
    /// GOP before its band ends, and checks the colour of the first frame it holds.
    /// </summary>
    private async Task AssertServesBandAsync(HttpClient client, Playlist playlist, double seconds)
    {
        var segment = playlist.Segments.First(s => s.Start >= seconds - 0.001 && s.Start % BandSeconds <= BandSeconds - 2);
        var produced = Path.Combine(_workDir, $"segment-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(produced, [.. await GetBytesAsync(client, playlist.Init), .. await GetBytesAsync(client, segment.Url)], TestContext.Current.CancellationToken);
        var (r, g, b) = await EndToEndMediaFixtures.ReadFirstFrameColourAsync(produced);
        var nearest = _bands
            .Select((band, index) => (band.Name, Index: index, Distance: Math.Pow(band.R - r, 2) + Math.Pow(band.G - g, 2) + Math.Pow(band.B - b, 2)))
            .MinBy(candidate => candidate.Distance);
        var expected = (int)(segment.Start / BandSeconds);
        Assert.True(
            nearest.Index == expected,
            FormattableString.Invariant($"segment at {segment.Start:F2} s: expected {_bands[expected].Name}, read {nearest.Name} (rgb {r},{g},{b})"));
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

    private static async Task<PlaybackSessionStreamDescriptor> GetDescriptorAsync(HttpClient client, Guid sessionId, long startTimeTicks)
    {
        using var response = await client.GetAsync(FormattableString.Invariant($"Playback/Sessions/{sessionId:N}/Stream?startTimeTicks={startTimeTicks}"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, FormattableString.Invariant($"GET .../Stream: {(int)response.StatusCode} {body}"));
        return JsonSerializer.Deserialize<PlaybackSessionStreamDescriptor>(body, JsonDefaults.Options)!;
    }

    private static async Task<Playlist> ReadPlaylistAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, FormattableString.Invariant($"GET '{url}': {(int)response.StatusCode} {body}"));
        var directory = url[..url.IndexOf('?', StringComparison.Ordinal)];
        directory = directory[..(directory.LastIndexOf('/') + 1)];
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var map = Assert.Single(lines, line => line.StartsWith("#EXT-X-MAP:URI=\"", StringComparison.Ordinal));
        var init = directory + map["#EXT-X-MAP:URI=\"".Length..map.LastIndexOf('"')];

        // A segment starts where the durations before it end.
        var segments = new List<Segment>();
        double start = 0;
        double duration = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                duration = double.Parse(line["#EXTINF:".Length..].Split(',')[0], CultureInfo.InvariantCulture);
            }
            else if (!line.StartsWith('#'))
            {
                segments.Add(new Segment(directory + line, start));
                start += duration;
            }
        }

        Assert.NotEmpty(segments);
        return new Playlist(init, segments);
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

    private static async Task PostAsync(HttpClient client, string url, object body)
    {
        using var response = await client.PostAsJsonAsync(url, body, JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, FormattableString.Invariant($"POST {url}: {(int)response.StatusCode}"));
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
        client.Timeout = TimeSpan.FromSeconds(180);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            AuthHelper.AuthHeaderName,
            MediaBoundaryFixture.AuthorizationHeader(deviceId, document.RootElement.GetProperty("AccessToken").GetString()));
        return (client, created.Id);
    }

    private sealed record Segment(string Url, double Start);

    private sealed record Playlist(string Init, List<Segment> Segments);
}
