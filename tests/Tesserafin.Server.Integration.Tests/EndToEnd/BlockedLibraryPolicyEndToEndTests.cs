using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Controller.Persistence;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Server.Integration.Tests.PlaybackCredentials;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// <c>BlockedMediaFolders</c>, over HTTP: a library blocked through <c>POST /Users/{id}/Policy</c> is
/// stored, read back, and applies to the next decision in the process that saved it and after a
/// restart.
/// </summary>
/// <remarks>
/// Two libraries, one holding the other's directory too, so an item can belong to both: Films
/// (films/) and Vault (vault/ and films/). bea's policy changes; cleo's never does.
///
/// The existing semantics are kept: a non-empty block list hides exactly the blocked libraries
/// and takes precedence over <c>EnableAllFolders</c>/<c>EnabledFolders</c>; an empty one defers
/// to them; an item of a blocked library stays visible through another library that holds it;
/// a null list, which the web library-access page sends, clears it like an empty one.
/// </remarks>
public sealed class BlockedLibraryPolicyEndToEndTests : IDisposable
{
    private const string Password = "blocked-password";

    // BlockedChannels takes the same path; no channel is installed, so only storage and read-back
    // are asserted here. Its access decision is covered by ChannelCatalogTests.
    private static readonly Guid BlockedChannel = Guid.Parse("6d1c58f4-4bd5-4c39-9a37-31a8d2c6a0e1");

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "reefin-e2e-test-data", "blk-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _mediaRoot = Directory.CreateTempSubdirectory("tesserafin-blk-media-").FullName;

    // Every wrong decision, not only the first, so a failure shows which states, routes and items went wrong.
    private readonly List<string> _wrong = [];

