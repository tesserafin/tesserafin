using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http.Json;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Database.Implementations;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Server.Integration.Tests.PlaybackCredentials;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// #129 step 5, over HTTP: "mark played / unplayed" on a real host, read back from the database
/// it persisted to, for one user and not another, through a folder and a series, and refused
/// without a write for an item the user may not see.
/// </summary>
public sealed class ItemPlayedStateEndToEndTests : IClassFixture<HlsIsolationApplicationFactory>, IAsyncLifetime
{
    private static readonly DateTime ReportedDate = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private readonly HlsIsolationApplicationFactory _factory;
    private HttpClient _admin = null!;
    private string _suffix = null!;
    private string _workDir = null!;
    private Guid _openLibrary;
    private Guid _openFolder;
    private Guid _film;
    private Guid _withheldFilm;
    private Guid _series;
    private Guid _episode1;
    private Guid _episode2;
    private Guid _missingAired;
    private Guid _missingUnaired;

    public ItemPlayedStateEndToEndTests(HlsIsolationApplicationFactory factory)
    {
        _factory = factory;
    }

    public async ValueTask InitializeAsync()
    {
        var (adminToken, _) = await _factory.EnsureAuthenticatedAsync();
        _admin = _factory.CreateClient();
        _admin.DefaultRequestHeaders.AddAuthHeader(adminToken);

        _suffix = Guid.NewGuid().ToString("N")[..8];
        _workDir = Directory.CreateTempSubdirectory("tesserafin-played-e2e-").FullName;

        // Both libraries before either folder: adding a library validates the root's children,
        // and a folder no scan resolved would not survive it.
        var openDir = await AddLibraryAsync("played-open-" + _suffix);
        var withheldDir = await AddLibraryAsync("played-withheld-" + _suffix);
        var libraries = await LibrariesAsync();
        _openLibrary = libraries["played-open-" + _suffix];
        _openFolder = AddFolder(openDir, _openLibrary);
        var withheldFolder = AddFolder(withheldDir, libraries["played-withheld-" + _suffix]);

        _film = SeedFilm(Path.Combine(openDir, "film-" + _suffix + ".mp4"), _openFolder);
        _withheldFilm = SeedFilm(Path.Combine(withheldDir, "withheld-" + _suffix + ".mp4"), withheldFolder);

        var library = _factory.Services.GetRequiredService<ILibraryManager>();
        var libraryWrites = _factory.Services.GetRequiredService<IItemWriteCommands>();
        var series = new Series { Id = Guid.NewGuid(), Name = "played-series-" + _suffix, Path = Path.Combine(openDir, "series"), ParentId = _openFolder, DateCreated = DateTime.UtcNow, DateModified = DateTime.UtcNow };
        series.PresentationUniqueKey = series.CreatePresentationUniqueKey();
        libraryWrites.CreateItems([series], null, CancellationToken.None);
        _series = series.Id;

        _episode1 = SeedEpisode(series, 1, virtualItem: false, premiere: DateTime.UtcNow.AddYears(-1));
        _episode2 = SeedEpisode(series, 2, virtualItem: false, premiere: DateTime.UtcNow.AddYears(-1));
        _missingAired = SeedEpisode(series, 3, virtualItem: true, premiere: DateTime.UtcNow.AddYears(-1));
        _missingUnaired = SeedEpisode(series, 4, virtualItem: true, premiere: DateTime.UtcNow.AddYears(1));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var name in new[] { "played-open-" + _suffix, "played-withheld-" + _suffix })
        {
            using var removed = await _admin.DeleteAsync("Library/VirtualFolders?name=" + name, CancellationToken.None);
        }

