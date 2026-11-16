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
using Tesserafin.Controller.Persistence;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Tasks;
using Tesserafin.Server.Integration.Tests.PlaybackCredentials;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// Box sets over HTTP (#320): a box set is reachable only through the library that hosts it (the box-set
/// library the server creates for collections), then through its children's libraries as before.
/// </summary>
/// <remarks>
/// Two media libraries, Films and Kids, and one box set over each (FilmsSet, KidsSet). bea's policy
/// changes on one process; cleo's never does; the admin's own libraries are limited once. Every state
/// asserts each box set by id, through <c>Items?ids=</c>, in the paged box set listing and its total, in a
/// listing mixing movies and box sets, and its children; and that the movies themselves keep the rights
/// their libraries give.
/// </remarks>
public sealed class CollectionAccessEndToEndTests : IDisposable
{
    private const string Password = "collection-password";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "reefin-e2e-test-data", "col-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _mediaRoot = Directory.CreateTempSubdirectory("tesserafin-col-media-").FullName;

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
    /// Without the hosting library, no box set is reachable by any route; with it, each box set follows its
    /// children's libraries; blocking it refuses them all; it grants nothing on the movies; and every
    /// change applies to the next request of the same process.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 300_000)]
    public async Task BoxSets_FollowTheirHostingLibrary_ThenTheirChildrensLibraries()
    {
        using var server = new Boot(_dataRoot);
        var seeded = await SeedAsync(server, TestContext.Current.CancellationToken);
        using var admin = server.AdminClient(seeded.AdminToken);
        using var bea = server.ClientFor("col-bea", seeded.BeaToken);
        using var cleo = server.ClientFor("col-cleo", seeded.CleoToken);

        async Task AssertAsync(string state, bool hosting, bool filmsSet, bool kidsSet, bool filmsMovie, bool kidsMovie)
        {
            await AssertDecisionsAsync(state + ", bea", bea, seeded.BeaId, seeded, hosting, filmsSet, kidsSet, filmsMovie, kidsMovie, TestContext.Current.CancellationToken);
            await AssertDecisionsAsync(state + ", cleo", cleo, seeded.CleoId, seeded, true, true, true, true, true, TestContext.Current.CancellationToken);
        }

        await AssertAsync("all libraries", hosting: true, filmsSet: true, kidsSet: true, filmsMovie: true, kidsMovie: true);

        await UpdatePolicyAsync(
            admin,
            seeded.BeaId,
            p =>
            {
                p["EnableAllFolders"] = false;
                p["EnabledFolders"] = Ids(seeded.Films);
            },
            TestContext.Current.CancellationToken);
        await AssertAsync("Films only", hosting: false, filmsSet: false, kidsSet: false, filmsMovie: true, kidsMovie: false);

        await UpdatePolicyAsync(admin, seeded.BeaId, p => p["EnabledFolders"] = Ids(seeded.Films, seeded.Sets), TestContext.Current.CancellationToken);
        await AssertAsync("Films and the box-set library", hosting: true, filmsSet: true, kidsSet: false, filmsMovie: true, kidsMovie: false);

        await UpdatePolicyAsync(admin, seeded.BeaId, p => p["EnabledFolders"] = Ids(seeded.Sets), TestContext.Current.CancellationToken);
        await AssertAsync("the box-set library only", hosting: true, filmsSet: false, kidsSet: false, filmsMovie: false, kidsMovie: false);

        await UpdatePolicyAsync(
            admin,
            seeded.BeaId,
            p =>
            {
                p["EnableAllFolders"] = true;
                p["EnabledFolders"] = Ids();
                p["BlockedMediaFolders"] = Ids(seeded.Sets);
            },
            TestContext.Current.CancellationToken);
        await AssertAsync("the box-set library blocked", hosting: false, filmsSet: false, kidsSet: false, filmsMovie: true, kidsMovie: true);

        await UpdatePolicyAsync(admin, seeded.BeaId, p => p["BlockedMediaFolders"] = Ids(seeded.Kids), TestContext.Current.CancellationToken);
        await AssertAsync("Kids blocked", hosting: true, filmsSet: true, kidsSet: false, filmsMovie: true, kidsMovie: false);

        await UpdatePolicyAsync(admin, seeded.BeaId, p => p["BlockedMediaFolders"] = Ids(), TestContext.Current.CancellationToken);
        await AssertAsync("cleared", hosting: true, filmsSet: true, kidsSet: true, filmsMovie: true, kidsMovie: true);

        // An administrator whose libraries are limited keeps the limit.
        await UpdatePolicyAsync(
            admin,
            seeded.AdminId,
            p =>
            {
                p["EnableAllFolders"] = false;
                p["EnabledFolders"] = Ids(seeded.Kids);
            },
            TestContext.Current.CancellationToken);
        await AssertDecisionsAsync("admin, Kids only", admin, seeded.AdminId, seeded, false, false, false, false, true, TestContext.Current.CancellationToken);
        await UpdatePolicyAsync(admin, seeded.AdminId, p => p["EnabledFolders"] = Ids(seeded.Kids, seeded.Sets), TestContext.Current.CancellationToken);
        await AssertDecisionsAsync("admin, Kids and the box-set library", admin, seeded.AdminId, seeded, true, false, true, false, true, TestContext.Current.CancellationToken);
        await UpdatePolicyAsync(
            admin,
            seeded.AdminId,
            p =>
            {
                p["EnableAllFolders"] = true;
                p["EnabledFolders"] = Ids();
            },
            TestContext.Current.CancellationToken);
        await AssertDecisionsAsync("admin, all libraries again", admin, seeded.AdminId, seeded, true, true, true, true, true, TestContext.Current.CancellationToken);

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

    private async Task AssertDecisionsAsync(string what, HttpClient client, Guid userId, Seeded seeded, bool hosting, bool filmsSet, bool kidsSet, bool filmsMovie, bool kidsMovie, CancellationToken cancellationToken)
    {
        // The empty box set has no children's libraries to match: the hosting library alone decides.
        var sets = new[]
        {
            (seeded.FilmsSet, "FilmsSet", filmsSet, (Guid?)seeded.FilmsMovie, filmsMovie),
            (seeded.KidsSet, "KidsSet", kidsSet, seeded.KidsMovie, kidsMovie),
            (seeded.EmptySet, "EmptySet", hosting, null, false),
        };
        foreach (var (set, label, allowed, child, childAllowed) in sets)
        {
            var at = what + ", " + label;
            Expect(at + ": by id", allowed ? HttpStatusCode.OK : HttpStatusCode.NotFound, await StatusAsync(client, FormattableString.Invariant($"Items/{set}?userId={userId}"), cancellationToken));

            var byIds = await ListAsync(client, FormattableString.Invariant($"Items?ids={set}&userId={userId}"), cancellationToken);
            Check(at + ": Items?ids=", allowed ? [set] : [], byIds.Ids, byIds.Total);

            // The children of an admitted box set stay filtered by their own library (#320).
            var (status, children) = await TryListAsync(client, FormattableString.Invariant($"Items?parentId={set}&userId={userId}"), cancellationToken);
            if (allowed)
            {
                Expect(at + ": children status", HttpStatusCode.OK, status);
                Check(at + ": children", childAllowed ? [child!.Value] : [], children?.Ids ?? [], children?.Total ?? -1);
            }
            else
            {
                // A parent the user may not see answers as one that does not exist.
                Expect(at + ": children of a refused box set", HttpStatusCode.NotFound, status);
            }
        }

        Expect(what + ": children of an absent parent", HttpStatusCode.NotFound, (await TryListAsync(client, FormattableString.Invariant($"Items?parentId={Guid.NewGuid()}&userId={userId}"), cancellationToken)).Status);
        Expect(what + ": children of a malformed parent id", HttpStatusCode.BadRequest, (await TryListAsync(client, FormattableString.Invariant($"Items?parentId=not-an-id&userId={userId}"), cancellationToken)).Status);

        var expectedSets = sets.Where(s => s.Item3).Select(s => s.Item1).ToArray();
        var expectedMovies = new[] { (seeded.FilmsMovie, filmsMovie), (seeded.KidsMovie, kidsMovie) }.Where(m => m.Item2).Select(m => m.Item1).ToArray();

        var listing = await ListAsync(client, FormattableString.Invariant($"Items?recursive=true&includeItemTypes=BoxSet&userId={userId}"), cancellationToken);
        Check(what + ": box set listing", expectedSets, listing.Ids, listing.Total);

        // Paged: the total counts the admitted box sets, the page holds one of them.
        var page = await ListAsync(client, FormattableString.Invariant($"Items?recursive=true&includeItemTypes=BoxSet&sortBy=SortName&limit=1&userId={userId}"), cancellationToken);
        if (page.Total != expectedSets.Length || page.Ids.Count != Math.Min(1, expectedSets.Length) || !page.Ids.All(expectedSets.Contains))
        {
            _wrong.Add(FormattableString.Invariant($"{what}: paged box set listing total {page.Total} [{string.Join(", ", page.Ids)}], expected total {expectedSets.Length}"));
        }

        // A listing mixing types holds exactly the user's movies and the box sets access by id admits.
        var mixed = await ListAsync(client, FormattableString.Invariant($"Items?recursive=true&includeItemTypes=Movie,BoxSet&userId={userId}"), cancellationToken);
        Check(what + ": movies and box sets listing", [.. expectedSets, .. expectedMovies], mixed.Ids, mixed.Total);

        var movies = await ListAsync(client, FormattableString.Invariant($"Items?recursive=true&includeItemTypes=Movie&userId={userId}"), cancellationToken);
        Check(what + ": movie listing", expectedMovies, movies.Ids, movies.Total);
        foreach (var (movie, allowed) in new[] { (seeded.FilmsMovie, filmsMovie), (seeded.KidsMovie, kidsMovie) })
        {
            Expect(FormattableString.Invariant($"{what}: movie {movie} by id"), allowed ? HttpStatusCode.OK : HttpStatusCode.NotFound, await StatusAsync(client, FormattableString.Invariant($"Items/{movie}?userId={userId}"), cancellationToken));
        }
    }

    private void Check(string what, Guid[] expected, IReadOnlyCollection<Guid> actual, int total)
    {
        if (!actual.Order().SequenceEqual(expected.Order()) || total != expected.Length)
        {
            _wrong.Add(FormattableString.Invariant($"{what}: [{string.Join(", ", actual)}] total {total}, expected [{string.Join(", ", expected)}]"));
        }
    }

    private void Expect(string what, HttpStatusCode expected, HttpStatusCode actual)
    {
        if (actual != expected)
        {
            _wrong.Add(FormattableString.Invariant($"{what}: {(int)actual}, expected {(int)expected}"));
        }
    }

    private static async Task<Listed> ListAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        var (status, listed) = await TryListAsync(client, url, cancellationToken);
        Assert.True(status == HttpStatusCode.OK, url + ": " + status);
        return listed!;
    }

