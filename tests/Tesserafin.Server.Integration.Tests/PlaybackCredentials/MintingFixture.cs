using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http.Json;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using System;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Api.Models.PlaybackCredentialDtos;
using Tesserafin.Api.Models.StartupDtos;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Controller.Persistence;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Server.Integration.Tests.EndToEnd;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.PlaybackCredentials;

/// <summary>
/// A server, an owner, a restricted second user, and three items — one of which the second user is
/// not allowed to see. Everything the minting checks in #153-A0-R1 item 3 need.
/// </summary>
public sealed class MintingFixture : IAsyncLifetime
{
    /// <summary>The tag the restricted user is blocked from.</summary>
    public const string BlockedTag = "r1-blocked";

    private string _workDirectory = string.Empty;
    private string _ownerName = string.Empty;
    private string _ownerPassword = string.Empty;

    /// <summary>Gets the booted server.</summary>
    public MintingApplicationFactory Factory { get; private set; } = null!;

    /// <summary>Gets the owner's durable token.</summary>
    public string OwnerToken { get; private set; } = string.Empty;

    /// <summary>Gets the owner's device id.</summary>
    public string OwnerDeviceId => "r1-mint-owner";

    /// <summary>Gets the restricted user's durable token.</summary>
    public string RestrictedToken { get; private set; } = string.Empty;

    /// <summary>Gets the restricted user's device id.</summary>
    public string RestrictedDeviceId => "r1-mint-restricted";

    /// <summary>Gets the item both users may see.</summary>
    public Guid VisibleItemId { get; private set; }

    /// <summary>Gets a second visible item, used to prove a media source belongs to its item.</summary>
    public Guid OtherItemId { get; private set; }

    /// <summary>Gets the item the restricted user is blocked from.</summary>
    public Guid BlockedItemId { get; private set; }

    /// <summary>Gets the visible item's media source id.</summary>
    public string VisibleMediaSourceId => VisibleItemId.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Gets the other item's media source id.</summary>
    public string OtherMediaSourceId => OtherItemId.ToString("N", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        Factory = new MintingApplicationFactory();

        using (var setup = Factory.CreateClient())
        {
            var startupUser = await setup
                .GetFromJsonAsync<StartupUserDto>("/Startup/User", JsonDefaults.Options)
                .ConfigureAwait(false);

            _ownerName = startupUser!.Name ?? string.Empty;
            _ownerPassword = startupUser.Password ?? string.Empty;

            using var complete = await setup.PostAsync("/Startup/Complete", new ByteArrayContent([])).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);
        }

        OwnerToken = await AuthenticateAsync(OwnerDeviceId, _ownerName, _ownerPassword).ConfigureAwait(false);

