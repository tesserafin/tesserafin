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
/// Listings that mix item types, or name none, decide box sets and playlists as access by id does
/// (GHSA-c5pr-6h39-jp86): a box set or playlist refused by id is neither listed, nor counted, nor on any page.
/// </summary>
/// <remarks>
/// Films and Kids libraries, a box set over each and an empty one. cleo owns three playlists over the Films
/// movie: private, shared with bea, public. bea is granted Films and the box-set library, so the Kids box set
/// and cleo's private playlist are refused to her by id; her rights then change on the same process.
/// </remarks>
public sealed class MixedListingVisibilityEndToEndTests : IDisposable
{
    private const string Password = "mixed-password";

    private static readonly string[] Queries =
    [
        "includeItemTypes=Movie,BoxSet",
        "includeItemTypes=Movie,Playlist",
        "includeItemTypes=BoxSet,Playlist",
        "includeItemTypes=Movie,BoxSet,Playlist",
        string.Empty,

        // Movies grouped into their box sets: the listing names Movie, yet returns box sets.
        "includeItemTypes=Movie&collapseBoxSetItems=true",
        "collapseBoxSetItems=true",
    ];

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "reefin-e2e-test-data", "mix-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _mediaRoot = Directory.CreateTempSubdirectory("tesserafin-mix-media-").FullName;
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
    /// Every mixed or untyped recursive listing, whole and paged, holds no box set or playlist that access by
    /// id refuses, counts exactly what it returns, and describes none of them in any field.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 600_000)]
    public async Task MixedAndUntypedListings_HoldOnlyWhatAccessByIdAdmits()
    {
        using var server = new Boot(_dataRoot);
        var s = await SeedAsync(server, TestContext.Current.CancellationToken);
        using var admin = server.AdminClient(s.AdminToken);
        using var bea = server.ClientFor("mix-bea", s.BeaToken);

        await UpdatePolicyAsync(
            admin,
            s.BeaId,
            p =>
            {
                p["EnableAllFolders"] = false;
                p["EnabledFolders"] = Ids(s.Films, s.Sets);
            },
            TestContext.Current.CancellationToken);
        await AssertListingsAsync("Films and the box-set library", bea, s.BeaId, s, TestContext.Current.CancellationToken);

        await UpdatePolicyAsync(admin, s.BeaId, p => p["EnabledFolders"] = Ids(s.Films, s.Kids, s.Sets), TestContext.Current.CancellationToken);
        await AssertListingsAsync("Films, Kids and the box-set library", bea, s.BeaId, s, TestContext.Current.CancellationToken);

        await UpdatePolicyAsync(
            admin,
            s.BeaId,
            p =>
            {
                p["EnableAllFolders"] = true;
                p["EnabledFolders"] = Ids();
                p["BlockedMediaFolders"] = Ids(s.Kids);
            },
            TestContext.Current.CancellationToken);
        await AssertListingsAsync("all libraries, Kids blocked", bea, s.BeaId, s, TestContext.Current.CancellationToken);

        await UpdatePolicyAsync(admin, s.BeaId, p => p["BlockedMediaFolders"] = Ids(s.Sets), TestContext.Current.CancellationToken);
        await AssertListingsAsync("all libraries, the box-set library blocked", bea, s.BeaId, s, TestContext.Current.CancellationToken);

        Assert.True(_wrong.Count == 0, string.Join(Environment.NewLine, _wrong));
    }