    private static async Task<(HttpStatusCode Status, Listed? Listed)> TryListAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return (response.StatusCode, null);
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var ids = document.RootElement.GetProperty("Items").EnumerateArray().Select(i => Guid.Parse(i.GetProperty("Id").GetString()!)).ToList();
        return (response.StatusCode, new Listed(ids, document.RootElement.GetProperty("TotalRecordCount").GetInt32()));
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return response.StatusCode;
    }

    private async Task<Seeded> SeedAsync(Boot server, CancellationToken cancellationToken)
    {
        var (adminToken, adminId) = await server.EnsureAuthenticatedAsync();
        using var admin = server.AdminClient(adminToken);

        var films = Directory.CreateDirectory(Path.Combine(_mediaRoot, "films")).FullName;
        var kids = Directory.CreateDirectory(Path.Combine(_mediaRoot, "kids")).FullName;
        await AddLibraryAsync(admin, "col-films", cancellationToken, films);
        await AddLibraryAsync(admin, "col-kids", cancellationToken, kids);

        // The server creates the hosting library itself, with the first box set. Adding a library validates
        // the root's children and queues a library scan, so both come before any folder no scan resolved.
        var before = DateTime.UtcNow;
        var emptySet = await CreateBoxSetAsync(admin, "Empty Set", cancellationToken);
        await WaitForLibraryScanAsync(server, before, cancellationToken);

        var library = server.Services.GetRequiredService<ILibraryManager>();
        var libraryWrites = server.Services.GetRequiredService<IItemWriteCommands>();
        var filmsFolder = AddFolder(library, libraryWrites, films);
        var kidsFolder = AddFolder(library, libraryWrites, kids);
        var ids = await LibrariesAsync(admin, cancellationToken);
        foreach (var (libraryId, folder) in new[] { (ids["col-films"], filmsFolder), (ids["col-kids"], kidsFolder) })
        {
            // What the library's own refresh would have recorded after a scan.
            var collectionFolder = Assert.IsType<CollectionFolder>(library.GetItemById(libraryId));
            collectionFolder.PhysicalFolderIds = [folder];
            await collectionFolder.UpdateToRepositoryAsync(server.Services.GetRequiredService<IItemWriteCommands>(), ItemUpdateType.MetadataEdit, CancellationToken.None);
        }

        var filmsMovie = Seed(server, Path.Combine(films, "col-film.mp4"), filmsFolder);
        var kidsMovie = Seed(server, Path.Combine(kids, "col-toon.mp4"), kidsFolder);

        var filmsSet = await CreateBoxSetAsync(admin, "Films Set", cancellationToken, filmsMovie);
        var kidsSet = await CreateBoxSetAsync(admin, "Kids Set", cancellationToken, kidsMovie);
        using var virtualFolders = JsonDocument.Parse(await admin.GetStringAsync("Library/VirtualFolders", cancellationToken));
        var sets = Guid.Parse(Assert.Single(
            virtualFolders.RootElement.EnumerateArray(),
            f => f.TryGetProperty("CollectionType", out var type) && type.GetString() == "boxsets").GetProperty("ItemId").GetString()!);

        var (beaToken, beaId) = await SignInAsync(server, admin, "col-bea", cancellationToken);
        var (cleoToken, cleoId) = await SignInAsync(server, admin, "col-cleo", cancellationToken);

        return new Seeded(adminToken, adminId, beaToken, beaId, cleoToken, cleoId, ids["col-films"], ids["col-kids"], sets, filmsMovie, kidsMovie, filmsSet, kidsSet, emptySet);
    }

    private static async Task WaitForLibraryScanAsync(Boot server, DateTime since, CancellationToken cancellationToken)
    {
        var scan = server.Services.GetRequiredService<ITaskManager>().ScheduledTasks.Single(w => w.ScheduledTask.Key == "RefreshLibrary");
        for (var i = 0; i < 600; i++)
        {
            if (scan.State == TaskState.Idle && scan.LastExecutionResult?.EndTimeUtc >= since)
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        Assert.Fail("the library scan queued by the box-set library did not finish");
    }

    private static async Task<Dictionary<string, Guid>> LibrariesAsync(HttpClient admin, CancellationToken cancellationToken)
    {
        using var libraries = JsonDocument.Parse(await admin.GetStringAsync("Library/VirtualFolders", cancellationToken));
        return libraries.RootElement.EnumerateArray().ToDictionary(f => f.GetProperty("Name").GetString()!, f => Guid.Parse(f.GetProperty("ItemId").GetString()!));
    }

    private static async Task<Guid> CreateBoxSetAsync(HttpClient admin, string name, CancellationToken cancellationToken, params Guid[] children)
    {
        var ids = children.Length == 0 ? string.Empty : "&ids=" + string.Join(',', children);
        using var created = await admin.PostAsync("Collections?name=" + Uri.EscapeDataString(name) + ids, null, cancellationToken);
        var body = await created.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(created.StatusCode == HttpStatusCode.OK, name + ": " + created.StatusCode + " " + body);
        using var document = JsonDocument.Parse(body);
        return Guid.Parse(document.RootElement.GetProperty("Id").GetString()!);
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

    private sealed record Listed(List<Guid> Ids, int Total);

    private sealed record Seeded(
        string AdminToken,
        Guid AdminId,
        string BeaToken,
        Guid BeaId,
        string CleoToken,
        Guid CleoId,
        Guid Films,
        Guid Kids,
        Guid Sets,
        Guid FilmsMovie,
        Guid KidsMovie,
        Guid FilmsSet,
        Guid KidsSet,
        Guid EmptySet);

    /// <summary>
    /// One server process on its own data directory.
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