        _workDirectory = Path.Combine(Path.GetTempPath(), "r1-minting-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(_workDirectory);

        var libraryManager = Factory.Services.GetRequiredService<ILibraryManager>();
        var mediaStreamRepository = Factory.Services.GetRequiredService<IMediaStreamRepository>();

        VisibleItemId = Seed(Factory.Services.GetRequiredService<IItemWriteCommands>(), mediaStreamRepository, "visible.mp4", "R1 mint visible", tags: []);
        OtherItemId = Seed(Factory.Services.GetRequiredService<IItemWriteCommands>(), mediaStreamRepository, "other.mp4", "R1 mint other", tags: []);
        BlockedItemId = Seed(Factory.Services.GetRequiredService<IItemWriteCommands>(), mediaStreamRepository, "blocked.mp4", "R1 mint blocked", tags: [BlockedTag]);

        await CreateRestrictedUserAsync().ConfigureAwait(false);
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
            // Nothing worth failing a run over.
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Creates a client presenting one device's durable token.
    /// </summary>
    /// <param name="deviceId">The device id.</param>
    /// <param name="token">That device's token.</param>
    /// <returns>An authenticated client.</returns>
    public HttpClient ClientFor(string deviceId, string token)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            AuthHelper.AuthHeaderName,
            MediaBoundaryFixture.AuthorizationHeader(deviceId, token));
        return client;
    }

    /// <summary>
    /// Posts one mint request and returns the raw response.
    /// </summary>
    /// <param name="client">The client to mint through.</param>
    /// <param name="body">The request body, serialized as-is so malformed shapes can be sent.</param>
    /// <returns>The status and body.</returns>
    public static async Task<(HttpStatusCode Status, string Body)> MintRawAsync(HttpClient client, object body)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var content = JsonContent.Create(body, options: JsonDefaults.Options);
        using var response = await client.PostAsync("/Playback/Capabilities", content, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        return (response.StatusCode, text);
    }

    /// <summary>
    /// Posts one well-formed mint request.
    /// </summary>
    /// <param name="client">The client to mint through.</param>
    /// <param name="scopes">The scopes.</param>
    /// <param name="itemId">The item, or null.</param>
    /// <param name="mediaSourceId">The media source, or null.</param>
    /// <param name="playSessionId">The play session.</param>
    /// <returns>The status and body.</returns>
    public static Task<(HttpStatusCode Status, string Body)> MintAsync(
        HttpClient client,
        IReadOnlyList<PlaybackCapabilityScope> scopes,
        Guid? itemId,
        string? mediaSourceId,
        string playSessionId = "r1-mint-play-session")
        => MintRawAsync(
            client,
            new PlaybackCapabilityRequestDto
            {
                PlaySessionId = playSessionId,
                ItemId = itemId,
                MediaSourceId = mediaSourceId,
                Scopes = scopes
            });

    private async Task<string> AuthenticateAsync(string deviceId, string userName, string password)
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, MediaBoundaryFixture.AuthorizationHeader(deviceId, null));
        request.Content = JsonContent.Create(new { Username = userName, Pw = password }, options: JsonDefaults.Options);

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return document.RootElement.GetProperty("AccessToken").GetString()!;
    }

    private async Task CreateRestrictedUserAsync()
    {
        using var owner = ClientFor(OwnerDeviceId, OwnerToken);

        UserDto created;
        using (var response = await owner.PostAsJsonAsync(
                   "/Users/New",
                   new { Name = "r1-restricted", Password = "r1-restricted-pw" },
                   JsonDefaults.Options).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            created = (await response.Content.ReadFromJsonAsync<UserDto>(JsonDefaults.Options).ConfigureAwait(false))!;
        }

        // Read the policy back and change exactly one field. Posting a hand-built policy would
        // silently reset every other permission to its default, and the test would then be
        // proving something about a user it accidentally rewrote.
        var policy = created.Policy!;
        policy.BlockedTags = [BlockedTag];

        using (var applied = await owner.PostAsJsonAsync(
                   FormattableString.Invariant($"/Users/{created.Id}/Policy"),
                   policy,
                   JsonDefaults.Options).ConfigureAwait(false))
        {
            applied.EnsureSuccessStatusCode();
        }

        RestrictedToken = await AuthenticateAsync(RestrictedDeviceId, "r1-restricted", "r1-restricted-pw").ConfigureAwait(false);
    }

    private Guid Seed(
        IItemWriteCommands writes,
        IMediaStreamRepository mediaStreamRepository,
        string fileName,
        string name,
        IReadOnlyList<string> tags)
    {
        var mediaPath = Path.Combine(_workDirectory, fileName);
        File.WriteAllBytes(mediaPath, new byte[1024]);

        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            Name = name,
            Path = mediaPath,
            Container = "mp4",
            Size = new FileInfo(mediaPath).Length,
            RunTimeTicks = EndToEndMediaFixtures.DurationTicks,
            VideoType = VideoType.VideoFile,
            IsInMixedFolder = true,
            DateCreated = DateTime.UtcNow,
            DateModified = DateTime.UtcNow,
            Tags = [.. tags]
        };

        writes.CreateItems([movie], null, CancellationToken.None);
        mediaStreamRepository.SaveMediaStreams(
            movie.Id,
            [
                new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264", Width = 320, Height = 240, IsDefault = true },
                new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "aac", Channels = 2, IsDefault = true },
            ],
            CancellationToken.None);

        return movie.Id;
    }
}
