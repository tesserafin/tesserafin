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
/// Consolidation step 1, progressive output (docs/playback-path-inventory.md §7, step 4): a client
/// that declares no HLS output is served one HTTP stream from the session's own progressive route,
/// executing the retained plan with the same executor, job ownership and stop rules as the HLS
/// route. The legacy planner throws on this host. What was produced is probed off the bytes.
/// </summary>
/// <remarks>
/// Not <c>Category=Smoke</c>, and required by name in the hosted integration job, for the reasons
/// given on <see cref="DirectPlayFilmEndToEndTests"/>.
/// </remarks>
public sealed class ProgressiveEndToEndTests : IClassFixture<DirectPlayFilmEndToEndTests.LegacyFreeFactory>, IAsyncLifetime
{
    private const int FixtureSeconds = 12;
    private const int ReadBytes = 256 * 1024;
    private const string Password = "progressive-password";

    private readonly DirectPlayFilmEndToEndTests.LegacyFreeFactory _factory;
    private HttpClient _admin = null!;
    private string _workDir = null!;
    private string _logDirectory = null!;
    private ITranscodeManager _transcodeManager = null!;

    public ProgressiveEndToEndTests(DirectPlayFilmEndToEndTests.LegacyFreeFactory factory)
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
        _transcodeManager = _factory.Services.GetRequiredService<ITranscodeManager>();
        _workDir = Directory.CreateTempSubdirectory("consol-progressive-e2e-").FullName;
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
    public async Task Progressive_TranscodeForAClientWithNoHlsOutput_StreamsTsFromTheSessionRoute()
    {
        const string PlaySession = "progressive-transcode-ps";
        var (client, userId) = await SignInAsync("progressive-transcode");
        var (intruder, _) = await SignInAsync("progressive-intruder");
        var itemId = await SeedFilmAsync("mpeg4", "aac", avi: true);
        var session = await CreateSessionAsync(client, itemId, userId, PlaySession);

        Assert.Equal(PlaybackMethod.Transcode, session.Method);
        Assert.Equal(PlaybackEngine.EngineVersion, session.DecisionVersion);

        var descriptor = await GetDescriptorAsync(client, session.Id);
        Assert.StartsWith(FormattableString.Invariant($"/Playback/Sessions/{session.Id:N}/Progressive.ts?"), descriptor.Url, StringComparison.Ordinal);
        Assert.Equal(StreamingProtocol.Http, descriptor.Protocol);
        Assert.Equal("ts", descriptor.Container);

        // Another user is refused before anything is started for them.
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(intruder, descriptor.Url));

        var bytes = await ReadHeadAsync(client, descriptor.Url);
        Assert.Equal(["video,h264", "audio,aac"], await ProbeAsync(bytes));
        var commands = await FfmpegCommandsAsync(itemId);
        Assert.Contains(commands, command => command.Contains("-codec:v:0 libx264", StringComparison.Ordinal) || command.Contains("-codec:v:0 h264_", StringComparison.Ordinal));
        var job = _transcodeManager.GetTranscodingJob(PlaySession);
        Assert.NotNull(job);
        Assert.Equal(userId, job.UserId);