    /// <summary>
    /// The same mixed and untyped listings scoped to a parent (each library and the playlists folder), recursive
    /// or not: Folder.QueryRecursive only post-filters listings naming box sets or playlists alone, so a mixed
    /// listing under a parent must still hold nothing access by id refuses.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 600_000)]
    public async Task ParentScopedMixedListings_HoldOnlyWhatAccessByIdAdmits()
    {
        using var server = new Boot(_dataRoot);
        var s = await SeedAsync(server, TestContext.Current.CancellationToken);
        using var admin = server.AdminClient(s.AdminToken);
        using var bea = server.ClientFor("mix-bea", s.BeaToken);
        var library = server.Services.GetRequiredService<ILibraryManager>();
        var libraryWrites = server.Services.GetRequiredService<IItemWriteCommands>();
        var parents = new[] { s.Films, s.Kids, s.Sets, library.GetItemById(s.SharedList)!.ParentId };

        await UpdatePolicyAsync(
            admin,
            s.BeaId,
            p =>
            {
                p["EnableAllFolders"] = false;
                p["EnabledFolders"] = Ids(s.Films, s.Sets);
            },
            TestContext.Current.CancellationToken);
        await AssertParentScopedListingsAsync("Films and the box-set library", bea, s.BeaId, s, parents, TestContext.Current.CancellationToken);

        await UpdatePolicyAsync(admin, s.BeaId, p => p["EnabledFolders"] = Ids(s.Films, s.Kids, s.Sets), TestContext.Current.CancellationToken);
        await AssertParentScopedListingsAsync("Films, Kids and the box-set library", bea, s.BeaId, s, parents, TestContext.Current.CancellationToken);

        await UpdatePolicyAsync(
            admin,
            s.BeaId,
            p =>
            {
                p["EnableAllFolders"] = true;
                p["EnabledFolders"] = Ids();
                p["BlockedMediaFolders"] = Ids(s.Kids);
            },
            TestContext.Current.CancellationToken);
        await AssertParentScopedListingsAsync("all libraries, Kids blocked", bea, s.BeaId, s, parents, TestContext.Current.CancellationToken);

        Assert.True(_wrong.Count == 0, string.Join(Environment.NewLine, _wrong));
    }

    /// <summary>
    /// Counts, filters, genres and studios aggregate only what access by id admits: a refused box set or playlist
    /// adds no count and no value, an admitted one still does.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 600_000)]
    public async Task Aggregates_CountAndDescribeOnlyWhatAccessByIdAdmits()
    {
        using var server = new Boot(_dataRoot);
        var s = await SeedAsync(server, TestContext.Current.CancellationToken);
        using var admin = server.AdminClient(s.AdminToken);
        using var bea = server.ClientFor("mix-bea", s.BeaToken);

        // Each value is carried by one item only, so it may appear exactly when that item is admitted.
        var marked = new (Guid Id, string Mark, int Year)[]
        {
            (s.FilmsMovie, "film", 1903), (s.KidsSet, "kidsset", 1901), (s.PrivateList, "private", 1902), (s.SharedList, "shared", 1904),
        };
        var library = server.Services.GetRequiredService<ILibraryManager>();
        var libraryWrites = server.Services.GetRequiredService<IItemWriteCommands>();
        foreach (var (id, mark, year) in marked)
        {
            var item = library.GetItemById(id)!;
            item.Genres = ["mixg-" + mark];
            item.Studios = ["mixs-" + mark];
            item.Tags = ["mixt-" + mark];
            item.OfficialRating = "mixr-" + mark;
            item.ProductionYear = year;
            await item.UpdateToRepositoryAsync(server.Services.GetRequiredService<IItemWriteCommands>(), ItemUpdateType.MetadataEdit, CancellationToken.None);

            // As the library validators leave them: one presentation key each, or the by-name listing folds them into one.
            foreach (BaseItem byName in new BaseItem[] { library.GetGenre("mixg-" + mark), library.GetStudio("mixs-" + mark) })
            {
                byName.PresentationUniqueKey = byName.CreatePresentationUniqueKey();
                await byName.UpdateToRepositoryAsync(server.Services.GetRequiredService<IItemWriteCommands>(), ItemUpdateType.MetadataEdit, CancellationToken.None);
            }
        }

        // Scoped to a parent too: each library and the playlists folder that hold the marked items.
        var parents = new[] { Guid.Empty, s.Films, s.Sets, library.GetItemById(s.SharedList)!.ParentId };

        await UpdatePolicyAsync(
            admin,
            s.BeaId,
            p =>
            {
                p["EnableAllFolders"] = false;
                p["EnabledFolders"] = Ids(s.Films, s.Sets);
            },
            TestContext.Current.CancellationToken);
        await AssertAggregatesAsync("Films and the box-set library", bea, s.BeaId, marked, parents);

        await UpdatePolicyAsync(admin, s.BeaId, p => p["EnabledFolders"] = Ids(s.Films, s.Kids, s.Sets), TestContext.Current.CancellationToken);
        await AssertAggregatesAsync("Films, Kids and the box-set library", bea, s.BeaId, marked, parents);

        await UpdatePolicyAsync(
            admin,
            s.BeaId,
            p =>
            {
                p["EnableAllFolders"] = true;
                p["EnabledFolders"] = Ids();
                p["BlockedMediaFolders"] = Ids(s.Kids);
            },
            TestContext.Current.CancellationToken);
        await AssertAggregatesAsync("all libraries, Kids blocked", bea, s.BeaId, marked, parents);

        Assert.True(_wrong.Count == 0, string.Join(Environment.NewLine, _wrong));
    }

