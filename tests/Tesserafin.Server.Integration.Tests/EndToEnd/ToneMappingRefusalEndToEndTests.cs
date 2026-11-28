using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
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
/// Consolidation step 1, audit rows S1 and S2 (docs/playback-path-inventory.md §7, decided 2026-11-27): an
/// HDR10 film for a client that shows SDR only, on a server whose FFmpeg cannot tone-map. The
/// session is refused with a 422 that names the reason; the legacy planner, which throws on this
/// host, is never reached. The host declares no tone mapping whatever FFmpeg runs it, so the
/// refusal is proved on every machine, not only on one without <c>tonemapx</c>.
/// </summary>
public sealed class ToneMappingRefusalEndToEndTests : IClassFixture<ToneMappingRefusalEndToEndTests.NoToneMappingFactory>, IAsyncLifetime
{
    private const int FixtureSeconds = 6;
    private const string Password = "tonemap-refusal-password";

    private readonly NoToneMappingFactory _factory;
    private HttpClient _admin = null!;
    private string _workDir = null!;

    public ToneMappingRefusalEndToEndTests(NoToneMappingFactory factory)
    {
        _factory = factory;
    }

    public async ValueTask InitializeAsync()
    {
        _admin = _factory.CreateClient();
        _admin.Timeout = TimeSpan.FromSeconds(120);
        var (accessToken, _) = await _factory.EnsureAuthenticatedAsync();
        _admin.DefaultRequestHeaders.AddAuthHeader(accessToken);
        _workDir = Directory.CreateTempSubdirectory("consol-tonemap-refusal-e2e-").FullName;
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
    public async Task ToneMapping_WithoutATonemappingFfmpeg_IsRefusedWithItsReason_NeverTheLegacyPlanner()
    {
        var (client, userId) = await SignInAsync("tonemap-refusal");
        var itemId = await SeedHdrFilmAsync();

        await AssertRefusedAsync(client, itemId, userId, ["SDR"], LocalMediaRefusal.ToneMappingUnavailable);
    }

    // Audit row S2 (decided 2026-11-27): the engine would keep HDR10 in an H.264 encode, which a
    // session does not produce. The client accepts SDR for H.264, so the session is re-planned to
    // SDR - and refused here, where the server cannot tone-map; the client does not accept SDR, so
    // it is refused outright. Neither reaches the legacy planner.
    [Theory]
    [InlineData(new[] { "SDR", "HDR10" }, LocalMediaRefusal.ToneMappingUnavailable)]
    [InlineData(new[] { "HDR10" }, LocalMediaRefusal.HdrOutputNotExecuted)]
    public async Task HdrOutput_TheSessionDoesNotExecute_IsReplannedToSdrOrRefused_NeverTheLegacyPlanner(string[] h264Ranges, LocalMediaRefusal expected)
    {
        var (client, userId) = await SignInAsync("hdr-output-" + h264Ranges.Length);
        var itemId = await SeedHdrFilmAsync();

        await AssertRefusedAsync(client, itemId, userId, h264Ranges, expected);
    }

    private static async Task AssertRefusedAsync(HttpClient client, Guid itemId, Guid userId, string[] h264Ranges, LocalMediaRefusal expected)
    {
        // HEVC is decoded in SDR only, so the HDR10 source is encoded, to H.264.
        var (capabilities, constraints) = EndToEndCapabilityPresets.BrowserRemuxOverHls();
        capabilities = capabilities with
        {
            Decode = capabilities.Decode with
            {
                VideoCodecs =
                [
                    new VideoCodecCapability("h264", [], null, null, h264Ranges, null, null),
                    new VideoCodecCapability("hevc", [], null, null, ["SDR"], null, null),
                ],
            },
        };
        var request = new CreatePlaybackSessionRequest(itemId, userId, capabilities, constraints, PlaySessionId: "refusal-" + Guid.NewGuid().ToString("N"));
        using var response = await client.PostAsJsonAsync("Playback/Sessions", request, JsonDefaults.Options, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // A legacy planner reached would answer 500 ("The legacy planner (StreamBuilder) was reached.").
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, FormattableString.Invariant($"POST Playback/Sessions: {(int)response.StatusCode} {body}"));
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(expected.ToString(), problem.RootElement.GetProperty("reason").GetString());
        Assert.Contains("HDR", problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
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
            $"Consolidation HDR10 refusal {Guid.NewGuid()}",
            runTimeTicks: TimeSpan.FromSeconds(FixtureSeconds).Ticks);
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

#pragma warning disable CA1034 // Nested types should not be visible: the xUnit class fixture is part of this test.
    /// <summary>
    /// The legacy-free host, with a planner that declares no tone mapping: the legacy planner throws
    /// if reached.
    /// </summary>
    public sealed class NoToneMappingFactory : DirectPlayFilmEndToEndTests.LegacyFreeFactory
    {
        /// <inheritdoc/>
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IPlaybackSessionPlanner>(provider => new LocalMediaPlanner(
                    provider.GetRequiredService<IPlaybackEngine>(),
                    provider.GetRequiredService<IV2PlanStore>(),
                    provider.GetRequiredService<ILogger<LocalMediaPlanner>>(),
                    canTonemap: () => false,
                    canReadDiscImage: isoType => provider.GetRequiredService<IMediaEncoder>().SupportsDiscImage(isoType))));
        }
    }
#pragma warning restore CA1034

    private sealed class LegacyChainReached : IPlaybackSessionPlanner
    {
        public PlaybackPlan? PlanAudio(MediaOptions options) => throw new InvalidOperationException("The legacy planner (StreamBuilder) was reached.");

        public PlaybackPlan? PlanVideo(MediaOptions options) => throw new InvalidOperationException("The legacy planner (StreamBuilder) was reached.");
    }
}
