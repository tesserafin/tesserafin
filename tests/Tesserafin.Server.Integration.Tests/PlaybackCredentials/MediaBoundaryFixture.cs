using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http.Json;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using System;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Api.Models.PlaybackCredentialDtos;
using Tesserafin.Api.Models.StartupDtos;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Controller.Persistence;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Entities;
using Tesserafin.Server.Integration.Tests.EndToEnd;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.PlaybackCredentials;

/// <summary>
/// One booted server, one durable token, one real library item, shared by the whole media
/// authorization matrix (#153-A0-R1).
/// </summary>
/// <remarks>
/// WHY A REAL ITEM. Every negative in this suite is a refusal that has to happen BEFORE the action
/// runs, and a refusal is only evidence if the same request would otherwise have succeeded. Against
/// a non-existent item every route answers 404 whether authorization held or not, so the whole
/// matrix would pass against a server with no authorization at all. The fixture exists so that a
/// 401 means something.
///
/// WHY THE FILE IS 4096 SYNTHETIC BYTES AND NOT A REAL ENCODE. The routes this fixture proves with
/// bytes are all static delivery: the server answers them with the source file verbatim, without
/// decoding it. A deterministic payload makes "these are the fixture's bytes" an exact assertion
/// rather than a length comparison, and keeps the suite free of ffmpeg. The routes that genuinely
/// need an encoder are marked <see cref="MediaRouteEvidence.Entry"/> in
/// <see cref="MediaRouteCatalog"/> and claim only what a request can honestly show there.
/// </remarks>
public sealed class MediaBoundaryFixture : IAsyncLifetime
{
    /// <summary>The index of the fixture's external subtitle stream.</summary>
    public const int SubtitleStreamIndex = 2;

    /// <summary>The device id the fixture's own session uses.</summary>
    public const string PrimaryDeviceId = "r1-primary-device";

    private static readonly byte[] _mediaBytes = CreateDeterministicPayload();

    private string _workDirectory = string.Empty;
    private string _userName = string.Empty;
    private string _password = string.Empty;

    /// <summary>Gets the booted server.</summary>
    public MediaBoundaryApplicationFactory Factory { get; private set; } = null!;

    /// <summary>Gets the durable session token of the fixture's user.</summary>
    public string DurableToken { get; private set; } = string.Empty;

    /// <summary>Gets the seeded item.</summary>
    public Guid ItemId { get; private set; }

    /// <summary>Gets a second seeded item, used to prove item binding is compared.</summary>
    public Guid OtherItemId { get; private set; }

    /// <summary>Gets the seeded item's media source id.</summary>
    public string MediaSourceId => ItemId.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Gets the other item's media source id.</summary>
    public string OtherMediaSourceId => OtherItemId.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Gets every media route, bound to this fixture.</summary>
    public IReadOnlyList<MediaRoute> Routes { get; private set; } = [];

    /// <summary>
    /// Gets a copy of the exact bytes on disk behind <see cref="ItemId"/>. A method, not a
    /// property: CA1819 is an error here, and handing every caller the same mutable array would
    /// let one test corrupt the expectation every other test compares against.
    /// </summary>
    /// <returns>The fixture's media payload.</returns>
    public byte[] GetMediaBytes() => (byte[])_mediaBytes.Clone();

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        Factory = new MediaBoundaryApplicationFactory();

