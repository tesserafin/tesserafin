using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Tasks;
using Tesserafin.Server.Integration.Tests.PlaybackCredentials;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// SECURITY-MEDIA-R3, over HTTP: whether a request that names items by id is answered with
/// anything of an item its user may not see.
/// </summary>
/// <remarks>
/// The restriction here is a library one - two real virtual folders, a user enabled on one of
/// them - because that is the restriction a query that names ids is not scoped by: tags and
/// ratings travel with the query's user, the libraries do not.
///
/// An answer is searched for the withheld item's name, its file, its folder and its id; where the
/// route selects by id it is also compared with the answer for an id that names nothing.
/// </remarks>
public sealed class ExplicitItemSelectionAuthorizationEndToEndTests : IClassFixture<HlsIsolationApplicationFactory>, IAsyncLifetime
{
    private const string Everything = "fields=Path,MediaSources,MediaStreams,ParentId,Overview,Tags";

    private readonly HlsIsolationApplicationFactory _factory;
    private HttpClient _admin = null!;
    private HttpClient _restricted = null!;
    private Guid _adminId;
    private Guid _restrictedId;
    private Guid _openLibrary;
    private Guid _withheldLibrary;
    private Guid _withheldFolder;
    private Guid _openFolder;
    private Guid _visible;
    private Guid _hidden;
    private Guid _hiddenSeries;
    private string _suffix = null!;
    private string _workDir = null!;
    private string _hiddenName = null!;
    private string _visibleName = null!;

    public ExplicitItemSelectionAuthorizationEndToEndTests(HlsIsolationApplicationFactory factory)
    {
        _factory = factory;
    }

    public static TheoryData<string> Selections => new()
    {
        { "Items?ids={ids}&userId={user}" },
        { "Items?ids={ids}" },
        { "Items?ids={ids}&recursive=true" },
        { "Items?ids={ids}&includeItemTypes=Movie" },
        { "Items?ids={ids}&enableTotalRecordCount=false" },
        { "Items?ids={ids}&limit=10&startIndex=0&sortBy=SortName" },
        { "Items?ids={ids}&parentId={openLibrary}" },
        { "Items?ids={ids}&searchTerm=SECURITY" },
        { "Users/{user}/Items?ids={ids}" },
        { "Trailers?ids={ids}&userId={user}" },
    };

    public static TheoryData<string, string> RoutesNamingTheWithheldItem => new()
    {
        { "GET", "Items/{hidden}" },
        { "GET", "Users/{user}/Items/{hidden}" },
        { "GET", "Items?parentId={hidden}" },
        { "GET", "Items?parentId={hiddenFolder}" },
        { "GET", "Items?parentId={hiddenFolder}&recursive=true" },
        { "GET", "Items?parentId={hiddenLibrary}" },
        { "GET", "Items?parentId={hiddenLibrary}&recursive=true&includeItemTypes=Movie" },
        { "GET", "Items?adjacentTo={hidden}" },
        { "GET", "Items?excludeItemIds={visible}&recursive=true&includeItemTypes=Movie" },
        { "GET", "Items/Filters?parentId={hiddenLibrary}" },
        { "GET", "Items/Filters2?parentId={hiddenLibrary}" },
        { "GET", "Items/{hidden}/Ancestors" },
        { "GET", "Items/{hidden}/Similar" },
        { "GET", "Items/{hidden}/ThemeMedia" },
        { "GET", "Items/{hidden}/SpecialFeatures" },
        { "GET", "Items/{hidden}/LocalTrailers" },
        { "GET", "Items/{hidden}/Intros" },
        { "GET", "Items/{hidden}/InstantMix" },
        { "GET", "Items/{hidden}/PlaybackInfo" },
        { "GET", "Items/{hidden}/Images" },
        { "GET", "Items/{hidden}/CriticReviews" },
        { "GET", "Videos/{hidden}/AdditionalParts" },
        { "GET", "MediaSegments/{hidden}" },
        { "GET", "UserItems/{hidden}/UserData" },
        { "POST", "UserFavoriteItems/{hidden}" },
        { "POST", "UserPlayedItems/{hidden}" },
        { "GET", "Movies/Recommendations" },
        { "GET", "Items/Latest" },
        { "GET", "Search/Hints?searchTerm=SECURITY" },
        { "GET", "Items?parentId={root}" },
        { "GET", "Items?parentId={root}&recursive=true" },
        { "GET", "Items/Latest?parentId={hiddenLibrary}" },
        { "GET", "Items/Latest?parentId={hiddenFolder}" },
        { "GET", "Items/Latest?parentId={root}" },
        { "GET", "UserItems/Resume?parentId={hiddenLibrary}" },
        { "GET", "UserItems/Resume?parentId={root}" },
        { "GET", "Items/Filters" },
        { "GET", "Items/Filters?parentId={hiddenFolder}" },
        { "GET", "Items/Filters?parentId={root}" },
        { "GET", "Items/Filters2" },
        { "GET", "Items/Filters2?parentId={hiddenFolder}" },
        { "GET", "Items/Filters2?parentId={root}" },
        { "GET", "Genres" },
        { "GET", "Genres?parentId={hiddenLibrary}" },
        { "GET", "Genres?parentId={hiddenFolder}" },
        { "GET", "Genres?parentId={root}" },
        { "GET", "Studios" },
        { "GET", "Studios?parentId={hiddenLibrary}" },
        { "GET", "Studios?parentId={hiddenFolder}" },
        { "GET", "Studios?parentId={root}" },
        { "GET", "Years?parentId={hiddenLibrary}" },
        { "GET", "Persons?parentId={hiddenLibrary}" },
        { "GET", "Persons?parentId={hiddenFolder}" },
        { "GET", "Persons?appearsInItemId={hidden}" },
        { "GET", "Shows/NextUp?seriesId={hiddenSeries}" },
        { "GET", "Artists?parentId={hiddenLibrary}" },
        { "GET", "Artists/AlbumArtists?parentId={hiddenLibrary}" },
        { "GET", "MusicGenres?parentId={hiddenLibrary}" },
        { "GET", "Shows/Upcoming?parentId={hiddenLibrary}" },
        { "GET", "Shows/NextUp?parentId={hiddenLibrary}" },
    };

