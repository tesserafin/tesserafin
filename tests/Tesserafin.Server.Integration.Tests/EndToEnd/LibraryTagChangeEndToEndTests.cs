using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http.Json;
using System.Net.Http;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using System;
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
/// MEDIA-ACCESS-R2, over HTTP: a tag saved on a library applies to the next decision about the
/// items of that library, in the process that saved it.
/// </summary>
/// <remarks>
/// The server under test is one that was started on libraries it already had, as every server is
/// after its first run: one boot creates the data, the next serves it. A server that has just
/// created its libraries validated its root in the same process, which is not the state a
/// restarted server is in.
///
/// Two libraries, one holding the other's directory too, so an item can belong to both: Films
/// (films/) and Vault (vault/ and films/). bea is blocked on the tag put on Vault; cleo is
/// blocked on a tag nothing carries.
/// </remarks>
public sealed class LibraryTagChangeEndToEndTests : IDisposable
{
    private const string VaultTag = "R2-Vault-Only";
    private const string Password = "r2-password";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "reefin-e2e-test-data", "r2-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _mediaRoot = Directory.CreateTempSubdirectory("tesserafin-r2-media-").FullName;

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
    /// Adding the tag refuses bea every covered route at once - by id, PlaybackInfo, the bytes with
    /// her token and with a capability minted before the change - for an item of Vault only and for
    /// one that is in both libraries; removing it gives them back; cleo is never refused. A restart
    /// decides the same, and removing the tag a restarted server loaded gives bea access again.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 300_000)]
    public async Task LibraryTag_AppliesToTheNextDecision_WithoutARestart()
    {
        Seeded seeded;
        using (var first = new Boot(_dataRoot))
        {
            seeded = await SeedAsync(first, TestContext.Current.CancellationToken);
        }

        using (var server = new Boot(_dataRoot))
        {
            using var admin = server.AdminClient(seeded.AdminToken);
            using var bea = server.ClientFor("r2-bea", seeded.BeaToken);
            using var cleo = server.ClientFor("r2-cleo", seeded.CleoToken);
            using var anonymous = server.CreateClient();

            var capabilities = new Dictionary<(string User, Guid Item), string>();
            foreach (var (name, client, userId) in new[] { ("bea", bea, seeded.BeaId), ("cleo", cleo, seeded.CleoId) })
            {
                foreach (var item in seeded.Items)
                {
                    capabilities[(name, item)] = await MintAsync(client, userId, item, TestContext.Current.CancellationToken);
                }
            }

            async Task AssertAsync(string state, bool beaAllowed)
            {
                await AssertDecisionsAsync(state + ", bea", bea, anonymous, seeded.BeaId, seeded, capabilities, "bea", beaAllowed);
                await AssertDecisionsAsync(state + ", cleo", cleo, anonymous, seeded.CleoId, seeded, capabilities, "cleo", allowed: true);
            }

            await AssertAsync("before any tag", beaAllowed: true);

            await TagAsync(admin, seeded.AdminId, seeded.Vault, TestContext.Current.CancellationToken, VaultTag);
            await AssertAsync("after tagging Vault", beaAllowed: false);

            await TagAsync(admin, seeded.AdminId, seeded.Vault, TestContext.Current.CancellationToken);
            await AssertAsync("after untagging Vault", beaAllowed: true);

            await TagAsync(admin, seeded.AdminId, seeded.Vault, TestContext.Current.CancellationToken, VaultTag);
            await AssertAsync("after tagging Vault again", beaAllowed: false);
        }

        using (var restarted = new Boot(_dataRoot))
        {
            using var admin = restarted.AdminClient(seeded.AdminToken);
            using var bea = restarted.ClientFor("r2-bea", seeded.BeaToken);
            using var cleo = restarted.ClientFor("r2-cleo", seeded.CleoToken);
            using var anonymous = restarted.CreateClient();
            var none = new Dictionary<(string User, Guid Item), string>();

            await AssertDecisionsAsync("after a restart, bea", bea, anonymous, seeded.BeaId, seeded, none, "bea", allowed: false);
            await AssertDecisionsAsync("after a restart, cleo", cleo, anonymous, seeded.CleoId, seeded, none, "cleo", allowed: true);

            await TagAsync(admin, seeded.AdminId, seeded.Vault, TestContext.Current.CancellationToken);
            await AssertDecisionsAsync("after untagging the tag a restart loaded, bea", bea, anonymous, seeded.BeaId, seeded, none, "bea", allowed: true);
        }

        Assert.True(_wrong.Count == 0, string.Join(Environment.NewLine, _wrong));
    }