        _admin.Dispose();
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
    /// A reported date counts one more play over the existing ones, clears the resume point and is
    /// recorded as is; the other user's state is untouched. Unplayed then clears all of it.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task MarkPlayedWithDate_ThenUnplayed_PersistsForThatUserOnly()
    {
        var (alice, aliceId) = await SignInAsync("played-alice-" + _suffix);
        var (bob, bobId) = await SignInAsync("played-bob-" + _suffix);
        using (alice)
        using (bob)
        {
            // Two earlier plays and a resume point, through the user data route.
            await PostUserDataAsync(alice, _film, new UpdateUserItemDataDto { PlayCount = 2, PlaybackPositionTicks = 5_000 });
            await PostUserDataAsync(bob, _film, new UpdateUserItemDataDto { PlayCount = 1, PlaybackPositionTicks = 7_000 });

            using (var played = await alice.PostAsync("UserPlayedItems/" + _film + "?datePlayed=" + Uri.EscapeDataString(ReportedDate.ToString("o")), null, TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, played.StatusCode);
                var dto = (await played.Content.ReadFromJsonAsync<UserItemDataDto>(JsonDefaults.Options, TestContext.Current.CancellationToken))!;
                Assert.True(dto.Played);
                Assert.Equal(3, dto.PlayCount);
            }

            var alicesRow = await StoredAsync(aliceId, _film);
            Assert.NotNull(alicesRow);
            Assert.True(alicesRow.Played);
            Assert.Equal(3, alicesRow.PlayCount);
            Assert.Equal(0, alicesRow.PlaybackPositionTicks);
            Assert.Equal(ReportedDate, DateTime.SpecifyKind(alicesRow.LastPlayedDate!.Value, DateTimeKind.Utc));

            var bobsRow = await StoredAsync(bobId, _film);
            Assert.NotNull(bobsRow);
            Assert.False(bobsRow.Played);
            Assert.Equal(1, bobsRow.PlayCount);
            Assert.Equal(7_000, bobsRow.PlaybackPositionTicks);

            using (var unplayed = await alice.DeleteAsync("UserPlayedItems/" + _film, TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, unplayed.StatusCode);
            }

            alicesRow = await StoredAsync(aliceId, _film);
            Assert.NotNull(alicesRow);
            Assert.False(alicesRow.Played);
            Assert.Equal(0, alicesRow.PlayCount);
            Assert.Equal(0, alicesRow.PlaybackPositionTicks);
            Assert.Null(alicesRow.LastPlayedDate);

            Assert.Equal(7_000, (await StoredAsync(bobId, _film))!.PlaybackPositionTicks);
        }
    }