    public async ValueTask InitializeAsync()
    {
        var (adminToken, adminId) = await _factory.EnsureAuthenticatedAsync();
        _adminId = adminId;
        _admin = _factory.CreateClient();
        _admin.DefaultRequestHeaders.AddAuthHeader(adminToken);

        _suffix = Guid.NewGuid().ToString("N")[..8];
        _workDir = Directory.CreateTempSubdirectory("tesserafin-smr3-e2e-").FullName;
        _hiddenName = "SECURITY-MEDIA-R3 withheld " + _suffix;
        _visibleName = "SECURITY-MEDIA-R3 open " + _suffix;

        // Both libraries before either folder: adding a library validates the root's children,
        // and a folder no scan resolved would not survive it.
        var openDir = await AddLibraryAsync("smr3-open-" + _suffix, TestContext.Current.CancellationToken);
        var withheldDir = await AddLibraryAsync("smr3-withheld-" + _suffix, TestContext.Current.CancellationToken);

        // So does the first collection a server is asked for: it creates the collections library, which also
        // queues a library scan nothing awaits. A scan running during the seeding below removes what it did
        // not resolve, and the next seeding write then fails on a foreign key (#317): wait for it to end.
        var beforeFirst = DateTime.UtcNow;
        bool createdTheCollectionsLibrary;
        using (var before = JsonDocument.Parse(await _admin.GetStringAsync("Library/VirtualFolders", TestContext.Current.CancellationToken)))
        {
            createdTheCollectionsLibrary = !before.RootElement.EnumerateArray().Any(f => f.TryGetProperty("CollectionType", out var type) && type.GetString() == "boxsets");
        }

        using (var first = await _admin.PostAsync("Collections?name=smr3-first-" + _suffix, null, TestContext.Current.CancellationToken))
        {
            first.EnsureSuccessStatusCode();
        }

        if (createdTheCollectionsLibrary)
        {
            await WaitForLibraryScanAsync(beforeFirst, TestContext.Current.CancellationToken);
        }

        var libraries = await LibrariesAsync(TestContext.Current.CancellationToken);
        _openLibrary = libraries["smr3-open-" + _suffix];
        _withheldLibrary = libraries["smr3-withheld-" + _suffix];
        _openFolder = AddFolder(openDir, _openLibrary);
        _withheldFolder = AddFolder(withheldDir, _withheldLibrary);

        _visible = Seed(Path.Combine(openDir, "open-" + _suffix + ".mp4"), _visibleName, _openFolder);
        _hidden = Seed(Path.Combine(withheldDir, "withheld-" + _suffix + ".mp4"), _hiddenName, _withheldFolder);

        // People, and a series with one episode to come next, for the routes that name those.
        var library = _factory.Services.GetRequiredService<ILibraryManager>();
        var libraryWrites = _factory.Services.GetRequiredService<IItemWriteCommands>();
        foreach (var (id, who) in new[] { (_hidden, "withheld-" + _suffix + " actor"), (_visible, "open-" + _suffix + " actor") })
        {
            await _factory.Services.GetRequiredService<IItemPeopleCommands>().UpdateAsync(library.GetItemById(id)!, [new PersonInfo { Name = who, Type = PersonKind.Actor }], TestContext.Current.CancellationToken);
        }

        var series = new Series { Id = Guid.NewGuid(), Name = "withheld-" + _suffix + " series", Path = Path.Combine(withheldDir, "series"), ParentId = _withheldFolder, DateCreated = DateTime.UtcNow, DateModified = DateTime.UtcNow };
        series.PresentationUniqueKey = series.CreatePresentationUniqueKey();
        libraryWrites.CreateItems([series], null, CancellationToken.None);
        var episode = new Episode
        {
            Id = Guid.NewGuid(),
            Name = "withheld-" + _suffix + " episode",
            Path = Path.Combine(withheldDir, "series", "withheld-" + _suffix + "-s01e01.mp4"),
            ParentId = series.Id,
            SeriesId = series.Id,
            SeriesName = series.Name,
            SeriesPresentationUniqueKey = series.PresentationUniqueKey,
            ParentIndexNumber = 1,
            IndexNumber = 1,
            DateCreated = DateTime.UtcNow,
            DateModified = DateTime.UtcNow,
        };
        episode.PresentationUniqueKey = episode.CreatePresentationUniqueKey();
        libraryWrites.CreateItems([episode], null, CancellationToken.None);
        _hiddenSeries = series.Id;

        // The scenario holds only if the seeding did: every seeded item exists, under the folder of its library.
        foreach (var (id, parent) in new[] { (_visible, _openFolder), (_hidden, _withheldFolder), (series.Id, _withheldFolder), (episode.Id, series.Id) })
        {
            var seeded = library.GetItemById(id);
            Assert.True(seeded is not null, FormattableString.Invariant($"seeded item {id} is gone"));
            Assert.Equal(parent, seeded.ParentId);
        }

        Assert.Equal([_openLibrary], library.GetCollectionFolders(library.GetItemById(_visible)!).Select(f => f.Id));
        Assert.Equal([_withheldLibrary], library.GetCollectionFolders(library.GetItemById(_hidden)!).Select(f => f.Id));

        (_restricted, _restrictedId) = await SignInAsync("smr3-restricted-" + _suffix, [_openLibrary], TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var name in new[] { "smr3-open-" + _suffix, "smr3-withheld-" + _suffix })
        {
            using var removed = await _admin.DeleteAsync("Library/VirtualFolders?name=" + name, CancellationToken.None);
        }

        _admin.Dispose();
        _restricted.Dispose();
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// The harness restricts what it says it restricts: the user is refused the item by the route
    /// that names one item, and an unrestricted listing does not carry it.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task Harness_TheRestrictedUserSeesOneLibraryAndNotTheOther()
    {
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(_restricted, "GET", "Items/" + _visible, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(_restricted, "GET", "Items/" + _hidden, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(_admin, "GET", "Items/" + _hidden, TestContext.Current.CancellationToken)).Status);

        // What the by-name routes are searched for is there to be found, by who may see it.
        // (Genres, Studios and the other by-name routes list items only a scan creates: on this
        // harness they answer with nothing for anyone, and their rows below cannot fail.)
        foreach (var mark in new[] { "-genre", "-tag" })
        {
            var all = await SendAsync(_admin, "GET", FormattableString.Invariant($"Items/Filters?userId={_adminId}&parentId={_withheldFolder}"), TestContext.Current.CancellationToken);
            Assert.Contains("withheld-" + _suffix + mark, all.Body, StringComparison.Ordinal);
            var mine = await SendAsync(_restricted, "GET", FormattableString.Invariant($"Items/Filters?userId={_restrictedId}&parentId={_openFolder}"), TestContext.Current.CancellationToken);
            Assert.Contains("open-" + _suffix + mark, mine.Body, StringComparison.Ordinal);
        }

        // A parent the restricted user may see still scopes to its children: the library, and the
        // folder inside it, which is an ancestor and no library.
        foreach (var parent in new[] { _openLibrary, _openFolder })
        {
            foreach (var route in new[] { "Items/Latest?", "Items?recursive=true&", "Items?recursive=true&includeItemTypes=Movie&" })
            {
                var under = await SendAsync(_restricted, "GET", FormattableString.Invariant($"{route}userId={_restrictedId}&parentId={parent}"), TestContext.Current.CancellationToken);
                Assert.True(under.Body.Contains(_visibleName, StringComparison.Ordinal), FormattableString.Invariant($"{route}parentId={(parent.Equals(_openLibrary) ? "library" : "folder")}: {(int)under.Status} {under.Body}"));
            }
        }

        // The people and the next episode the rows below look for are there, for who may see them.
        foreach (var route in new[] { "Persons?parentId=" + _withheldLibrary, "Persons?appearsInItemId=" + _hidden })
        {
            Assert.Contains("withheld-" + _suffix + " actor", (await SendAsync(_admin, "GET", route + "&userId=" + _adminId, TestContext.Current.CancellationToken)).Body, StringComparison.Ordinal);
        }

        Assert.Contains("open-" + _suffix + " actor", (await SendAsync(_restricted, "GET", "Persons?appearsInItemId=" + _visible + "&userId=" + _restrictedId, TestContext.Current.CancellationToken)).Body, StringComparison.Ordinal);
        Assert.Contains("withheld-" + _suffix + " episode", (await SendAsync(_admin, "GET", "Shows/NextUp?seriesId=" + _hiddenSeries + "&userId=" + _adminId, TestContext.Current.CancellationToken)).Body, StringComparison.Ordinal);

        var listing = await SendAsync(_restricted, "GET", "Items?recursive=true&includeItemTypes=Movie&" + Everything, TestContext.Current.CancellationToken);
        Assert.Contains(_visibleName, listing.Body, StringComparison.Ordinal);
        AssertNothingOfTheWithheldItem("unrestricted listing", listing.Body);
    }

    /// <summary>
    /// Naming a withheld item by id is answered as naming nothing: no name, no server path, no
    /// media source, no count.
    /// </summary>
    /// <param name="template">The selection, with the ids and the user to fill in.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Theory(Timeout = 120_000)]
    [MemberData(nameof(Selections))]
    public async Task WithheldItemNamedById_IsAnsweredAsAnIdThatNamesNothing(string template)
    {
        var refused = await SendAsync(_restricted, "GET", Fill(template, _hidden) + "&" + Everything, TestContext.Current.CancellationToken);
        var unknown = await SendAsync(_restricted, "GET", Fill(template, Guid.NewGuid()) + "&" + Everything, TestContext.Current.CancellationToken);

        AssertNothingOfTheWithheldItem(template, refused.Body);
        Assert.Equal(unknown.Status, refused.Status);
        Assert.Equal(unknown.Body, refused.Body);
    }

    /// <summary>
    /// A selection mixing an item the user may see with one it may not is answered with the first
    /// alone, counted as one, wherever the withheld id stands in the list.
    /// </summary>
    /// <param name="template">The selection, with the ids and the user to fill in.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Theory(Timeout = 120_000)]
    [MemberData(nameof(Selections))]
    public async Task SelectionMixingVisibleAndWithheldItems_IsAnsweredWithTheVisibleOnesAlone(string template)
    {
        if (template.StartsWith("Trailers", StringComparison.Ordinal))
        {
            // A film is not a trailer: that route has nothing to answer with for either item.
            return;
        }

        foreach (var ids in new[] { new[] { _visible, _hidden }, [_hidden, _visible], [_hidden, _visible, Guid.NewGuid(), _hidden] })
        {
            var mixed = await SendAsync(_restricted, "GET", Fill(template, ids) + "&" + Everything, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, mixed.Status);
            AssertNothingOfTheWithheldItem(template, mixed.Body);
            using var document = JsonDocument.Parse(mixed.Body);
            var item = Assert.Single(document.RootElement.GetProperty("Items").EnumerateArray());
            Assert.Equal(_visible, Guid.Parse(item.GetProperty("Id").GetString()!));
            if (!template.Contains("enableTotalRecordCount=false", StringComparison.Ordinal))
            {
                Assert.Equal(1, document.RootElement.GetProperty("TotalRecordCount").GetInt32());
            }
        }
    }

    /// <summary>
    /// A withheld item is left out before the page is cut, not after: when it sorts first, the
    /// first page of one still holds the visible item, the second is empty, and both count one.
    /// The same pages, asked by a user who may see both, prove the withheld item does sort first.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task PageOfAMixedSelection_IsCutAfterTheWithheldItemIsLeftOut()
    {
        const string Page = "Items?ids={ids}&userId={user}&sortBy=SortName&sortOrder=Descending&limit=1&startIndex=";

        var (allowed, allowedId) = await SignInAsync("smr3-pager-" + _suffix, [_openLibrary, _withheldLibrary], TestContext.Current.CancellationToken);
        using (allowed)
        {
            foreach (var (start, expected) in new[] { (0, _hidden), (1, _visible) })
            {
                var page = await SendAsync(allowed, "GET", FillAs(Page + start, allowedId, _visible, _hidden), TestContext.Current.CancellationToken);
                using var document = JsonDocument.Parse(page.Body);
                Assert.Equal(expected, Guid.Parse(Assert.Single(document.RootElement.GetProperty("Items").EnumerateArray()).GetProperty("Id").GetString()!));
                Assert.Equal(2, document.RootElement.GetProperty("TotalRecordCount").GetInt32());
            }
        }

        var first = await SendAsync(_restricted, "GET", Fill(Page + "0", [_visible, _hidden]) + "&" + Everything, TestContext.Current.CancellationToken);
        AssertNothingOfTheWithheldItem("first page", first.Body);
        using (var document = JsonDocument.Parse(first.Body))
        {
            Assert.Equal(_visible, Guid.Parse(Assert.Single(document.RootElement.GetProperty("Items").EnumerateArray()).GetProperty("Id").GetString()!));
            Assert.Equal(1, document.RootElement.GetProperty("TotalRecordCount").GetInt32());
        }

        var second = await SendAsync(_restricted, "GET", Fill(Page + "1", [_visible, _hidden]) + "&" + Everything, TestContext.Current.CancellationToken);
        AssertNothingOfTheWithheldItem("second page", second.Body);
        using (var document = JsonDocument.Parse(second.Body))
        {
            Assert.Empty(document.RootElement.GetProperty("Items").EnumerateArray());
            Assert.Equal(1, document.RootElement.GetProperty("TotalRecordCount").GetInt32());
        }
    }

    /// <summary>
    /// A user enabled on no library is answered with nothing, whichever item it names.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task UserWithNoLibrary_IsAnsweredWithNothing()
    {
        var (nobody, nobodyId) = await SignInAsync("smr3-nobody-" + _suffix, [], TestContext.Current.CancellationToken);
        using (nobody)
        {
            foreach (var template in Selections.Select(row => row.Data))
            {
                var answer = await SendAsync(nobody, "GET", FillAs(template, nobodyId, _visible, _hidden) + "&" + Everything, TestContext.Current.CancellationToken);
                var unknown = await SendAsync(nobody, "GET", FillAs(template, nobodyId, Guid.NewGuid()) + "&" + Everything, TestContext.Current.CancellationToken);

                AssertNothingOfTheWithheldItem(template, answer.Body);
                Assert.DoesNotContain(_visibleName, answer.Body, StringComparison.Ordinal);
                Assert.Equal(unknown.Status, answer.Status);
                Assert.Equal(unknown.Body, answer.Body);
            }
        }
    }

    /// <summary>
    /// A library withdrawn from a user between two requests is withdrawn from the second, on the
    /// token that was served the first.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task LibraryWithdrawnBetweenTwoRequests_TheSecondNoLongerCarriesItsItems()
    {
        var (second, secondId) = await SignInAsync("smr3-second-" + _suffix, [_openLibrary, _withheldLibrary], TestContext.Current.CancellationToken);
        using (second)
        {
            var url = FillAs("Items?ids={ids}&userId={user}", secondId, _visible, _hidden) + "&" + Everything;

            var before = await SendAsync(second, "GET", url, TestContext.Current.CancellationToken);
            Assert.Contains(_hiddenName, before.Body, StringComparison.Ordinal);
            Assert.Contains(_visibleName, before.Body, StringComparison.Ordinal);

            await RestrictAsync(secondId, [_openLibrary], TestContext.Current.CancellationToken);

            var after = await SendAsync(second, "GET", url, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, after.Status);
            AssertNothingOfTheWithheldItem("after the library was withdrawn", after.Body);
            Assert.Contains(_visibleName, after.Body, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A withheld item and a visible one merged as versions of each other, whichever of the two
    /// is the primary: the visible one is answered with its own media source and nothing of the
    /// other, and the other is answered as nothing when it is named itself.
    /// </summary>
    /// <param name="withheldIsPrimary">Whether the withheld item is the primary version.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Theory(Timeout = 120_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithheldVersionOfAVisibleItem_IsNotDescribedThroughTheVisibleOne(bool withheldIsPrimary)
    {
        // The merge takes the widest as the primary.
        var open = Seed(Path.Combine(_workDir, "smr3-open-" + _suffix, "open-version-" + _suffix + ".mp4"), _visibleName + " version", _openFolder, withheldIsPrimary ? 320 : 1280);
        var withheld = Seed(Path.Combine(_workDir, "smr3-withheld-" + _suffix, "withheld-version-" + _suffix + ".mp4"), _hiddenName + " version", _withheldFolder, withheldIsPrimary ? 1280 : 320);
        using (var merged = await _admin.PostAsync(FormattableString.Invariant($"Videos/MergeVersions?ids={open},{withheld}"), null, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, merged.StatusCode);
        }

        // The merge is real: who may see both is told of two sources, on the primary.
        var primary = withheldIsPrimary ? withheld : open;
        var both = await SendAsync(_admin, "GET", FormattableString.Invariant($"Items/{primary}?userId={_adminId}&{Everything}"), TestContext.Current.CancellationToken);
        using (var document = JsonDocument.Parse(both.Body))
        {
            Assert.Equal(2, document.RootElement.GetProperty("MediaSources").GetArrayLength());
        }

        string[] marks = [_hiddenName, "withheld-", withheld.ToString("N", CultureInfo.InvariantCulture), withheld.ToString()];
        string[] templates =
        [
            "Items?ids={ids}&userId={user}",
            "Items?ids={ids}&recursive=true",
            "Users/{user}/Items?ids={ids}",
            "Items/{ids}",
            "Users/{user}/Items/{ids}",
            "Items/{ids}/PlaybackInfo?userId={user}",
            "Videos/{ids}/AdditionalParts",
            "Items?recursive=true&includeItemTypes=Movie",
            "Items?parentId={openLibrary}",
        ];
        foreach (var template in templates)
        {
            var separator = template.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            var answer = await SendAsync(_restricted, "GET", Fill(template, open) + separator + Everything, TestContext.Current.CancellationToken);

            Assert.True(answer.Status == HttpStatusCode.OK, FormattableString.Invariant($"{template} answered {(int)answer.Status}: {answer.Body}"));
            foreach (var mark in marks)
            {
                Assert.False(answer.Body.Contains(mark, StringComparison.OrdinalIgnoreCase), FormattableString.Invariant($"{template}: the answer carries \"{mark}\": {answer.Body}"));
            }
        }

        // Its own version is still its own, with its own file.
        var own = await SendAsync(_restricted, "GET", FormattableString.Invariant($"Items/{open}?userId={_restrictedId}&{Everything}"), TestContext.Current.CancellationToken);
        using (var document = JsonDocument.Parse(own.Body))
        {
            var source = Assert.Single(document.RootElement.GetProperty("MediaSources").EnumerateArray());
            Assert.Equal(open.ToString("N", CultureInfo.InvariantCulture), source.GetProperty("Id").GetString());
            Assert.EndsWith("open-version-" + _suffix + ".mp4", source.GetProperty("Path").GetString(), StringComparison.Ordinal);
        }

        var named = await SendAsync(_restricted, "GET", Fill("Items?ids={ids}&userId={user}", withheld) + "&" + Everything, TestContext.Current.CancellationToken);
        var unknown = await SendAsync(_restricted, "GET", Fill("Items?ids={ids}&userId={user}", Guid.NewGuid()) + "&" + Everything, TestContext.Current.CancellationToken);
        Assert.Equal(unknown.Body, named.Body);
    }

    /// <summary>
    /// Who may see an item is still answered with it: a user enabled on its library, an
    /// administrator, an API key naming no user, and a caller inside the server.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task WhoMaySeeAnItem_IsStillAnsweredWithIt()
    {
        var (allowed, allowedId) = await SignInAsync("smr3-allowed-" + _suffix, [_openLibrary, _withheldLibrary], TestContext.Current.CancellationToken);
        using (allowed)
        {
            foreach (var template in new[] { "Items?ids={ids}&userId={user}", "Items?ids={ids}", "Users/{user}/Items?ids={ids}", "Items?ids={ids}&recursive=true&includeItemTypes=Movie" })
            {
                var answer = await SendAsync(allowed, "GET", FillAs(template, allowedId, _hidden, _visible) + "&" + Everything, TestContext.Current.CancellationToken);

                Assert.Equal(HttpStatusCode.OK, answer.Status);
                using var document = JsonDocument.Parse(answer.Body);
                Assert.Equal([_hidden, _visible], document.RootElement.GetProperty("Items").EnumerateArray().Select(i => Guid.Parse(i.GetProperty("Id").GetString()!)));
                Assert.Equal(2, document.RootElement.GetProperty("TotalRecordCount").GetInt32());
                Assert.Contains(_suffix + ".mp4", answer.Body, StringComparison.Ordinal);
            }
        }

        var byAdmin = await SendAsync(_admin, "GET", FillAs("Items?ids={ids}&userId={user}", _adminId, _hidden, _visible) + "&" + Everything, TestContext.Current.CancellationToken);
        Assert.Contains(_hiddenName, byAdmin.Body, StringComparison.Ordinal);
        Assert.Contains(_visibleName, byAdmin.Body, StringComparison.Ordinal);

        // The restricted user's own item, by id, as before.
        var mine = await SendAsync(_restricted, "GET", Fill("Items?ids={ids}&userId={user}", _visible) + "&" + Everything, TestContext.Current.CancellationToken);
        Assert.Contains(_visibleName, mine.Body, StringComparison.Ordinal);
        Assert.Contains("open-" + _suffix + ".mp4", mine.Body, StringComparison.Ordinal);

        var app = "smr3-key-" + _suffix;
        using (var created = await _admin.PostAsync("Auth/Keys?app=" + app, null, TestContext.Current.CancellationToken))
        {
            created.EnsureSuccessStatusCode();
        }

        using var keys = JsonDocument.Parse(await _admin.GetStringAsync("Auth/Keys", TestContext.Current.CancellationToken));
        var key = keys.RootElement.GetProperty("Items").EnumerateArray().Single(k => k.GetProperty("AppName").GetString() == app).GetProperty("AccessToken").GetString();
        using var anonymous = _factory.CreateClient();
        var byKey = await SendAsync(anonymous, "GET", FormattableString.Invariant($"Items?ids={_hidden},{_visible}&ApiKey={key}&{Everything}"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, byKey.Status);
        Assert.Contains(_hiddenName, byKey.Body, StringComparison.Ordinal);
        Assert.Contains(_visibleName, byKey.Body, StringComparison.Ordinal);

        // A key that asks as a user is answered as that user.
        var byKeyAsUser = await SendAsync(anonymous, "GET", FormattableString.Invariant($"Items?ids={_hidden},{_visible}&userId={_restrictedId}&ApiKey={key}&{Everything}"), TestContext.Current.CancellationToken);
        AssertNothingOfTheWithheldItem("an API key asking as the restricted user", byKeyAsUser.Body);
        Assert.Contains(_visibleName, byKeyAsUser.Body, StringComparison.Ordinal);

        // Inside the server a query that names ids and no user is not a user's request.
        var system = _factory.Services.GetRequiredService<ILibraryManager>().GetItemList(new InternalItemsQuery { ItemIds = [_hidden, _visible] });
        Assert.Equal(2, system.Count);
    }

    /// <summary>
    /// The other routes a user can name the withheld item, its folder or its library on: none
    /// answers with its name or a path of its.
    /// </summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="template">The route.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Theory(Timeout = 120_000)]
    [MemberData(nameof(RoutesNamingTheWithheldItem))]
    public async Task RouteNamingTheWithheldItem_AnswersWithNothingOfIt(string method, string template)
    {
        var separator = template.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var answer = await SendAsync(_restricted, method, Fill(template, _hidden) + separator + "userId=" + _restrictedId + "&" + Everything, TestContext.Current.CancellationToken);

        Assert.True((int)answer.Status < 500, FormattableString.Invariant($"{method} {template} answered {(int)answer.Status}"));
        AssertNothingOfTheWithheldItem(method + " " + template, answer.Body, idToo: false);
    }

    /// <summary>
    /// A playlist is not a way around: a user cannot put an item it may not see in one of its
    /// own, and is not told of such an item in a playlist someone else shares with it.
    /// </summary>
    /// <param name="ownedByTheRestrictedUser">Whether the restricted user creates the playlist itself, or reads a public one of the administrator's.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Theory(Timeout = 120_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithheldItemInAPlaylist_IsNotDescribedToTheRestrictedUser(bool ownedByTheRestrictedUser)
    {
        var owner = ownedByTheRestrictedUser ? _restricted : _admin;
        var ownerId = ownedByTheRestrictedUser ? _restrictedId : _adminId;
        Guid playlist;
        using (var created = await owner.PostAsJsonAsync("Playlists", new { Name = "smr3-playlist-" + _suffix, Ids = new[] { _hidden, _visible }, UserId = ownerId, MediaType = "Video", IsPublic = true }, JsonDefaults.Options, TestContext.Current.CancellationToken))
        {
            var body = await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(created.IsSuccessStatusCode, body);
            using var document = JsonDocument.Parse(body);
            playlist = Guid.Parse(document.RootElement.GetProperty("Id").GetString()!);
        }

        using (var added = await owner.PostAsync(FormattableString.Invariant($"Playlists/{playlist}/Items?ids={_hidden}&userId={ownerId}"), null, TestContext.Current.CancellationToken))
        {
            Assert.True((int)added.StatusCode < 500);
        }

        // What the playlist holds, told to who may see everything: both items when the
        // administrator filled it, the visible one alone when the restricted user did.
        var held = await SendAsync(_admin, "GET", FormattableString.Invariant($"Playlists/{playlist}/Items?userId={_adminId}"), TestContext.Current.CancellationToken);
        Assert.Contains(_visibleName, held.Body, StringComparison.Ordinal);
        Assert.Equal(!ownedByTheRestrictedUser, held.Body.Contains(_hiddenName, StringComparison.Ordinal));

        foreach (var url in new[]
        {
            FormattableString.Invariant($"Playlists/{playlist}/Items?userId={_restrictedId}&{Everything}"),
            FormattableString.Invariant($"Playlists/{playlist}"),
            FormattableString.Invariant($"Items?parentId={playlist}&userId={_restrictedId}&{Everything}"),
            FormattableString.Invariant($"Items?parentId={playlist}&recursive=true&userId={_restrictedId}&{Everything}"),
            FormattableString.Invariant($"Items/{playlist}?userId={_restrictedId}&{Everything}"),
            FormattableString.Invariant($"Playlists/{playlist}/InstantMix?userId={_restrictedId}&{Everything}"),
        })
        {
            var read = await SendAsync(_restricted, "GET", url, TestContext.Current.CancellationToken);
            Assert.True(read.Status == HttpStatusCode.OK, FormattableString.Invariant($"{url} answered {(int)read.Status}"));
            AssertNothingOfTheWithheldItem(url, read.Body, container: url.StartsWith("Items/", StringComparison.Ordinal));
        }

        var mine = await SendAsync(_restricted, "GET", FormattableString.Invariant($"Playlists/{playlist}/Items?userId={_restrictedId}&{Everything}"), TestContext.Current.CancellationToken);
        Assert.Contains(_visibleName, mine.Body, StringComparison.Ordinal);

        // Nor by asking as the user who may see it.
        var asAnother = await SendAsync(_restricted, "GET", FormattableString.Invariant($"Playlists/{playlist}/Items?userId={_adminId}&{Everything}"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, asAnother.Status);
        AssertNothingOfTheWithheldItem("the playlist, asked as the administrator", asAnother.Body);
    }

    /// <summary>
    /// A collection is the server's, not a user's: one holding a withheld item is listed to the
    /// restricted user without it.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task WithheldItemInACollection_IsNotDescribedToTheRestrictedUser()
    {
        Guid collection;
        using (var created = await _admin.PostAsync(FormattableString.Invariant($"Collections?name=smr3-collection-{_suffix}&ids={_hidden},{_visible}"), null, TestContext.Current.CancellationToken))
        {
            var body = await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(created.IsSuccessStatusCode, body);
            using var document = JsonDocument.Parse(body);
            collection = Guid.Parse(document.RootElement.GetProperty("Id").GetString()!);
        }

        // A box set is reached through the library that hosts it (#320): the restricted user is granted it,
        // and still not the withheld library.
        using (var folders = JsonDocument.Parse(await _admin.GetStringAsync("Library/VirtualFolders", TestContext.Current.CancellationToken)))
        {
            var hosting = Guid.Parse(folders.RootElement.EnumerateArray()
                .Single(f => f.TryGetProperty("CollectionType", out var type) && type.GetString() == "boxsets")
                .GetProperty("ItemId").GetString()!);
            await RestrictAsync(_restrictedId, [_openLibrary, hosting], TestContext.Current.CancellationToken);
        }

        var held = await SendAsync(_admin, "GET", FormattableString.Invariant($"Items?parentId={collection}&userId={_adminId}"), TestContext.Current.CancellationToken);
        Assert.Contains(_visibleName, held.Body, StringComparison.Ordinal);
        Assert.Contains(_hiddenName, held.Body, StringComparison.Ordinal);

        foreach (var url in new[]
        {
            FormattableString.Invariant($"Items?parentId={collection}&userId={_restrictedId}&{Everything}"),
            FormattableString.Invariant($"Items?parentId={collection}&recursive=true&userId={_restrictedId}&{Everything}"),
            FormattableString.Invariant($"Items?parentId={collection}&recursive=true&includeItemTypes=Movie&userId={_restrictedId}&{Everything}"),
            FormattableString.Invariant($"Items/{collection}?userId={_restrictedId}&{Everything}"),
            FormattableString.Invariant($"Items?ids={collection}&userId={_restrictedId}&{Everything}"),
            FormattableString.Invariant($"Items?includeItemTypes=BoxSet&recursive=true&userId={_restrictedId}&{Everything}"),
        })
        {
            var read = await SendAsync(_restricted, "GET", url, TestContext.Current.CancellationToken);
            Assert.True(read.Status == HttpStatusCode.OK, FormattableString.Invariant($"{url} answered {(int)read.Status}"));
            AssertNothingOfTheWithheldItem(url, read.Body, container: !url.Contains("parentId", StringComparison.Ordinal));
        }

        var mine = await SendAsync(_restricted, "GET", FormattableString.Invariant($"Items?parentId={collection}&recursive=true&userId={_restrictedId}&{Everything}"), TestContext.Current.CancellationToken);
        Assert.Contains(_visibleName, mine.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The item a session says it plays or looks at is not described back to it, in its session
    /// or in what the server remembers of the playback.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task WithheldIdReportedAsPlayedOrViewed_IsNotHandedBackDescribed()
    {
        // The instrument sees what it looks for: an item the user may see is described back.
        using (var playing = await _restricted.PostAsJsonAsync("Sessions/Playing", new { ItemId = _visible, MediaSourceId = _visible.ToString("N", CultureInfo.InvariantCulture), PlaySessionId = "smr3-open-" + _suffix, CanSeek = true }, JsonDefaults.Options, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, playing.StatusCode);
        }

        Assert.Contains(_visibleName, (await SendAsync(_restricted, "GET", "Sessions", TestContext.Current.CancellationToken)).Body, StringComparison.Ordinal);
        using (var viewing = await _restricted.PostAsync("Sessions/Viewing?itemId=" + _visible, null, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, viewing.StatusCode);
        }

        // A session repeats the ids its client reported; it is what the server adds that is looked for.
        using (var playing = await _restricted.PostAsJsonAsync("Sessions/Playing", new { ItemId = _hidden, MediaSourceId = _hidden.ToString("N", CultureInfo.InvariantCulture), PlaySessionId = "smr3-" + _suffix, CanSeek = true }, JsonDefaults.Options, TestContext.Current.CancellationToken))
        {
            Assert.True((int)playing.StatusCode < 500);
        }

        AssertNothingOfTheWithheldItem("GET Sessions, after Sessions/Playing", (await SendAsync(_restricted, "GET", "Sessions", TestContext.Current.CancellationToken)).Body, idToo: false);

        using (var progress = await _restricted.PostAsJsonAsync("Sessions/Playing/Progress", new { ItemId = _hidden, MediaSourceId = _hidden.ToString("N", CultureInfo.InvariantCulture), PlaySessionId = "smr3-" + _suffix, PositionTicks = 10_000_000 }, JsonDefaults.Options, TestContext.Current.CancellationToken))
        {
            Assert.True((int)progress.StatusCode < 500);
        }

        AssertNothingOfTheWithheldItem("GET Sessions, after Sessions/Playing/Progress", (await SendAsync(_restricted, "GET", "Sessions", TestContext.Current.CancellationToken)).Body, idToo: false);

        using (var viewing = await _restricted.PostAsync("Sessions/Viewing?itemId=" + _hidden, null, TestContext.Current.CancellationToken))
        {
            Assert.True((int)viewing.StatusCode < 500);
        }

        AssertNothingOfTheWithheldItem("GET Sessions, after Sessions/Viewing", (await SendAsync(_restricted, "GET", "Sessions", TestContext.Current.CancellationToken)).Body, idToo: false);

        using (var stopped = await _restricted.PostAsJsonAsync("Sessions/Playing/Stopped", new { ItemId = _hidden, MediaSourceId = _hidden.ToString("N", CultureInfo.InvariantCulture), PlaySessionId = "smr3-" + _suffix, PositionTicks = 10_000_000 }, JsonDefaults.Options, TestContext.Current.CancellationToken))
        {
            Assert.True((int)stopped.StatusCode < 500);
        }

        foreach (var url in new[] { "Sessions", "UserItems/Resume?userId=" + _restrictedId + "&" + Everything, "Items?filters=IsResumable&recursive=true&userId=" + _restrictedId + "&" + Everything, "Items/Latest?userId=" + _restrictedId + "&" + Everything })
        {
            AssertNothingOfTheWithheldItem(url + ", after a reported playback", (await SendAsync(_restricted, "GET", url, TestContext.Current.CancellationToken)).Body, idToo: false);
        }
    }

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpClient client, string method, string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        using var response = await client.SendAsync(request, cancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private string Fill(string template, params Guid[] ids) => FillAs(template, _restrictedId, ids);

    private string FillAs(string template, Guid user, params Guid[] ids)
        => template
            .Replace("{ids}", string.Join(',', ids), StringComparison.Ordinal)
            .Replace("{user}", user.ToString(), StringComparison.Ordinal)
            .Replace("{hidden}", _hidden.ToString(), StringComparison.Ordinal)
            .Replace("{hiddenSeries}", _hiddenSeries.ToString(), StringComparison.Ordinal)
            .Replace("{visible}", _visible.ToString(), StringComparison.Ordinal)
            .Replace("{hiddenFolder}", _withheldFolder.ToString(), StringComparison.Ordinal)
            .Replace("{hiddenLibrary}", _withheldLibrary.ToString(), StringComparison.Ordinal)
            .Replace("{openLibrary}", _openLibrary.ToString(), StringComparison.Ordinal)
            .Replace("{root}", _factory.Services.GetRequiredService<ILibraryManager>().RootFolder.Id.ToString(), StringComparison.Ordinal);

    private void AssertNothingOfTheWithheldItem(string what, string body, bool idToo = true, bool container = false)
    {
        // A playlist or a collection carries the genres its refresh gathered from every child, as
        // metadata of its own: not looked for on the container itself (known, not repaired here).
        var file = container ? "withheld-" + _suffix + ".mp4" : "withheld-" + _suffix;
        string[] marks = idToo
            ? [_hiddenName, file, _hidden.ToString("N", CultureInfo.InvariantCulture), _hidden.ToString()]
            : [_hiddenName, file];
        foreach (var mark in marks)
        {
            Assert.False(body.Contains(mark, StringComparison.OrdinalIgnoreCase), FormattableString.Invariant($"{what}: the answer carries \"{mark}\": {(body.Length > 600 ? body[..600] : body)}"));
        }
    }

    private Guid Seed(string file, string name, Guid folder, int width = 320)
    {
        File.WriteAllText(file, name);
        return LibraryItemSeeder.SeedVideo(
            _factory.Services.GetRequiredService<IItemWriteCommands>(),
            _factory.Services.GetRequiredService<IMediaStreamRepository>(),
            file,
            "mp4",
            [
                new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264", Width = width, Height = 240, IsDefault = true },
                new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "aac", Channels = 2, IsDefault = true },
            ],
            name,
            parentId: folder,
            configure: movie =>
            {
                // Named after the file, so that what is withheld is recognisable wherever it comes out.
                var mark = Path.GetFileNameWithoutExtension(file);
                movie.Genres = [mark + "-genre"];
                movie.Studios = [mark + "-studio"];
                movie.Tags = [mark + "-tag"];
            });
    }

    private async Task WaitForLibraryScanAsync(DateTime since, CancellationToken cancellationToken)
    {
        var scan = _factory.Services.GetRequiredService<ITaskManager>().ScheduledTasks.Single(w => w.ScheduledTask.Key == "RefreshLibrary");
        for (var i = 0; i < 600; i++)
        {
            if (scan.State == TaskState.Idle && scan.LastExecutionResult?.EndTimeUtc >= since)
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        Assert.Fail("the library scan queued by the collections library did not finish");
    }

    private async Task<string> AddLibraryAsync(string name, CancellationToken cancellationToken)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_workDir, name)).FullName;
        using var added = await _admin.PostAsJsonAsync("Library/VirtualFolders?collectionType=movies&refreshLibrary=false&name=" + name + "&paths=" + Uri.EscapeDataString(directory), new { }, JsonDefaults.Options, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, added.StatusCode);
        return directory;
    }

    /// <summary>
    /// In place of the scan that would create it: the folder a library's directory is under the
    /// root, which is the top parent its items are filed under and the library is scoped by.
    /// </summary>
    private Guid AddFolder(string directory, Guid libraryId)
    {
        var library = _factory.Services.GetRequiredService<ILibraryManager>();
        var libraryWrites = _factory.Services.GetRequiredService<IItemWriteCommands>();
        var folder = new Folder { Id = Guid.NewGuid(), Name = Path.GetFileName(directory), Path = directory, ParentId = library.RootFolder.Id, DateCreated = DateTime.UtcNow, DateModified = DateTime.UtcNow };
        libraryWrites.CreateItems([folder], library.RootFolder, CancellationToken.None);

        // What the library's own refresh would have recorded after a scan.
        Assert.IsType<CollectionFolder>(library.GetItemById(libraryId)).PhysicalFolderIds = [folder.Id];
        return folder.Id;
    }

    private async Task<Dictionary<string, Guid>> LibrariesAsync(CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await _admin.GetStringAsync("Library/VirtualFolders", cancellationToken));
        return document.RootElement.EnumerateArray().ToDictionary(f => f.GetProperty("Name").GetString()!, f => Guid.Parse(f.GetProperty("ItemId").GetString()!));
    }

    private async Task RestrictAsync(Guid userId, Guid[] libraries, CancellationToken cancellationToken)
    {
        var user = (await _admin.GetFromJsonAsync<UserDto>(FormattableString.Invariant($"Users/{userId}"), JsonDefaults.Options, cancellationToken))!;
        user.Policy!.EnableAllFolders = false;
        user.Policy.EnabledFolders = libraries;
        using var applied = await _admin.PostAsJsonAsync(FormattableString.Invariant($"Users/{userId}/Policy"), user.Policy, JsonDefaults.Options, cancellationToken);
        applied.EnsureSuccessStatusCode();
    }

    private async Task<(HttpClient Client, Guid UserId)> SignInAsync(string name, Guid[] libraries, CancellationToken cancellationToken)
    {
        const string Password = "smr3-password";
        UserDto created;
        using (var response = await _admin.PostAsJsonAsync("/Users/New", new { Name = name, Password }, JsonDefaults.Options, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            created = (await response.Content.ReadFromJsonAsync<UserDto>(JsonDefaults.Options, cancellationToken))!;
        }

        await RestrictAsync(created.Id, libraries, cancellationToken);

        using var anonymous = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, MediaBoundaryFixture.AuthorizationHeader(name + "-device", null));
        request.Content = JsonContent.Create(new { Username = name, Pw = Password }, options: JsonDefaults.Options);
        using var authenticated = await anonymous.SendAsync(request, cancellationToken);
        authenticated.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await authenticated.Content.ReadAsStringAsync(cancellationToken));

        var client = _factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(90);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            AuthHelper.AuthHeaderName,
            MediaBoundaryFixture.AuthorizationHeader(name + "-device", document.RootElement.GetProperty("AccessToken").GetString()));
        return (client, created.Id);
    }
}