    public void Dispose()
    {
        foreach (var directory in new[] { _dataRoot, _mediaRoot })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    /// <summary>
    /// Blocking Vault refuses bea the Vault-only item, the Vault library and its listing on every
    /// covered route, including a capability minted before the change, while the item Films also
    /// holds stays hers; the stored list is what the policy reads back; the web library-access save
    /// (block list null) clears it; it wins over an enabled-folders list naming Vault; clearing it
    /// with an empty list gives everything
    /// back; a restart decides the same as the process that saved it.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 300_000)]
    public async Task BlockedLibrary_IsStored_ReadBack_AndAppliesToTheNextDecision()
    {
        Seeded seeded;
        using (var first = new Boot(_dataRoot))
        {
            seeded = await SeedAsync(first, TestContext.Current.CancellationToken);
        }

        using (var server = new Boot(_dataRoot))
        {
            using var admin = server.AdminClient(seeded.AdminToken);
            using var bea = server.ClientFor("blk-bea", seeded.BeaToken);
            using var cleo = server.ClientFor("blk-cleo", seeded.CleoToken);
            using var anonymous = server.CreateClient();

            var capabilities = new Dictionary<(string User, Guid Item), string>();
            foreach (var (name, client, userId) in new[] { ("bea", bea, seeded.BeaId), ("cleo", cleo, seeded.CleoId) })
            {
                foreach (var item in new[] { seeded.InBoth, seeded.VaultOnly })
                {
                    capabilities[(name, item)] = await MintAsync(client, userId, item, TestContext.Current.CancellationToken);
                }
            }

            async Task AssertAsync(string state, bool vaultAllowed)
            {
                await AssertDecisionsAsync(state + ", bea", bea, anonymous, seeded.BeaId, seeded, capabilities, "bea", vaultAllowed);
                await AssertDecisionsAsync(state + ", cleo", cleo, anonymous, seeded.CleoId, seeded, capabilities, "cleo", vaultAllowed: true);
            }

            await AssertAsync("before any block", vaultAllowed: true);

            await UpdatePolicyAsync(
                admin,
                seeded.BeaId,
                p =>
                {
                    p["BlockedMediaFolders"] = Ids(seeded.Vault);
                    p["BlockedChannels"] = Ids(BlockedChannel);
                },
                TestContext.Current.CancellationToken);
            await AssertBlockedAsync(admin, "after blocking Vault", seeded.BeaId, TestContext.Current.CancellationToken, seeded.Vault);
            await AssertBlockedAsync(admin, "after blocking a channel", seeded.BeaId, "BlockedChannels", TestContext.Current.CancellationToken, BlockedChannel);
            await AssertAsync("after blocking Vault", vaultAllowed: false);

            // What the web library-access page saves (Access.tsx): all libraries, the block list null.
            // The libraries it shows are the whole of the user's access, so null clears the block.
            await UpdatePolicyAsync(
                admin,
                seeded.BeaId,
                p =>
                {
                    p["EnableAllFolders"] = true;
                    p["EnabledFolders"] = Ids();
                    p["BlockedMediaFolders"] = null;
                    p["BlockedChannels"] = null;
                },
                TestContext.Current.CancellationToken);
            await AssertBlockedAsync(admin, "after the library-access page saved", seeded.BeaId, TestContext.Current.CancellationToken);
            await AssertBlockedAsync(admin, "after the library-access page saved", seeded.BeaId, "BlockedChannels", TestContext.Current.CancellationToken);
            await AssertAsync("after the library-access page saved", vaultAllowed: true);

            await UpdatePolicyAsync(admin, seeded.BeaId, p => p["BlockedMediaFolders"] = Ids(seeded.Vault), TestContext.Current.CancellationToken);
            await AssertAsync("after blocking Vault again", vaultAllowed: false);

            // The block list wins over an enabled-folders list that names the blocked library.
            await UpdatePolicyAsync(
                admin,
                seeded.BeaId,
                p =>
                {
                    p["EnableAllFolders"] = false;
                    p["EnabledFolders"] = Ids(seeded.Vault);
                },
                TestContext.Current.CancellationToken);
            await AssertBlockedAsync(admin, "with Vault both blocked and enabled", seeded.BeaId, TestContext.Current.CancellationToken, seeded.Vault);
            await AssertAsync("with Vault both blocked and enabled", vaultAllowed: false);

            await UpdatePolicyAsync(
                admin,
                seeded.BeaId,
                p =>
                {
                    p["EnableAllFolders"] = true;
                    p["EnabledFolders"] = Ids();
                    p["BlockedMediaFolders"] = Ids();
                },
                TestContext.Current.CancellationToken);
            await AssertBlockedAsync(admin, "after clearing the block", seeded.BeaId, TestContext.Current.CancellationToken);
            await AssertAsync("after clearing the block", vaultAllowed: true);

            await UpdatePolicyAsync(
                admin,
                seeded.BeaId,
                p =>
                {
                    p["BlockedMediaFolders"] = Ids(seeded.Vault);
                    p["BlockedChannels"] = Ids(BlockedChannel);
                },
                TestContext.Current.CancellationToken);
            await AssertAsync("after blocking Vault once more", vaultAllowed: false);
        }

        using (var restarted = new Boot(_dataRoot))
        {
            using var admin = restarted.AdminClient(seeded.AdminToken);
            using var bea = restarted.ClientFor("blk-bea", seeded.BeaToken);
            using var cleo = restarted.ClientFor("blk-cleo", seeded.CleoToken);
            using var anonymous = restarted.CreateClient();
            var none = new Dictionary<(string User, Guid Item), string>();

            await AssertBlockedAsync(admin, "after a restart", seeded.BeaId, TestContext.Current.CancellationToken, seeded.Vault);
            await AssertBlockedAsync(admin, "after a restart", seeded.BeaId, "BlockedChannels", TestContext.Current.CancellationToken, BlockedChannel);
            await AssertDecisionsAsync("after a restart, bea", bea, anonymous, seeded.BeaId, seeded, none, "bea", vaultAllowed: false);
            await AssertDecisionsAsync("after a restart, cleo", cleo, anonymous, seeded.CleoId, seeded, none, "cleo", vaultAllowed: true);

            await UpdatePolicyAsync(admin, seeded.BeaId, p => p["BlockedMediaFolders"] = Ids(), TestContext.Current.CancellationToken);
            await AssertDecisionsAsync("after clearing the block a restart loaded, bea", bea, anonymous, seeded.BeaId, seeded, none, "bea", vaultAllowed: true);
        }

        Assert.True(_wrong.Count == 0, string.Join(Environment.NewLine, _wrong));
    }

    private static JsonArray Ids(params Guid[] ids) => new(ids.Select(i => (JsonNode)i.ToString()).ToArray());

    /// <summary>
    /// What the user editor does: reads the user's policy, changes it, posts it back.
    /// </summary>
    private static async Task UpdatePolicyAsync(HttpClient admin, Guid userId, Action<JsonObject> change, CancellationToken cancellationToken)
    {
        var policy = JsonNode.Parse(await admin.GetStringAsync(FormattableString.Invariant($"Users/{userId}"), cancellationToken))!["Policy"]!.AsObject();
        change(policy);
        using var content = new StringContent(policy.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var saved = await admin.PostAsync(FormattableString.Invariant($"Users/{userId}/Policy"), content, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
    }

    private Task AssertBlockedAsync(HttpClient admin, string what, Guid userId, CancellationToken cancellationToken, params Guid[] expected) =>
        AssertBlockedAsync(admin, what, userId, "BlockedMediaFolders", cancellationToken, expected);

    private async Task AssertBlockedAsync(HttpClient admin, string what, Guid userId, string field, CancellationToken cancellationToken, params Guid[] expected)
    {
        var user = (await admin.GetFromJsonAsync<UserDto>(FormattableString.Invariant($"Users/{userId}"), JsonDefaults.Options, cancellationToken))!;
        var actual = (field == "BlockedChannels" ? user.Policy!.BlockedChannels : user.Policy!.BlockedMediaFolders) ?? [];
        if (!actual.Order().SequenceEqual(expected.Order()))
        {
            _wrong.Add(FormattableString.Invariant($"{what}: policy reads back {field} [{string.Join(", ", actual)}], expected [{string.Join(", ", expected)}]"));
        }
    }

    private async Task AssertDecisionsAsync(
        string what,
        HttpClient client,
        HttpClient anonymous,
        Guid userId,
        Seeded seeded,
        Dictionary<(string User, Guid Item), string> capabilities,
        string name,
        bool vaultAllowed)
    {
        foreach (var (item, label, allowed) in new[] { (seeded.InBoth, "in both libraries", true), (seeded.VaultOnly, "in Vault only", vaultAllowed) })
        {
            var expected = allowed ? HttpStatusCode.OK : HttpStatusCode.NotFound;
            var at = what + ", item " + label;
            Expect(at + ": by id", expected, await StatusAsync(client, HttpMethod.Get, FormattableString.Invariant($"Items/{item}?userId={userId}"), cancellationToken: TestContext.Current.CancellationToken));
            Expect(at + ": bytes with the user's token", expected, await StatusAsync(client, HttpMethod.Get, Bytes(item), cancellationToken: TestContext.Current.CancellationToken));
            if (capabilities.TryGetValue((name, item), out var capability))
            {
                Expect(at + ": bytes with a capability minted before the change", expected, await StatusAsync(anonymous, HttpMethod.Get, Bytes(item) + "&playbackCapability=" + Uri.EscapeDataString(capability), cancellationToken: TestContext.Current.CancellationToken));
            }
        }

        Expect(what + ": the Vault library by id", vaultAllowed ? HttpStatusCode.OK : HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Get, FormattableString.Invariant($"Items/{seeded.Vault}?userId={userId}"), cancellationToken: TestContext.Current.CancellationToken));
        Expect(what + ": the Films library by id", HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Get, FormattableString.Invariant($"Items/{seeded.Films}?userId={userId}"), cancellationToken: TestContext.Current.CancellationToken));

        var views = await IdsAsync(client, FormattableString.Invariant($"UserViews?userId={userId}"), TestContext.Current.CancellationToken);
        if (views.Contains(seeded.Vault) != vaultAllowed || !views.Contains(seeded.Films))
        {
            _wrong.Add(FormattableString.Invariant($"{what}: views [{string.Join(", ", views)}]"));
        }

        var listed = await IdsAsync(client, FormattableString.Invariant($"Items?recursive=true&includeItemTypes=Movie&userId={userId}"), TestContext.Current.CancellationToken);
        if (!listed.Contains(seeded.InBoth) || listed.Contains(seeded.VaultOnly) != vaultAllowed)
        {
            _wrong.Add(what + ": listing");
        }
    }

    private static async Task<HashSet<Guid>> IdsAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync(url, cancellationToken));
        return document.RootElement.GetProperty("Items").EnumerateArray().Select(i => Guid.Parse(i.GetProperty("Id").GetString()!)).ToHashSet();
    }

    private void Expect(string what, HttpStatusCode expected, HttpStatusCode actual)
    {
        if (actual != expected)
        {
            _wrong.Add(FormattableString.Invariant($"{what}: {(int)actual}, expected {(int)expected}"));
        }
    }

    private static string Bytes(Guid item) => FormattableString.Invariant($"Videos/{item}/stream?static=true&mediaSourceId={item:N}");

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, HttpMethod method, string url, object? body = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonDefaults.Options);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return response.StatusCode;
    }

    private static async Task<string> MintAsync(HttpClient client, Guid userId, Guid item, CancellationToken cancellationToken)
    {
        // A play session is the client's own id: the capability is bound to it, nothing has to have opened it.
        var playSession = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var (status, body) = await MintingFixture.MintAsync(client, [PlaybackCapabilityScope.Media], item, item.ToString("N"), playSession);
        Assert.Equal(HttpStatusCode.OK, status);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("Value").GetString()!;
    }

    private async Task<Seeded> SeedAsync(Boot server, CancellationToken cancellationToken)
    {
        var (adminToken, adminId) = await server.EnsureAuthenticatedAsync();
        using var admin = server.AdminClient(adminToken);

        var films = Directory.CreateDirectory(Path.Combine(_mediaRoot, "films")).FullName;
        var vault = Directory.CreateDirectory(Path.Combine(_mediaRoot, "vault")).FullName;

        // Both libraries before either folder: adding a library validates the root's children,
        // and a folder no scan resolved would not survive it.
        await AddLibraryAsync(admin, "blk-films", cancellationToken, films);
        await AddLibraryAsync(admin, "blk-vault", cancellationToken, vault, films);

        using var libraries = JsonDocument.Parse(await admin.GetStringAsync("Library/VirtualFolders", cancellationToken));
        var ids = libraries.RootElement.EnumerateArray().ToDictionary(f => f.GetProperty("Name").GetString()!, f => Guid.Parse(f.GetProperty("ItemId").GetString()!));

        var library = server.Services.GetRequiredService<ILibraryManager>();
        var libraryWrites = server.Services.GetRequiredService<IItemWriteCommands>();
        var filmsFolder = AddFolder(library, libraryWrites, films);
        var vaultFolder = AddFolder(library, libraryWrites, vault);

        // What the libraries' own refresh would have recorded after a scan, saved so the next boot has it.
        foreach (var (libraryId, folders) in new[] { (ids["blk-films"], new[] { filmsFolder }), (ids["blk-vault"], new[] { vaultFolder, filmsFolder }) })
        {
            var collectionFolder = Assert.IsType<CollectionFolder>(library.GetItemById(libraryId));
            collectionFolder.PhysicalFolderIds = folders;
            await collectionFolder.UpdateToRepositoryAsync(server.Services.GetRequiredService<IItemWriteCommands>(), ItemUpdateType.MetadataEdit, CancellationToken.None);
        }

        var inBoth = Seed(server, Path.Combine(films, "blk-film.mp4"), filmsFolder);
        var vaultOnly = Seed(server, Path.Combine(vault, "blk-vault.mp4"), vaultFolder);

        var (beaToken, beaId) = await SignInAsync(server, admin, "blk-bea", cancellationToken);
        var (cleoToken, cleoId) = await SignInAsync(server, admin, "blk-cleo", cancellationToken);

        return new Seeded(adminToken, beaToken, beaId, cleoToken, cleoId, ids["blk-films"], ids["blk-vault"], inBoth, vaultOnly);
    }

    private static async Task AddLibraryAsync(HttpClient admin, string name, CancellationToken cancellationToken, params string[] paths)
    {
        var query = string.Concat(paths.Select(p => "&paths=" + Uri.EscapeDataString(p)));
        using var added = await admin.PostAsJsonAsync("Library/VirtualFolders?collectionType=movies&refreshLibrary=false&name=" + name + query, new { }, JsonDefaults.Options, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, added.StatusCode);
    }

    private static Guid AddFolder(ILibraryManager library, IItemWriteCommands libraryWrites, string directory)
    {
        var folder = new Folder { Id = Guid.NewGuid(), Name = Path.GetFileName(directory), Path = directory, ParentId = library.RootFolder.Id, DateCreated = DateTime.UtcNow, DateModified = DateTime.UtcNow };
        libraryWrites.CreateItems([folder], library.RootFolder, CancellationToken.None);
        return folder.Id;
    }

    private static Guid Seed(Boot server, string file, Guid folder)
    {
        File.WriteAllText(file, Path.GetFileName(file));
        return LibraryItemSeeder.SeedVideo(
            server.Services.GetRequiredService<IItemWriteCommands>(),
            server.Services.GetRequiredService<IMediaStreamRepository>(),
            file,
            "mp4",
            [
                new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264", Width = 320, Height = 240, IsDefault = true },
                new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "aac", Channels = 2, IsDefault = true },
            ],
            Path.GetFileNameWithoutExtension(file),
            parentId: folder);
    }

    private static async Task<(string Token, Guid Id)> SignInAsync(Boot server, HttpClient admin, string name, CancellationToken cancellationToken)
    {
        UserDto created;
        using (var response = await admin.PostAsJsonAsync("/Users/New", new { Name = name, Password }, JsonDefaults.Options, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            created = (await response.Content.ReadFromJsonAsync<UserDto>(JsonDefaults.Options, cancellationToken))!;
        }

        using var anonymous = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, MediaBoundaryFixture.AuthorizationHeader(name, null));
        request.Content = JsonContent.Create(new { Username = name, Pw = Password }, options: JsonDefaults.Options);
        using var authenticated = await anonymous.SendAsync(request, cancellationToken);
        authenticated.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await authenticated.Content.ReadAsStringAsync(cancellationToken));
        return (document.RootElement.GetProperty("AccessToken").GetString()!, created.Id);
    }

    private sealed record Seeded(string AdminToken, string BeaToken, Guid BeaId, string CleoToken, Guid CleoId, Guid Films, Guid Vault, Guid InBoth, Guid VaultOnly);

    /// <summary>
    /// One server process on a data directory another may have left.
    /// </summary>
    private sealed class Boot(string dataRoot) : E2eApplicationFactory
    {
        protected override string DataRoot { get; } = dataRoot;

        public HttpClient ClientFor(string deviceId, string token)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation(AuthHelper.AuthHeaderName, MediaBoundaryFixture.AuthorizationHeader(deviceId, token));
            return client;
        }

        public HttpClient AdminClient(string token)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.AddAuthHeader(token);
            return client;
        }
    }
}