    /// <summary>
    /// Without a date a first mark counts one play and records the time of the request; a second
    /// mark without a date neither counts again nor moves the date.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task MarkPlayedWithoutDate_CountsOnceAndKeepsTheFirstDate()
    {
        var (carol, carolId) = await SignInAsync("played-carol-" + _suffix);
        using (carol)
        {
            var before = DateTime.UtcNow.AddSeconds(-1);
            using (var first = await carol.PostAsync("UserPlayedItems/" + _film, null, TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            }

            var row = (await StoredAsync(carolId, _film))!;
            Assert.True(row.Played);
            Assert.Equal(1, row.PlayCount);
            var firstDate = DateTime.SpecifyKind(row.LastPlayedDate!.Value, DateTimeKind.Utc);
            Assert.InRange(firstDate, before, DateTime.UtcNow.AddSeconds(1));

            using (var second = await carol.PostAsync("UserPlayedItems/" + _film, null, TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            }

            row = (await StoredAsync(carolId, _film))!;
            Assert.Equal(1, row.PlayCount);
            Assert.Equal(firstDate, DateTime.SpecifyKind(row.LastPlayedDate!.Value, DateTimeKind.Utc));
        }
    }

    /// <summary>
    /// A series marks its real episodes and not its missing ones, whatever the user displays; the
    /// series itself gets no row. Unplayed resets the same episodes.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task Series_MarksItsRealEpisodes()
    {
        var (dave, daveId) = await SignInAsync("played-dave-" + _suffix, displayMissingEpisodes: true);
        using (dave)
        {
            using (var played = await dave.PostAsync("UserPlayedItems/" + _series, null, TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, played.StatusCode);
            }

            Assert.True((await StoredAsync(daveId, _episode1))?.Played);
            Assert.True((await StoredAsync(daveId, _episode2))?.Played);
            Assert.Null(await StoredAsync(daveId, _missingAired));
            Assert.Null(await StoredAsync(daveId, _missingUnaired));
            Assert.Null(await StoredAsync(daveId, _series));

            using (var unplayed = await dave.DeleteAsync("UserPlayedItems/" + _series, TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, unplayed.StatusCode);
            }

            Assert.False((await StoredAsync(daveId, _episode1))?.Played);
            Assert.False((await StoredAsync(daveId, _episode2))?.Played);
        }
    }

    /// <summary>
    /// A folder marks every leaf under it. A user who displays missing episodes marks the aired
    /// ones too, never an unaired one; a user who does not, marks only real items.
    /// </summary>
    /// <param name="displayMissingEpisodes">The user's display setting.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Theory(Timeout = 120_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Folder_MarksItsLeaves_PerTheUsersMissingEpisodeSetting(bool displayMissingEpisodes)
    {
        var (erin, erinId) = await SignInAsync("played-erin-" + displayMissingEpisodes + "-" + _suffix, displayMissingEpisodes);
        using (erin)
        {
            using (var played = await erin.PostAsync("UserPlayedItems/" + _openFolder, null, TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, played.StatusCode);
            }

            foreach (var leaf in new[] { _film, _episode1, _episode2 })
            {
                Assert.True((await StoredAsync(erinId, leaf))?.Played, "a real leaf under the folder is played");
            }

            Assert.Equal(displayMissingEpisodes ? true : null, (await StoredAsync(erinId, _missingAired))?.Played);
            Assert.Null(await StoredAsync(erinId, _missingUnaired));
            Assert.Null(await StoredAsync(erinId, _series));
            Assert.Null(await StoredAsync(erinId, _withheldFilm));
        }
    }

    /// <summary>
    /// An item in a library the user may not see is refused as unknown, and nothing is written.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 120_000)]
    public async Task WithheldItem_IsRefusedWithoutAWrite()
    {
        var (frank, frankId) = await SignInAsync("played-frank-" + _suffix);
        using (frank)
        {
            using (var played = await frank.PostAsync("UserPlayedItems/" + _withheldFilm + "?datePlayed=" + Uri.EscapeDataString(ReportedDate.ToString("o")), null, TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.NotFound, played.StatusCode);
            }

            using (var unplayed = await frank.DeleteAsync("UserPlayedItems/" + _withheldFilm, TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.NotFound, unplayed.StatusCode);
            }

            Assert.Null(await StoredAsync(frankId, _withheldFilm));
        }
    }

    private async Task<Tesserafin.Data.Entities.UserData?> StoredAsync(Guid userId, Guid itemId)
    {
        var contexts = _factory.Services.GetRequiredService<IDbContextFactory<TesserafinDbContext>>();
        var context = await contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);
        await using (context.ConfigureAwait(false))
        {
            // One row per user data key of the item (an episode has several); a save writes them alike.
            var rows = await context.UserData.AsNoTracking()
                .Where(d => d.UserId.Equals(userId) && d.ItemId.Equals(itemId))
                .ToListAsync(TestContext.Current.CancellationToken);
            if (rows.Count == 0)
            {
                return null;
            }

            Assert.Single(rows.Select(d => (d.Played, d.PlayCount, d.PlaybackPositionTicks, d.LastPlayedDate)).Distinct());
            return rows[0];
        }
    }

    private static async Task PostUserDataAsync(HttpClient client, Guid itemId, UpdateUserItemDataDto data)
    {
        using var response = await client.PostAsJsonAsync("UserItems/" + itemId + "/UserData", data, JsonDefaults.Options, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private Guid SeedFilm(string file, Guid folder)
    {
        File.WriteAllText(file, file);
        return LibraryItemSeeder.SeedVideo(
            _factory.Services.GetRequiredService<IItemWriteCommands>(),
            _factory.Services.GetRequiredService<IMediaStreamRepository>(),
            file,
            "mp4",
            [
                new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264", Width = 320, Height = 240, IsDefault = true },
                new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "aac", Channels = 2, IsDefault = true },
            ],
            Path.GetFileNameWithoutExtension(file),
            parentId: folder);
    }

    private Guid SeedEpisode(Series series, int number, bool virtualItem, DateTime premiere)
    {
        var episode = new Episode
        {
            Id = Guid.NewGuid(),
            Name = series.Name + " e" + number,
            Path = virtualItem ? null : Path.Combine(series.Path, series.Name + "-s01e0" + number + ".mp4"),
            ParentId = series.Id,
            SeriesId = series.Id,
            SeriesName = series.Name,
            SeriesPresentationUniqueKey = series.PresentationUniqueKey,
            ParentIndexNumber = 1,
            IndexNumber = number,
            IsVirtualItem = virtualItem,
            PremiereDate = premiere,
            DateCreated = DateTime.UtcNow,
            DateModified = DateTime.UtcNow,
        };
        episode.PresentationUniqueKey = episode.CreatePresentationUniqueKey();
        _factory.Services.GetRequiredService<IItemWriteCommands>().CreateItems([episode], null, CancellationToken.None);
        return episode.Id;
    }

    private async Task<string> AddLibraryAsync(string name)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_workDir, name)).FullName;
        using var added = await _admin.PostAsJsonAsync("Library/VirtualFolders?collectionType=movies&refreshLibrary=false&name=" + name + "&paths=" + Uri.EscapeDataString(directory), new { }, JsonDefaults.Options, TestContext.Current.CancellationToken);
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
        Assert.IsType<CollectionFolder>(library.GetItemById(libraryId)).PhysicalFolderIds = [folder.Id];
        return folder.Id;
    }

    private async Task<Dictionary<string, Guid>> LibrariesAsync()
    {
        using var document = JsonDocument.Parse(await _admin.GetStringAsync("Library/VirtualFolders", TestContext.Current.CancellationToken));
        return document.RootElement.EnumerateArray().ToDictionary(f => f.GetProperty("Name").GetString()!, f => Guid.Parse(f.GetProperty("ItemId").GetString()!));
    }

    /// <summary>
    /// A new user enabled on the open library only.
    /// </summary>
    private async Task<(HttpClient Client, Guid UserId)> SignInAsync(string name, bool displayMissingEpisodes = false)
    {
        const string Password = "played-password";
        UserDto created;
        using (var response = await _admin.PostAsJsonAsync("/Users/New", new { Name = name, Password }, JsonDefaults.Options, TestContext.Current.CancellationToken))
        {
            response.EnsureSuccessStatusCode();
            created = (await response.Content.ReadFromJsonAsync<UserDto>(JsonDefaults.Options, TestContext.Current.CancellationToken))!;
        }

        var user = (await _admin.GetFromJsonAsync<UserDto>("Users/" + created.Id, JsonDefaults.Options, TestContext.Current.CancellationToken))!;
        user.Policy!.EnableAllFolders = false;
        user.Policy.EnabledFolders = [_openLibrary];
        using (var applied = await _admin.PostAsJsonAsync("Users/" + created.Id + "/Policy", user.Policy, JsonDefaults.Options, TestContext.Current.CancellationToken))
        {
            applied.EnsureSuccessStatusCode();
        }

        user.Configuration!.DisplayMissingEpisodes = displayMissingEpisodes;
        using (var configured = await _admin.PostAsJsonAsync("Users/" + created.Id + "/Configuration", user.Configuration, JsonDefaults.Options, TestContext.Current.CancellationToken))
        {
            configured.EnsureSuccessStatusCode();
        }

        using var anonymous = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, MediaBoundaryFixture.AuthorizationHeader(name + "-device", null));
        request.Content = JsonContent.Create(new { Username = name, Pw = Password }, options: JsonDefaults.Options);
        using var authenticated = await anonymous.SendAsync(request, TestContext.Current.CancellationToken);
        authenticated.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await authenticated.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            AuthHelper.AuthHeaderName,
            MediaBoundaryFixture.AuthorizationHeader(name + "-device", document.RootElement.GetProperty("AccessToken").GetString()));
        return (client, created.Id);
    }
}