        using (var setup = Factory.CreateClient())
        {
            var startupUser = await setup
                .GetFromJsonAsync<StartupUserDto>("/Startup/User", JsonDefaults.Options)
                .ConfigureAwait(false);

            _userName = startupUser!.Name ?? string.Empty;
            _password = startupUser.Password ?? string.Empty;

            using var complete = await setup.PostAsync("/Startup/Complete", new ByteArrayContent([])).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);
        }

        DurableToken = await AuthenticateAsync(PrimaryDeviceId).ConfigureAwait(false);

        _workDirectory = Path.Combine(Path.GetTempPath(), "r1-media-boundary-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(_workDirectory);

        var libraryManager = Factory.Services.GetRequiredService<ILibraryManager>();
        var libraryManagerWrites = Factory.Services.GetRequiredService<IItemWriteCommands>();
        var mediaStreamRepository = Factory.Services.GetRequiredService<IMediaStreamRepository>();

        ItemId = SeedItem(Factory.Services.GetRequiredService<IItemWriteCommands>(), mediaStreamRepository, "fixture.mp4", "R1 media boundary fixture");
        OtherItemId = SeedItem(Factory.Services.GetRequiredService<IItemWriteCommands>(), mediaStreamRepository, "other.mp4", "R1 media boundary second fixture");

        Routes = MediaRouteCatalog.For(ItemId, MediaSourceId, SubtitleStreamIndex);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Factory?.Dispose();

        try
        {
            if (Directory.Exists(_workDirectory))
            {
                Directory.Delete(_workDirectory, true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Creates a client that presents nothing at all.
    /// </summary>
    /// <returns>An anonymous client.</returns>
    public HttpClient AnonymousClient() => Factory.CreateClient();

    /// <summary>
    /// Creates a client that presents the durable token the legacy way, in the header.
    /// </summary>
    /// <returns>An authenticated client.</returns>
    public HttpClient DurableHeaderClient() => ClientFor(PrimaryDeviceId, DurableToken);

    /// <summary>
    /// Builds the <c>Authorization</c> header value for one device.
    /// </summary>
    /// <param name="deviceId">The device id to claim.</param>
    /// <param name="token">The durable token to present, or null for an unauthenticated header.</param>
    /// <returns>The header value.</returns>
    public static string AuthorizationHeader(string deviceId, string? token)
    {
        var header = $"MediaBrowser Client=\"R1 Media Boundary\", DeviceId=\"{deviceId}\", Device=\"xunit\", Version=\"10.8.0\"";
        return token is null ? header : header + $", Token=\"{token}\"";
    }

    /// <summary>
    /// Authenticates the fixture's user on one device, producing an independent session whose
    /// revocation does not disturb the rest of the suite.
    /// </summary>
    /// <param name="deviceId">The device id to authenticate as.</param>
    /// <returns>That session's durable token.</returns>
    public async Task<string> AuthenticateAsync(string deviceId)
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, AuthorizationHeader(deviceId, null));
        request.Content = JsonContent.Create(
            new { Username = _userName, Pw = _password },
            options: JsonDefaults.Options);

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return document.RootElement.GetProperty("AccessToken").GetString()!;
    }

    /// <summary>
    /// Creates a client presenting one device's durable token in the header.
    /// </summary>
    /// <param name="deviceId">The device id.</param>
    /// <param name="token">That device's token.</param>
    /// <returns>An authenticated client.</returns>
    public HttpClient ClientFor(string deviceId, string token)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(AuthHelper.AuthHeaderName, AuthorizationHeader(deviceId, token));
        return client;
    }

    /// <summary>
    /// Mints a capability over real HTTP, with the durable token in a header.
    /// </summary>
    /// <param name="scopes">The scopes to request.</param>
    /// <param name="itemId">The item to bind to, or null.</param>
    /// <param name="mediaSourceId">The media source to bind to, or null.</param>
    /// <param name="playSessionId">The play session to bind to.</param>
    /// <returns>The minted capability.</returns>
    public async Task<PlaybackCapabilityDto> MintAsync(
        IReadOnlyList<PlaybackCapabilityScope> scopes,
        Guid? itemId,
        string? mediaSourceId,
        string playSessionId = "r1-play-session")
    {
        using var client = DurableHeaderClient();
        return await MintWithAsync(client, scopes, itemId, mediaSourceId, playSessionId).ConfigureAwait(false);
    }

    /// <summary>
    /// Mints a capability through a caller-supplied authenticated client, which the caller may then
    /// log out to prove revocation without disturbing the fixture's own session.
    /// </summary>
    /// <param name="client">The authenticated client. The caller keeps ownership of it.</param>
    /// <param name="scopes">The scopes to request.</param>
    /// <param name="itemId">The item to bind to, or null.</param>
    /// <param name="mediaSourceId">The media source to bind to, or null.</param>
    /// <param name="playSessionId">The play session to bind to.</param>
    /// <returns>The minted capability.</returns>
    public static async Task<PlaybackCapabilityDto> MintWithAsync(
        HttpClient client,
        IReadOnlyList<PlaybackCapabilityScope> scopes,
        Guid? itemId,
        string? mediaSourceId,
        string playSessionId = "r1-play-session")
    {
        ArgumentNullException.ThrowIfNull(client);

        using var response = await client.PostAsJsonAsync(
            "/Playback/Capabilities",
            new PlaybackCapabilityRequestDto
            {
                PlaySessionId = playSessionId,
                ItemId = itemId,
                MediaSourceId = mediaSourceId,
                Scopes = scopes
            },
            JsonDefaults.Options,
            TestContext.Current.CancellationToken).ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content
            .ReadFromJsonAsync<PlaybackCapabilityDto>(JsonDefaults.Options, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);

        Assert.NotNull(dto);
        Assert.NotEmpty(dto!.Value);
        return dto;
    }

    /// <summary>
    /// Mints exactly the capability a route is supposed to accept: its own scope, and the item and
    /// media source THAT ROUTE names — not the fixture's, the route's.
    /// </summary>
    /// <remarks>
    /// Binding to more than the route names is not harmless. Once binding is compared exactly, a
    /// capability carrying a media source is refused on the three legacy routes that name none, and
    /// a test that always minted a source-bound capability would go on passing its revocation and
    /// wrong-item cases there — refused by the media-source rule, for a reason unrelated to the
    /// property each of those tests claims to prove.
    /// </remarks>
    /// <param name="route">The route to mint for.</param>
    /// <returns>The minted capability.</returns>
    public Task<PlaybackCapabilityDto> MintForAsync(MediaRoute route) => MintForAsync(route, DurableHeaderClient);

    /// <summary>
    /// Mints for a route through a caller-chosen session.
    /// </summary>
    /// <param name="route">The route to mint for.</param>
    /// <param name="clientFactory">Produces the authenticated client to mint through.</param>
    /// <returns>The minted capability.</returns>
    public async Task<PlaybackCapabilityDto> MintForAsync(MediaRoute route, Func<HttpClient> clientFactory)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(clientFactory);

        using var client = clientFactory();
        return await MintWithAsync(
            client,
            [route.Scope],
            route.ItemBound ? ItemId : null,
            route.MediaSourceBound ? MediaSourceId : null).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one request and reads the whole body.
    /// </summary>
    /// <param name="client">The client to send with.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">The path.</param>
    /// <returns>The status code and the body.</returns>
    public static async Task<(HttpStatusCode Status, byte[] Body)> SendAsync(HttpClient client, string method, string path)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        return (response.StatusCode, body);
    }

    private static byte[] CreateDeterministicPayload()
    {
        var payload = new byte[4096];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        return payload;
    }

    private Guid SeedItem(IItemWriteCommands libraryManagerWrites, IMediaStreamRepository mediaStreamRepository, string fileName, string name)
    {
        var mediaPath = Path.Combine(_workDirectory, fileName);
        File.WriteAllBytes(mediaPath, _mediaBytes);
        var subtitlePath = EndToEndMediaFixtures.CreateExternalSrt(_workDirectory, Path.GetFileNameWithoutExtension(fileName) + ".srt");

        return LibraryItemSeeder.SeedVideo(
            libraryManagerWrites,
            mediaStreamRepository,
            mediaPath,
            "mp4",
            [
                new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264", Width = 320, Height = 240, IsDefault = true },
                new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "aac", Channels = 2, IsDefault = true },
                new MediaStream { Index = SubtitleStreamIndex, Type = MediaStreamType.Subtitle, Codec = "srt", IsExternal = true, Path = subtitlePath, Language = "eng" },
            ],
            name);
    }
}