    private async Task AssertAggregatesAsync(string state, HttpClient client, Guid userId, (Guid Id, string Mark, int Year)[] marked, Guid[] parents)
    {
        var admitted = new HashSet<Guid>();
        foreach (var (id, _, _) in marked)
        {
            using var response = await client.GetAsync(FormattableString.Invariant($"Items/{id}?userId={userId}"), TestContext.Current.CancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                admitted.Add(id);
            }
        }

        // Counts: as many box sets as the (already fixed) box-set listing holds.
        using (var counts = JsonDocument.Parse(await client.GetStringAsync(FormattableString.Invariant($"Items/Counts?userId={userId}"), TestContext.Current.CancellationToken)))
        {
            using var sets = JsonDocument.Parse(await client.GetStringAsync(FormattableString.Invariant($"Items?recursive=true&includeItemTypes=BoxSet&userId={userId}"), TestContext.Current.CancellationToken));
            var expected = sets.RootElement.GetProperty("TotalRecordCount").GetInt32();
            var actual = counts.RootElement.GetProperty("BoxSetCount").GetInt32();
            if (actual != expected)
            {
                _wrong.Add(FormattableString.Invariant($"{state}, Items/Counts: BoxSetCount {actual}, the listing holds {expected}"));
            }
        }

        var seen = new HashSet<(string Route, Guid Id)>();
        var typeFilters = new[] { string.Empty, "Movie", "BoxSet", "Playlist", "Movie,BoxSet", "Movie,Playlist", "Movie,BoxSet,Playlist" };
        foreach (var (parent, types) in parents.SelectMany(p => typeFilters.Select(t => (p, t))))
        {
            var filter = (types.Length == 0 ? string.Empty : "&includeItemTypes=" + types) + (parent.Equals(Guid.Empty) ? string.Empty : "&parentId=" + parent);
            foreach (var route in new[] { "Items/Filters", "Items/Filters2", "Genres", "Studios" })
            {
                var url = FormattableString.Invariant($"{route}?userId={userId}{filter}");
                var body = await client.GetStringAsync(url, TestContext.Current.CancellationToken);
                using var document = JsonDocument.Parse(body);
                var years = route == "Items/Filters" && document.RootElement.TryGetProperty("Years", out var y) && y.ValueKind == JsonValueKind.Array
                    ? y.EnumerateArray().Select(e => e.GetInt32()).ToHashSet()
                    : [];
                foreach (var (id, mark, year) in marked)
                {
                    var shown = body.Contains("-" + mark + "\"", StringComparison.Ordinal) || years.Contains(year);
                    if (shown && !admitted.Contains(id))
                    {
                        _wrong.Add(FormattableString.Invariant($"{state}, {url}: describes '{mark}', refused by id"));
                    }

                    if (shown)
                    {
                        seen.Add((route, id));
                    }
                }
            }
        }

        // The control: each route shows every admitted item's values somewhere, so an absence above means something.
        foreach (var route in new[] { "Items/Filters", "Items/Filters2", "Genres", "Studios" })
        {
            foreach (var (id, mark, _) in marked.Where(m => admitted.Contains(m.Id) && !seen.Contains((route, m.Id))))
            {
                _wrong.Add(FormattableString.Invariant($"{state}, {route}: never shows '{mark}', admitted by id"));
            }
        }
    }

    private static JsonArray Ids(params Guid[] ids) => new(ids.Select(i => (JsonNode)i.ToString()).ToArray());