        // A progressive seek: the stream restarts at the requested position.
        var seekBytes = await ReadHeadAsync(client, descriptor.Url + "&startTimeTicks=" + TimeSpan.FromSeconds(6).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.NotEmpty(seekBytes);
        var seekCommands = await FfmpegCommandsAsync(itemId);
        Assert.Equal(2, seekCommands.Count);
        Assert.Contains(seekCommands, command => command.Contains("-ss 00:00:06", StringComparison.Ordinal));

        using (var stop = await client.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{session.Id:N}"), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        }

        Assert.Null(_transcodeManager.GetTranscodingJob(PlaySession));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, descriptor.Url));
    }

    [Fact]
    public async Task Progressive_RemuxForAClientWithNoHlsOutput_CopiesBothStreamsIntoTs()
    {
        var (client, userId) = await SignInAsync("progressive-remux");
        var itemId = await SeedFilmAsync("libx264", "aac", avi: false);
        var session = await CreateSessionAsync(client, itemId, userId, "progressive-remux-ps", allowCopy: true);

        Assert.Equal(PlaybackMethod.Remux, session.Method);
        var descriptor = await GetDescriptorAsync(client, session.Id);
        Assert.StartsWith(FormattableString.Invariant($"/Playback/Sessions/{session.Id:N}/Progressive.ts?"), descriptor.Url, StringComparison.Ordinal);

        var bytes = await ReadHeadAsync(client, descriptor.Url);
        Assert.Equal(["video,h264", "audio,aac"], await ProbeAsync(bytes));
        Assert.All(await FfmpegCommandsAsync(itemId), command =>
        {
            Assert.Contains("-codec:v:0 copy", command, StringComparison.Ordinal);
            Assert.Contains("-codec:a:0 copy", command, StringComparison.Ordinal);
        });

        using var stop = await client.DeleteAsync(FormattableString.Invariant($"Playback/Sessions/{session.Id:N}"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
    }

    private async Task<Guid> SeedFilmAsync(string videoEncoder, string audioEncoder, bool avi)
    {
        var path = await EndToEndMediaFixtures.CreateFilmAsync(_workDir, $"film-{Guid.NewGuid():N}.{(avi ? "avi" : "mkv")}", FixtureSeconds, videoEncoder, audioEncoder, format: avi ? "avi" : "matroska");
        MediaStream[] streams =
        [
            new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = videoEncoder == "mpeg4" ? "mpeg4" : "h264", Width = EndToEndMediaFixtures.Width, Height = EndToEndMediaFixtures.Height, IsDefault = true, IsAVC = avi ? false : null },
            new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "aac", Channels = 2, IsDefault = true },
        ];
        return LibraryItemSeeder.SeedVideo(
            _factory.Services.GetRequiredService<IItemWriteCommands>(),
            _factory.Services.GetRequiredService<IMediaStreamRepository>(),
            path,
            avi ? "avi" : "mkv",
            streams,
            $"Consolidation progressive {Guid.NewGuid()}",
            runTimeTicks: TimeSpan.FromSeconds(FixtureSeconds).Ticks);
    }

    private static async Task<PlaybackSessionResponse> CreateSessionAsync(HttpClient client, Guid itemId, Guid userId, string playSessionId, bool allowCopy = false)
    {
        var (capabilities, constraints) = EndToEndCapabilityPresets.TranscodeProgressiveHttp();
        if (allowCopy)
        {
            // Decodes H.264/AAC (in MP4): a Matroska source is then a remux, not a transcode.
            capabilities = capabilities with { Decode = capabilities.Decode with { DirectPlayProfiles = [new DecodeProfile(MediaKind.Video, ["mp4"], ["h264"], ["aac"])] } };
            constraints = constraints with { AllowDirectStream = true, AllowVideoStreamCopy = true, AllowAudioStreamCopy = true };
        }

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

    /// <summary>Reads the head of a progressive stream, as a player that starts playing does.</summary>
    private static async Task<byte[]> ReadHeadAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, FormattableString.Invariant($"GET '{url}': {(int)response.StatusCode}"));
        await using var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        var buffer = new byte[ReadBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), TestContext.Current.CancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return buffer[..total];
    }

    private async Task<IReadOnlyList<string>> ProbeAsync(byte[] bytes)
    {
        Assert.NotEmpty(bytes);
        var path = Path.Combine(_workDir, $"progressive-{Guid.NewGuid():N}.ts");
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        // MPEG-TS lists each stream under its program too: one line per distinct stream.
        return (await EndToEndMediaFixtures.ProbeStreamsAsync(path)).Select(line => string.Join(',', line.Split(',').Take(2))).Distinct().ToList();
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        return response.StatusCode;
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
            await using var file = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(file);
            commands.Add(await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
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