    private async Task AssertDecisionsAsync(
        string what,
        HttpClient client,
        HttpClient anonymous,
        Guid userId,
        Seeded seeded,
        Dictionary<(string User, Guid Item), string> capabilities,
        string name,
        bool allowed)
    {
        var expected = allowed ? HttpStatusCode.OK : HttpStatusCode.NotFound;
        foreach (var item in seeded.Items)
        {
            var at = FormattableString.Invariant($"{what}, item {(item.Equals(seeded.InBoth) ? "in both libraries" : "in Vault only")}");
            Expect(at + ": by id", expected, await StatusAsync(client, HttpMethod.Get, FormattableString.Invariant($"Items/{item}?userId={userId}"), cancellationToken: TestContext.Current.CancellationToken));
            Expect(at + ": PlaybackInfo", expected, await StatusAsync(client, HttpMethod.Post, FormattableString.Invariant($"Items/{item}/PlaybackInfo?userId={userId}"), new { }, cancellationToken: TestContext.Current.CancellationToken));
            Expect(at + ": bytes with the user's token", expected, await StatusAsync(client, HttpMethod.Get, Bytes(item), cancellationToken: TestContext.Current.CancellationToken));
            if (capabilities.TryGetValue((name, item), out var capability))
            {
                Expect(at + ": bytes with a capability minted before the change", expected, await StatusAsync(anonymous, HttpMethod.Get, Bytes(item) + "&playbackCapability=" + Uri.EscapeDataString(capability), cancellationToken: TestContext.Current.CancellationToken));
            }
        }

        Expect(what + ": the Vault library by id", expected, await StatusAsync(client, HttpMethod.Get, FormattableString.Invariant($"Items/{seeded.Vault}?userId={userId}"), cancellationToken: TestContext.Current.CancellationToken));
        Expect(what + ": the Films library by id", HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Get, FormattableString.Invariant($"Items/{seeded.Films}?userId={userId}"), cancellationToken: TestContext.Current.CancellationToken));

