using System;
using System.IO;
using System.Linq;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Database.Implementations;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Xunit;

namespace Tesserafin.Server.Integration.Tests;

public sealed class OwnedExtrasEndToEndTests(TesserafinApplicationFactory factory) : IClassFixture<TesserafinApplicationFactory>
{
    [Fact]
    public async Task Extras_RejectsFailedSave_Retries_RefreshesAndReconcilesCatalogue_WithoutDeletingMediaOrVersions()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var writes = factory.Services.GetRequiredService<IItemWriteCommands>();
        var resolution = factory.Services.GetRequiredService<IItemResolutionService>();
        var repository = factory.Services.GetRequiredService<IItemRepository>();
        var lookup = factory.Services.GetRequiredService<IItemLookupService>();
        var files = factory.Services.GetRequiredService<IFileSystem>();
        var deletions = factory.Services.GetRequiredService<IItemDeletionCommands>();
        var databases = factory.Services.GetRequiredService<IDbContextFactory<TesserafinDbContext>>();
        var root = Directory.CreateTempSubdirectory("owned-extras-host-").FullName;
        var ownerPath = Path.Combine(root, "Owner.mkv");
        var trailerPath = Path.Combine(root, "Owner-trailer.mkv");
        var featurePath = Path.Combine(root, "Owner-behindthescenes.mkv");
        var alternatePath = Path.Combine(root, "Owner-alt.mkv");
        var paths = new[] { ownerPath, trailerPath, featurePath, alternatePath };
        var bytes = new byte[] { 7, 11, 13, 17 };
        foreach (var path in paths)
        {
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        }

        try
        {
            var owner = new Movie
            {
                Id = resolution.GetNewItemId(ownerPath, typeof(Movie)),
                Name = "Owner",
                Path = ownerPath,
                Genres = ["owned bonus genre"],
                Overview = "Owner metadata",
                IsInMixedFolder = true
            };
            writes.CreateItem(owner, null);
            var alternate = new Video { Id = resolution.GetNewItemId(alternatePath, typeof(Video)), Path = alternatePath, Name = "alternate", OwnerId = owner.Id };
            writes.CreateItem(alternate, null);
            var trailerId = resolution.GetNewItemId(trailerPath, typeof(Trailer));
            await using (var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken))
            {
                await database.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_extra BEFORE INSERT ON BaseItems WHEN NEW.Path LIKE '%owned-extras-host-%' AND NEW.Path LIKE '%Owner-trailer.mkv' BEGIN SELECT RAISE(ABORT, 'extra save rejected'); END;", TestContext.Current.CancellationToken);
            }

            try
            {
                var error = await Assert.ThrowsAnyAsync<Exception>(() => RefreshAsync(trailerPath));
                Assert.Contains("extra save rejected", error.ToString(), StringComparison.Ordinal);
                Assert.Null(lookup.GetItemById(trailerId));
                Assert.Null(repository.RetrieveItem(trailerId));
            }
            finally
            {
                await using var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken);
                await database.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_extra;", TestContext.Current.CancellationToken);
            }

            Assert.True(await RefreshAsync(trailerPath));
            var trailer = Assert.IsType<Trailer>(lookup.GetItemById(trailerId));
            Assert.Equal(owner.Id, trailer.OwnerId);
            Assert.Equal(Guid.Empty, trailer.ParentId);
            Assert.Equal(owner.Genres, trailer.Genres);
            Assert.Equal(owner.Overview, trailer.Overview);
            var trailers = await client.GetFromJsonAsync<BaseItemDto[]>($"Items/{owner.Id}/LocalTrailers", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal(trailer.Id, Assert.Single(Assert.IsType<BaseItemDto[]>(trailers)).Id);
            Assert.False(await RefreshAsync(trailerPath));
            Assert.True(await RefreshAsync(featurePath));
            Assert.Null(repository.RetrieveItem(trailer.Id));
            Assert.Null(lookup.GetItemById(trailer.Id));
            var owned = repository.GetItemList(new InternalItemsQuery { OwnerIds = [owner.Id] });
            Assert.Contains(owned, item => item.Id.Equals(alternate.Id) && !item.ExtraType.HasValue);
            var feature = Assert.Single(owned, item => item.ExtraType == ExtraType.BehindTheScenes);
            var features = await client.GetFromJsonAsync<BaseItemDto[]>($"Items/{owner.Id}/SpecialFeatures", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal(feature.Id, Assert.Single(Assert.IsType<BaseItemDto[]>(features)).Id);
            Assert.Equal(owner.Id, feature.OwnerId);
            foreach (var path in paths)
            {
                Assert.Equal(bytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            }

            Task<bool> RefreshAsync(string path) => writes.RefreshExtrasAsync(
                owner,
                deletions,
                new MetadataRefreshOptions(new DirectoryService(files))
                {
                    MetadataRefreshMode = MetadataRefreshMode.None,
                    ImageRefreshMode = MetadataRefreshMode.None
                },
                [files.GetFileInfo(path)],
                TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
