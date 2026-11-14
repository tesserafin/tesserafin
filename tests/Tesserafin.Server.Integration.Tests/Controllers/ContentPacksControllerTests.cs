using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Tesserafin.Api.Models.ContentPackDtos;
using Tesserafin.Api.Models.UserDtos;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.ContentPacks;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Users;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.Controllers;

/// <summary>
/// End-to-end behaviour of the content pack endpoints over real HTTP: who may read, who may write,
/// and what the server answers when a caller asks about something it must not confirm exists.
/// </summary>
public sealed class ContentPacksControllerTests : IClassFixture<TesserafinApplicationFactory>
{
    private readonly TesserafinApplicationFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;

    private static string? _accessToken;

    public ContentPacksControllerTests(TesserafinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ContentPacks_WithoutAuthentication_AreNotReachable()
    {
        var client = _factory.CreateClient();

        using var response = await client.GetAsync("ContentPacks", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ManagementWithoutThePermissionIs403WhileReadingStaysAllowed()
    {
        // An ordinary user, not the administrator: the repository's existing semantics let an
        // administrator through every permission policy, so only a non-admin can show the gate.
        var client = await CreateOrdinaryUserClientAsync("packless");

        // Reading needs an authenticated user and nothing more.
        using (var list = await client.GetAsync("ContentPacks", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        }

        using (var create = await client.PostAsJsonAsync(
            "ContentPacks",
            new CreateContentPackRequest { Name = "Refused" },
            _jsonOptions,
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        }

        using (var reorder = await client.PostAsJsonAsync(
            "ContentPacks/Order",
            new ReorderContentPacksRequest { PackIds = [] },
            _jsonOptions,
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, reorder.StatusCode);
        }

        using (var delete = await client.DeleteAsync(
            $"ContentPacks/{Guid.NewGuid():N}",
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        }

        using (var add = await client.PostAsync(
            $"ContentPacks/{Guid.NewGuid():N}/Items/{Guid.NewGuid():N}",
            content: null,
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, add.StatusCode);
        }
    }

    [Fact]
    public async Task FullLifecycleUnderTheManagementPermission()
    {
        var client = await CreateAuthenticatedClientAsync();
        await SetContentPackManagementAsync(client, enabled: true);

        var first = await CreatePackAsync(client, "Sport", "Everything that runs");
        var second = await CreatePackAsync(client, "Concerts", null);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(0, first.SortOrder);
        Assert.Equal(1, second.SortOrder);

        // Both packs are genuinely empty, so both are listed with a zero count.
        Assert.Equal(0, first.VisibleItemCount);
        Assert.Null(first.RepresentativeItemId);

        // A duplicate name loses to the unique constraint, not to a 500.
        using (var duplicate = await client.PostAsJsonAsync(
            "ContentPacks",
            new CreateContentPackRequest { Name = "  sport " },
            _jsonOptions,
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        }

        using (var invalid = await client.PostAsJsonAsync(
            "ContentPacks",
            new CreateContentPackRequest { Name = "   " },
            _jsonOptions,
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        // Renaming keeps the identifier, and the same body twice is the same result.
        var renamed = await UpdatePackAsync(client, first.Id, "Sports", "Everything that runs");
        var renamedAgain = await UpdatePackAsync(client, first.Id, "Sports", "Everything that runs");
        Assert.Equal(first.Id, renamed.Id);
        Assert.Equal(first.Id, renamedAgain.Id);
        Assert.Equal("Sports", renamedAgain.Name);

        using (var missing = await client.PostAsJsonAsync(
            $"ContentPacks/{Guid.NewGuid():N}",
            new UpdateContentPackRequest { Name = "Nowhere" },
            _jsonOptions,
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        // Reorder must be the whole list, exactly once each.
        using (var partial = await client.PostAsJsonAsync(
            "ContentPacks/Order",
            new ReorderContentPacksRequest { PackIds = [first.Id] },
            _jsonOptions,
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.BadRequest, partial.StatusCode);
        }

        using (var unknown = await client.PostAsJsonAsync(
            "ContentPacks/Order",
            new ReorderContentPacksRequest { PackIds = [first.Id, second.Id, Guid.NewGuid()] },
            _jsonOptions,
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        using (var reorder = await client.PostAsJsonAsync(
            "ContentPacks/Order",
            new ReorderContentPacksRequest { PackIds = [second.Id, first.Id] },
            _jsonOptions,
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, reorder.StatusCode);
        }

        var listed = await ListPacksAsync(client);
        Assert.Equal([second.Id, first.Id], listed.ConvertAll(p => p.Id));
        Assert.Equal([0, 1], listed.ConvertAll(p => p.SortOrder));

        // An item nobody can see — because it does not exist — is indistinguishable from one the
        // caller may not see.
        using (var addUnknownItem = await client.PostAsync(
            $"ContentPacks/{first.Id:N}/Items/{Guid.NewGuid():N}",
            content: null,
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, addUnknownItem.StatusCode);
        }

        // Removing an absent membership from a real pack succeeds.
        using (var removeAbsent = await client.DeleteAsync(
            $"ContentPacks/{first.Id:N}/Items/{Guid.NewGuid():N}",
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, removeAbsent.StatusCode);
        }

        // An existing but empty pack answers with an empty page, not a 404.
        using (var items = await client.GetAsync(
            $"ContentPacks/{first.Id:N}/Items",
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, items.StatusCode);
        }

        using (var itemsOfMissingPack = await client.GetAsync(
            $"ContentPacks/{Guid.NewGuid():N}/Items",
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, itemsOfMissingPack.StatusCode);
        }

        using (var packsOfUnknownItem = await client.GetAsync(
            $"Items/{Guid.NewGuid():N}/ContentPacks",
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, packsOfUnknownItem.StatusCode);
        }

        // Deleting is effect-idempotent: the second attempt reports there is nothing left.
        foreach (var id in new[] { first.Id, second.Id })
        {
            using var deleted = await client.DeleteAsync($"ContentPacks/{id:N}", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

            using var again = await client.DeleteAsync($"ContentPacks/{id:N}", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        }

        Assert.Empty(await ListPacksAsync(client));
    }

    [Fact]
    public async Task BrowsingPreferenceRoundTripsThroughTheUserConfiguration()
    {
        var client = await CreateAuthenticatedClientAsync();
        var user = await AuthHelper.GetUserDtoAsync(client);

        Assert.NotNull(user.Configuration);
        var original = user.Configuration.ContentPackBrowsingPreference;

        user.Configuration.ContentPackBrowsingPreference = global::Tesserafin.Data.Enums.ContentPackBrowsingPreference.ContentPackFirst;

        using (var update = await client.PostAsJsonAsync(
            $"Users/{user.Id:N}/Configuration",
            user.Configuration,
            _jsonOptions,
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        }

        var reread = await AuthHelper.GetUserDtoAsync(client);
        Assert.Equal(
            global::Tesserafin.Data.Enums.ContentPackBrowsingPreference.ContentPackFirst,
            reread.Configuration!.ContentPackBrowsingPreference);

        // Put it back so the shared fixture is left as it was found.
        reread.Configuration.ContentPackBrowsingPreference = original;
        using var restore = await client.PostAsJsonAsync(
            $"Users/{user.Id:N}/Configuration",
            reread.Configuration,
            _jsonOptions,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode);
    }

    private async Task<HttpClient> CreateOrdinaryUserClientAsync(string username)
    {
        var admin = await CreateAuthenticatedClientAsync();

        using (var created = await admin.PostAsJsonAsync(
            "Users/New",
            new CreateUserByName { Name = username, Password = "pack-test-password" },
            _jsonOptions,
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        }

        var client = _factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, AuthHelper.DummyAuthHeader);
        request.Content = JsonContent.Create(
            new AuthenticateUserByName { Username = username, Pw = "pack-test-password" },
            options: _jsonOptions);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions, TestContext.Current.CancellationToken);
        var token = payload.GetProperty("AccessToken").GetString();
        Assert.NotNull(token);

        client.DefaultRequestHeaders.AddAuthHeader(token);
        return client;
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));
        return client;
    }

    private async Task SetContentPackManagementAsync(HttpClient client, bool enabled)
    {
        var user = await AuthHelper.GetUserDtoAsync(client);
        Assert.NotNull(user.Policy);

        user.Policy.EnableContentPackManagement = enabled;

        using var response = await client.PostAsJsonAsync(
            $"Users/{user.Id.ToString("N", CultureInfo.InvariantCulture)}/Policy",
            user.Policy,
            _jsonOptions,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<ContentPackDto> CreatePackAsync(HttpClient client, string name, string? description)
    {
        using var response = await client.PostAsJsonAsync(
            "ContentPacks",
            new CreateContentPackRequest { Name = name, Description = description },
            _jsonOptions,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ContentPackDto>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(dto);
        return dto;
    }

    private async Task<ContentPackDto> UpdatePackAsync(HttpClient client, Guid packId, string name, string? description)
    {
        using var response = await client.PostAsJsonAsync(
            $"ContentPacks/{packId.ToString("N", CultureInfo.InvariantCulture)}",
            new UpdateContentPackRequest { Name = name, Description = description },
            _jsonOptions,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ContentPackDto>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(dto);
        return dto;
    }

    private async Task<List<ContentPackDto>> ListPacksAsync(HttpClient client)
    {
        using var response = await client.GetAsync("ContentPacks", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var packs = await response.Content.ReadFromJsonAsync<List<ContentPackDto>>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(packs);
        return packs;
    }
}