    private static async Task UpdatePolicyAsync(HttpClient admin, Guid userId, Action<JsonObject> change, CancellationToken cancellationToken)
    {
        var policy = JsonNode.Parse(await admin.GetStringAsync(FormattableString.Invariant($"Users/{userId}"), cancellationToken))!["Policy"]!.AsObject();
        change(policy);
        using var content = new StringContent(policy.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var saved = await admin.PostAsync(FormattableString.Invariant($"Users/{userId}/Policy"), content, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
    }

    private async Task AssertParentScopedListingsAsync(string state, HttpClient client, Guid userId, Seeded s, Guid[] parents, CancellationToken cancellationToken)
    {
        var guarded = new (Guid Id, string Name)[]
        {
            (s.FilmsSet, "mix Films Set"), (s.KidsSet, "mix Kids Set"), (s.EmptySet, "mix Empty Set"),
            (s.PrivateList, "mix private list"), (s.SharedList, "mix shared list"), (s.PublicList, "mix public list"),
        };
        var refused = new List<(Guid Id, string Name)>();
        var unseen = new HashSet<Guid>();
        foreach (var item in guarded)
        {
            using var response = await client.GetAsync(FormattableString.Invariant($"Items/{item.Id}?userId={userId}"), cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                refused.Add(item);
            }
            else
            {
                unseen.Add(item.Id);
            }
        }

        if (refused.Count == 0)
        {
            _wrong.Add(state + ": the oracle refuses nothing, so the listings below prove nothing");
        }

        var answered = 0;

        foreach (var parent in parents)
        {
            foreach (var recursive in new[] { "true", "false" })
            {
                foreach (var filter in Queries)
                {
                    var url = FormattableString.Invariant($"Items?recursive={recursive}&parentId={parent}&fields=Overview,Path,Tags,ParentId&userId={userId}") + (filter.Length == 0 ? string.Empty : "&" + filter);
                    using var response = await client.GetAsync(url, cancellationToken);
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        continue;
                    }

                    answered++;
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    using var document = JsonDocument.Parse(body);
                    var ids = document.RootElement.GetProperty("Items").EnumerateArray().Select(i => Guid.Parse(i.GetProperty("Id").GetString()!)).ToList();
                    unseen.ExceptWith(ids);
                    foreach (var (id, name) in refused)
                    {
                        if (ids.Contains(id) || body.Contains(name, StringComparison.Ordinal))
                        {
                            _wrong.Add(FormattableString.Invariant($"{state}, {url}: lists '{name}', refused by id"));
                        }
                    }
                }
            }
        }

        // A refused listing proves nothing: some must answer, and together they must still list
        // every box set or playlist access by id admits.
        if (answered == 0)
        {
            _wrong.Add(state + ": no parent-scoped listing answered, so none was checked");
        }

        foreach (var id in unseen)
        {
            _wrong.Add(FormattableString.Invariant($"{state}: no parent-scoped listing holds {id}, admitted by id"));
        }
    }

    private async Task AssertListingsAsync(string state, HttpClient client, Guid userId, Seeded s, CancellationToken cancellationToken)
    {
        // The oracle is access by id, asked now: what it refuses must appear nowhere below.
        var guarded = new (Guid Id, string Name)[]
        {
            (s.FilmsSet, "mix Films Set"), (s.KidsSet, "mix Kids Set"), (s.EmptySet, "mix Empty Set"),
            (s.PrivateList, "mix private list"), (s.SharedList, "mix shared list"), (s.PublicList, "mix public list"),
        };
        var refused = new List<(Guid Id, string Name)>();
        var admitted = new List<Guid>();
        foreach (var item in guarded)
        {
            using var response = await client.GetAsync(FormattableString.Invariant($"Items/{item.Id}?userId={userId}"), cancellationToken);
            (response.StatusCode == HttpStatusCode.OK ? admitted : null)?.Add(item.Id);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                refused.Add(item);
            }
        }

        await AssertOtherRoutesAsync(state, client, userId, refused);

        foreach (var filter in Queries)
        {
            var url = "Items?recursive=true&fields=Overview,Path,Tags,ParentId&userId=" + userId + (filter.Length == 0 ? string.Empty : "&" + filter);
            var at = state + ", " + (filter.Length == 0 ? "no type filter" : filter);

            using var whole = JsonDocument.Parse(await client.GetStringAsync(url, cancellationToken));
            var body = whole.RootElement.GetRawText();
            var ids = whole.RootElement.GetProperty("Items").EnumerateArray().Select(i => Guid.Parse(i.GetProperty("Id").GetString()!)).ToList();
            var total = whole.RootElement.GetProperty("TotalRecordCount").GetInt32();

            foreach (var (id, name) in refused)
            {
                if (ids.Contains(id) || body.Contains(name, StringComparison.Ordinal))
                {
                    _wrong.Add(FormattableString.Invariant($"{at}: lists '{name}', refused by id"));
                }
            }

            if (total != ids.Count)
            {
                _wrong.Add(FormattableString.Invariant($"{at}: total {total}, {ids.Count} returned"));
            }

            // Every admitted box set or playlist the listing's types name is listed.
            foreach (var id in admitted.Where(id => Names(filter, id, s)))
            {
                if (!ids.Contains(id))
                {
                    _wrong.Add(FormattableString.Invariant($"{at}: misses {id}, admitted by id"));
                }
            }

            // Grouped into box sets: a movie the user may see is listed, itself or through an admitted box set
            // holding it, never lost behind a refused one.
            if (filter.Contains("collapseBoxSetItems=true", StringComparison.Ordinal))
            {
                foreach (var (movie, set) in new[] { (s.FilmsMovie, s.FilmsSet), (s.KidsMovie, s.KidsSet) })
                {
                    using var byId = await client.GetAsync(FormattableString.Invariant($"Items/{movie}?userId={userId}"), cancellationToken);
                    if (byId.StatusCode == HttpStatusCode.OK && !ids.Contains(movie) && !(admitted.Contains(set) && ids.Contains(set)))
                    {
                        _wrong.Add(FormattableString.Invariant($"{at}: movie {movie} admitted by id, neither listed nor grouped into an admitted box set"));
                    }
                }
            }

            // Paged one at a time: the pages hold the same items, the total never counts a refused one.
            var paged = new List<Guid>();
            for (var start = 0; start < ids.Count + 2; start++)
            {
                using var page = JsonDocument.Parse(await client.GetStringAsync(url + "&sortBy=SortName,Id&limit=1&startIndex=" + start, cancellationToken));
                if (page.RootElement.GetProperty("TotalRecordCount").GetInt32() != ids.Count)
                {
                    _wrong.Add(FormattableString.Invariant($"{at}: page {start} total {page.RootElement.GetProperty("TotalRecordCount").GetInt32()}, expected {ids.Count}"));
                }

                paged.AddRange(page.RootElement.GetProperty("Items").EnumerateArray().Select(i => Guid.Parse(i.GetProperty("Id").GetString()!)));
            }

            if (!paged.Order().SequenceEqual(ids.Order()))
            {
                _wrong.Add(FormattableString.Invariant($"{at}: pages [{string.Join(", ", paged)}] differ from the listing [{string.Join(", ", ids)}]"));
            }
        }
    }

