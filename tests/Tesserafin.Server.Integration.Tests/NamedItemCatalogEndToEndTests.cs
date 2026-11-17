using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Common;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Database.Implementations;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Querying;
using Tesserafin.Server.Core.Library.Validators;
using Xunit;

namespace Tesserafin.Server.Integration.Tests;

public sealed class NamedItemCatalogEndToEndTests(TesserafinApplicationFactory factory) : IClassFixture<TesserafinApplicationFactory>
{
    private static string? _accessToken;

    [Fact]
    public async Task HttpReadsAndUnknownFilters_DoNotMaterializeNames_ExplicitWritesBecomeVisible()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));
        var catalog = factory.Services.GetRequiredService<INamedItemCatalog>();
        var writes = factory.Services.GetRequiredService<IItemWriteCommands>();
        var lookup = factory.Services.GetRequiredService<IItemLookupService>();
        var changes = factory.Services.GetRequiredService<IItemChangeEvents>();
        var configuration = factory.Services.GetRequiredService<IServerConfigurationManager>();
        var mark = Guid.NewGuid().ToString("N");
        var artistName = "catalogartist" + mark;
        var studioName = "catalogstudio" + mark;
        var genreName = "cataloggenre" + mark;
        var musicGenreName = "catalogmusic" + mark;
        const int year = 2999;
        var film = new Movie { Id = Guid.NewGuid(), Name = "catalogue film " + mark, Studios = [studioName], Genres = [genreName] };
        writes.CreateItems([film], null, TestContext.Current.CancellationToken);
        var added = 0;
        changes.ItemAdded += Added;
        try
        {
            foreach (var route in new[] { $"Artists/{artistName}", $"Studios/{studioName}", $"Genres/{genreName}", $"MusicGenres/{musicGenreName}", $"Years/{year}", $"Artists/{artistName}/Images/Primary", $"Genres/{genreName}/Images/Primary" })
            {
                using var response = await client.GetAsync(route, TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }

            var dto = factory.Services.GetRequiredService<IDtoService>().GetBaseItemDto(film, new DtoOptions(true));
            Assert.Equal(catalog.GetId<Studio>(studioName), Assert.Single(dto.Studios).Id);
            Assert.Equal(catalog.GetId<Genre>(genreName), Assert.Single(dto.GenreItems).Id);
            Assert.Null(lookup.GetItemById(catalog.GetId<MusicArtist>(artistName)));
            Assert.Null(lookup.GetItemById(catalog.GetId<Studio>(studioName)));
            Assert.Null(lookup.GetItemById(catalog.GetId<Genre>(genreName)));
            Assert.Null(lookup.GetItemById(catalog.GetId<MusicGenre>(musicGenreName)));
            Assert.Null(lookup.GetItemById(catalog.GetId<Year>(year.ToString(CultureInfo.InvariantCulture))));
            Assert.False(Directory.Exists(MusicArtist.GetPath(artistName, factory.Services.GetRequiredService<Tesserafin.Model.IO.IFileSystem>(), configuration.ApplicationPaths.ArtistsPath)));
            Assert.False(Directory.Exists(Studio.GetPath(studioName, factory.Services.GetRequiredService<Tesserafin.Model.IO.IFileSystem>(), configuration.ApplicationPaths.StudioPath)));
            Assert.Equal(0, added);

            var control = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>($"Items?ids={film.Id}&recursive=true", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(control);
            Assert.Equal(film.Id, Assert.Single(control.Items).Id);
            foreach (var filter in new[] { "artists=" + artistName, "studios=" + studioName })
            {
                var filtered = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>($"Items?ids={film.Id}&recursive=true&{filter}", JsonDefaults.Options, TestContext.Current.CancellationToken);
                Assert.NotNull(filtered);
                Assert.Empty(filtered.Items);
            }

            Assert.Equal(0, added);
            var artist = writes.GetOrCreateNamedItem<MusicArtist>(artistName, TestContext.Current.CancellationToken);
            var studio = writes.GetOrCreateNamedItem<Studio>(studioName, TestContext.Current.CancellationToken);
            var genre = writes.GetOrCreateNamedItem<Genre>(genreName, TestContext.Current.CancellationToken);
            var music = writes.GetOrCreateNamedItem<MusicGenre>(musicGenreName, TestContext.Current.CancellationToken);
            var yearItem = writes.GetOrCreateNamedItem<Year>(year.ToString(CultureInfo.InvariantCulture), TestContext.Current.CancellationToken);
            foreach (var (route, id) in new[] { ($"Artists/{artistName}", artist.Id), ($"Studios/{studioName}", studio.Id), ($"Genres/{genreName}", genre.Id), ($"MusicGenres/{musicGenreName}", music.Id), ($"Years/{year}", yearItem.Id) })
            {
                var item = await client.GetFromJsonAsync<BaseItemDto>(route, JsonDefaults.Options, TestContext.Current.CancellationToken);
                Assert.NotNull(item);
                Assert.Equal(id, item.Id);
            }

            Assert.Equal(5, added);
            Assert.Equal(studio.Id, writes.GetOrCreateNamedItem<Studio>(studioName, TestContext.Current.CancellationToken).Id);
            Assert.Equal(5, added);
        }
        finally
        {
            changes.ItemAdded -= Added;
        }

        void Added(object? sender, ItemChangeEventArgs args)
        {
            if (new[] { artistName, studioName, genreName, musicGenreName, year.ToString(CultureInfo.InvariantCulture) }.Contains(args.Item.Name, StringComparer.Ordinal))
            {
                added++;
            }
        }
    }

    [Fact]
    public async Task NamedWrite_FailedSqlCanRetry_AndConcurrentCreationPublishesOnce()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));
        var catalog = factory.Services.GetRequiredService<INamedItemCatalog>();
        var writes = factory.Services.GetRequiredService<IItemWriteCommands>();
        var changes = factory.Services.GetRequiredService<IItemChangeEvents>();
        var databases = factory.Services.GetRequiredService<IDbContextFactory<TesserafinDbContext>>();
        var name = "namedreject" + Guid.NewGuid().ToString("N");
        var additions = 0;
        changes.ItemAdded += Added;
        try
        {
            await using (var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken))
            {
                await database.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_named_creation BEFORE INSERT ON BaseItems WHEN NEW.Name LIKE 'namedreject%' BEGIN SELECT RAISE(ABORT, 'named creation rejected'); END;", TestContext.Current.CancellationToken);
            }

            try
            {
                var failure = Assert.ThrowsAny<Exception>(() => writes.GetOrCreateNamedItem<Genre>(name, TestContext.Current.CancellationToken));
                Assert.Contains("named creation rejected", failure.ToString(), StringComparison.Ordinal);
                Assert.Null(catalog.Find<Genre>(name));
                Assert.Equal(0, additions);
            }
            finally
            {
                await using var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken);
                await database.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_named_creation;", TestContext.Current.CancellationToken);
            }

            var cancellationToken = TestContext.Current.CancellationToken;
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => writes.GetOrCreateNamedItem<Genre>(name, cancellationToken), cancellationToken)));
            var id = catalog.GetId<Genre>(name);
            Assert.All(results, item => Assert.Equal(id, item.Id));
            Assert.Equal(id, catalog.Find<Genre>(name)!.Id);
            Assert.Equal(1, additions);
            await using var finalDatabase = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, await finalDatabase.BaseItems.CountAsync(item => item.Id.Equals(id), TestContext.Current.CancellationToken));
        }
        finally
        {
            changes.ItemAdded -= Added;
        }

        void Added(object? sender, ItemChangeEventArgs args)
        {
            if (string.Equals(args.Item.Name, name, StringComparison.Ordinal))
            {
                additions++;
            }
        }
    }

    [Fact]
    public async Task AlbumArtistReads_DoNotCreate_RealFoldersWin_AndPostScanMaterializesProductionYears()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));
        var catalog = factory.Services.GetRequiredService<INamedItemCatalog>();
        var writes = factory.Services.GetRequiredService<IItemWriteCommands>();
        var repository = factory.Services.GetRequiredService<IItemRepository>();
        var mark = Guid.NewGuid().ToString("N");
        var name = "albumartist" + mark;
        var album = new MusicAlbum { Id = Guid.NewGuid(), Name = "album", AlbumArtists = [name] };

        Assert.Null(album.MusicArtist);
        Assert.Null(album.GetMusicArtist());
        Assert.Null(catalog.Find<MusicArtist>(name));
        var named = writes.GetOrCreateNamedItem<MusicArtist>(name, TestContext.Current.CancellationToken);
        Assert.Equal(named.Id, Assert.IsType<MusicArtist>(album.MusicArtist).Id);
        var folder = new MusicArtist { Id = Guid.NewGuid(), Name = name, ParentId = Guid.NewGuid(), Path = Path.Combine(Path.GetTempPath(), mark) };
        writes.CreateItems([folder], null, TestContext.Current.CancellationToken);
        Assert.Equal(folder.Id, catalog.Find<MusicArtist>(name)!.Id);
        Assert.Equal(folder.Id, catalog.GetId<MusicArtist>(name));
        Assert.Equal(folder.Id, writes.GetOrCreateNamedItem<MusicArtist>(name, TestContext.Current.CancellationToken).Id);
        Assert.Equal(folder.Id, Assert.IsType<MusicArtist>(album.MusicArtist).Id);

        const int year = 2998;
        writes.CreateItems(
            [new Movie { Id = Guid.NewGuid(), Name = "year first " + mark, ProductionYear = year },
             new Movie { Id = Guid.NewGuid(), Name = "year second " + mark, ProductionYear = year },
             new Movie { Id = Guid.NewGuid(), Name = "invalid year " + mark, ProductionYear = -1 }],
            null,
            TestContext.Current.CancellationToken);
        var years = await repository.GetProductionYearsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, years.Count(value => value == year));
        Assert.DoesNotContain(-1, years);
        Assert.Contains(typeof(YearsPostScanTask), factory.Services.GetRequiredService<IApplicationHost>().GetExportTypes<ILibraryPostScanTask>());
        await new YearsPostScanTask(repository, writes).Run(new Progress<double>(), TestContext.Current.CancellationToken);
        Assert.NotNull(catalog.Find<Year>(year.ToString(CultureInfo.InvariantCulture)));
    }
}