        using var listing = JsonDocument.Parse(await client.GetStringAsync(FormattableString.Invariant($"Items?recursive=true&includeItemTypes=Movie&userId={userId}"), TestContext.Current.CancellationToken));
        var listed = listing.RootElement.GetProperty("Items").EnumerateArray().Select(i => Guid.Parse(i.GetProperty("Id").GetString()!)).ToHashSet();
        if (!seeded.Items.All(i => listed.Contains(i) == allowed))
        {
            _wrong.Add(what + ": listing");
        }
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
        using var info = await client.PostAsJsonAsync(FormattableString.Invariant($"Items/{item}/PlaybackInfo?userId={userId}"), new { }, JsonDefaults.Options, cancellationToken);
        info.EnsureSuccessStatusCode();
        using var infoDocument = JsonDocument.Parse(await info.Content.ReadAsStringAsync(cancellationToken));
        var (status, body) = await MintingFixture.MintAsync(client, [PlaybackCapabilityScope.Media], item, item.ToString("N"), infoDocument.RootElement.GetProperty("PlaySessionId").GetString()!);
        Assert.Equal(HttpStatusCode.OK, status);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("Value").GetString()!;
    }

    /// <summary>
    /// What the item editor does: reads the item, changes its tags, posts it back.
    /// </summary>
    private static async Task TagAsync(HttpClient admin, Guid adminId, Guid item, CancellationToken cancellationToken, params string[] tags)
    {
        var dto = JsonNode.Parse(await admin.GetStringAsync(FormattableString.Invariant($"Items/{item}?userId={adminId}"), cancellationToken))!;
        dto["Tags"] = new JsonArray(tags.Select(t => (JsonNode)t).ToArray());
        using var content = new StringContent(dto.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var saved = await admin.PostAsync(FormattableString.Invariant($"Items/{item}"), content, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
    }

    private async Task<Seeded> SeedAsync(Boot server, CancellationToken cancellationToken)
    {
        var (adminToken, adminId) = await server.EnsureAuthenticatedAsync();
        using var admin = server.AdminClient(adminToken);

        var films = Directory.CreateDirectory(Path.Combine(_mediaRoot, "films")).FullName;
        var vault = Directory.CreateDirectory(Path.Combine(_mediaRoot, "vault")).FullName;

        // Both libraries before either folder: adding a library validates the root's children,
        // and a folder no scan resolved would not survive it.
        await AddLibraryAsync(admin, "r2-films", cancellationToken, films);
        await AddLibraryAsync(admin, "r2-vault", cancellationToken, vault, films);

        using var libraries = JsonDocument.Parse(await admin.GetStringAsync("Library/VirtualFolders", cancellationToken));
        var ids = libraries.RootElement.EnumerateArray().ToDictionary(f => f.GetProperty("Name").GetString()!, f => Guid.Parse(f.GetProperty("ItemId").GetString()!));

        var library = server.Services.GetRequiredService<ILibraryManager>();
        var libraryWrites = server.Services.GetRequiredService<IItemWriteCommands>();
        var filmsFolder = AddFolder(library, libraryWrites, films);
        var vaultFolder = AddFolder(library, libraryWrites, vault);

        // What the libraries' own refresh would have recorded after a scan, saved so the next boot has it.
        foreach (var (libraryId, folders) in new[] { (ids["r2-films"], new[] { filmsFolder }), (ids["r2-vault"], new[] { vaultFolder, filmsFolder }) })
        {
            var collectionFolder = Assert.IsType<CollectionFolder>(library.GetItemById(libraryId));
            collectionFolder.PhysicalFolderIds = folders;
            await collectionFolder.UpdateToRepositoryAsync(server.Services.GetRequiredService<IItemWriteCommands>(), ItemUpdateType.MetadataEdit, CancellationToken.None);
        }

        var inBoth = Seed(server, Path.Combine(films, "r2-film.mp4"), filmsFolder);
        var vaultOnly = Seed(server, Path.Combine(vault, "r2-vault.mp4"), vaultFolder);

        var (beaToken, beaId) = await SignInAsync(server, admin, "r2-bea", "r2 vault only", cancellationToken);
        var (cleoToken, cleoId) = await SignInAsync(server, admin, "r2-cleo", "r2 unrelated", cancellationToken);

        return new Seeded(adminToken, adminId, beaToken, beaId, cleoToken, cleoId, ids["r2-films"], ids["r2-vault"], inBoth, [inBoth, vaultOnly]);
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

    private static async Task<(string Token, Guid Id)> SignInAsync(Boot server, HttpClient admin, string name, string blockedTag, CancellationToken cancellationToken)
    {
        UserDto created;
        using (var response = await admin.PostAsJsonAsync("/Users/New", new { Name = name, Password }, JsonDefaults.Options, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            created = (await response.Content.ReadFromJsonAsync<UserDto>(JsonDefaults.Options, cancellationToken))!;
        }

        var policy = created.Policy!;
        policy.BlockedTags = [blockedTag];
        using (var applied = await admin.PostAsJsonAsync(FormattableString.Invariant($"/Users/{created.Id}/Policy"), policy, JsonDefaults.Options, cancellationToken))
        {
            applied.EnsureSuccessStatusCode();
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

    private sealed record Seeded(string AdminToken, Guid AdminId, string BeaToken, Guid BeaId, string CleoToken, Guid CleoId, Guid Films, Guid Vault, Guid InBoth, Guid[] Items);

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