    private async Task AssertOtherRoutesAsync(string state, HttpClient client, Guid userId, IReadOnlyList<(Guid Id, string Name)> refused)
    {
        foreach (var url in new[]
        {
            FormattableString.Invariant($"Search/Hints?searchTerm=mix&userId={userId}"),
            FormattableString.Invariant($"Items?searchTerm=mix&recursive=true&userId={userId}"),
            FormattableString.Invariant($"Items/Latest?userId={userId}&fields=Overview,Path"),
            FormattableString.Invariant($"Items?recursive=true&isFavorite=false&userId={userId}"),
            FormattableString.Invariant($"Items?recursive=true&sortBy=DateCreated&sortOrder=Descending&limit=50&userId={userId}"),
        })
        {
            using var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            foreach (var (id, name) in refused)
            {
                if (body.Contains(name, StringComparison.Ordinal) || body.Contains(id.ToString("N"), StringComparison.Ordinal) || body.Contains(id.ToString(), StringComparison.Ordinal))
                {
                    _wrong.Add(FormattableString.Invariant($"{state}, {url}: describes '{name}', refused by id"));
                }
            }
        }
    }

    private static bool Names(string filter, Guid id, Seeded s)
    {
        // Only a type the filter names must be there; a grouped listing shows a box set only in place of its movies.
        var isSet = id.Equals(s.FilmsSet) || id.Equals(s.KidsSet) || id.Equals(s.EmptySet);
        var types = filter.Split('&').FirstOrDefault(p => p.StartsWith("includeItemTypes=", StringComparison.Ordinal))?["includeItemTypes=".Length..].Split(',') ?? [];
        return types.Contains(isSet ? "BoxSet" : "Playlist");
    }

    private async Task<Seeded> SeedAsync(Boot server, CancellationToken cancellationToken)
    {
        var (adminToken, _) = await server.EnsureAuthenticatedAsync();
        using var admin = server.AdminClient(adminToken);

        var films = Directory.CreateDirectory(Path.Combine(_mediaRoot, "films")).FullName;
        var kids = Directory.CreateDirectory(Path.Combine(_mediaRoot, "kids")).FullName;
        await AddLibraryAsync(admin, "mix-films", cancellationToken, films);
        await AddLibraryAsync(admin, "mix-kids", cancellationToken, kids);

        // The first box set creates the box-set library and queues a scan: both before any unscanned folder.
        var before = DateTime.UtcNow;
        var emptySet = await CreateBoxSetAsync(admin, "mix Empty Set", cancellationToken);
        await WaitForLibraryScanAsync(server, before, cancellationToken);

        var library = server.Services.GetRequiredService<ILibraryManager>();
        var libraryWrites = server.Services.GetRequiredService<IItemWriteCommands>();
        var filmsFolder = AddFolder(library, libraryWrites, films);
        var kidsFolder = AddFolder(library, libraryWrites, kids);
        var ids = await LibrariesAsync(admin, cancellationToken);
        foreach (var (libraryId, folder) in new[] { (ids["mix-films"], filmsFolder), (ids["mix-kids"], kidsFolder) })
        {
            var collectionFolder = Assert.IsType<CollectionFolder>(library.GetItemById(libraryId));
            collectionFolder.PhysicalFolderIds = [folder];
            await collectionFolder.UpdateToRepositoryAsync(server.Services.GetRequiredService<IItemWriteCommands>(), ItemUpdateType.MetadataEdit, CancellationToken.None);
        }

        var filmsMovie = Seed(server, Path.Combine(films, "mix-film.mp4"), filmsFolder);
        var kidsMovie = Seed(server, Path.Combine(kids, "mix-toon.mp4"), kidsFolder);
        var filmsSet = await CreateBoxSetAsync(admin, "mix Films Set", cancellationToken, filmsMovie);
        var kidsSet = await CreateBoxSetAsync(admin, "mix Kids Set", cancellationToken, kidsMovie);

        using var virtualFolders = JsonDocument.Parse(await admin.GetStringAsync("Library/VirtualFolders", cancellationToken));
        var sets = Guid.Parse(virtualFolders.RootElement.EnumerateArray()
            .Single(f => f.TryGetProperty("CollectionType", out var type) && type.GetString() == "boxsets")
            .GetProperty("ItemId").GetString()!);

        var (beaToken, beaId) = await SignInAsync(server, admin, "mix-bea", cancellationToken);
        var (cleoToken, cleoId) = await SignInAsync(server, admin, "mix-cleo", cancellationToken);
        using var cleo = server.ClientFor("mix-cleo", cleoToken);
        var privateList = await CreatePlaylistAsync(cleo, cleoId, "mix private list", filmsMovie, isPublic: false, sharedWith: [], cancellationToken: cancellationToken);
        var sharedList = await CreatePlaylistAsync(cleo, cleoId, "mix shared list", filmsMovie, isPublic: false, sharedWith: [beaId], cancellationToken: cancellationToken);
        var publicList = await CreatePlaylistAsync(cleo, cleoId, "mix public list", filmsMovie, isPublic: true, sharedWith: [], cancellationToken: cancellationToken);

        // The scenario holds only if the preparation did: each box set has its child, each playlist its item.
        // The playlists are asked as their owner: a private one is no one else's, administrator included.
        foreach (var (asker, container, child) in new[] { (admin, filmsSet, filmsMovie), (admin, kidsSet, kidsMovie), (cleo, privateList, filmsMovie), (cleo, sharedList, filmsMovie), (cleo, publicList, filmsMovie) })
        {
            using var children = JsonDocument.Parse(await asker.GetStringAsync(FormattableString.Invariant($"Items?parentId={container}"), cancellationToken));
            Assert.Contains(child.ToString("N"), children.RootElement.GetRawText().Replace("-", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        }

        return new Seeded(adminToken, beaToken, beaId, ids["mix-films"], ids["mix-kids"], sets, filmsSet, kidsSet, emptySet, privateList, sharedList, publicList, filmsMovie, kidsMovie);
    }

    private static async Task<Guid> CreatePlaylistAsync(HttpClient owner, Guid ownerId, string name, Guid item, bool isPublic, Guid[] sharedWith, CancellationToken cancellationToken)
    {
        var body = new { Name = name, Ids = new[] { item }, UserId = ownerId, IsPublic = isPublic, Users = sharedWith.Select(u => new { UserId = u, CanEdit = false }).ToArray() };
        using var created = await owner.PostAsJsonAsync("Playlists", body, JsonDefaults.Options, cancellationToken);
        var text = await created.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(created.IsSuccessStatusCode, name + ": " + created.StatusCode + " " + text);
        using var document = JsonDocument.Parse(text);
        return Guid.Parse(document.RootElement.GetProperty("Id").GetString()!);
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

    private sealed record Seeded(
        string AdminToken,
        string BeaToken,
        Guid BeaId,
        Guid Films,
        Guid Kids,
        Guid Sets,
        Guid FilmsSet,
        Guid KidsSet,
        Guid EmptySet,
        Guid PrivateList,
        Guid SharedList,
        Guid PublicList,
        Guid FilmsMovie,
        Guid KidsMovie);

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
