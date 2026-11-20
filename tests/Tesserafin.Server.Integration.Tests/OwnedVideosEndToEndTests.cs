using System;
using System.IO;
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

public sealed class OwnedVideosEndToEndTests(TesserafinApplicationFactory factory) : IClassFixture<TesserafinApplicationFactory>
{
    [Fact]
    public async Task OwnedVersions_RejectFailedCreation_RetryThroughEntityRefresh_ExposeMultipartSourcesAndPreserveMedia()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var writes = factory.Services.GetRequiredService<IItemWriteCommands>();
        var metadata = factory.Services.GetRequiredService<IProviderManager>();
        var deletions = factory.Services.GetRequiredService<IItemDeletionCommands>();
        var resolution = factory.Services.GetRequiredService<IItemResolutionService>();
        var repository = factory.Services.GetRequiredService<IItemRepository>();
        var lookup = factory.Services.GetRequiredService<IItemLookupService>();
        var linked = factory.Services.GetRequiredService<ILinkedChildrenService>();
        var files = factory.Services.GetRequiredService<IFileSystem>();
        var databases = factory.Services.GetRequiredService<IDbContextFactory<TesserafinDbContext>>();
        var root = Directory.CreateTempSubdirectory("owned-videos-host-").FullName;
        var primaryPath = Path.Combine(root, "Feature - 2160p.mkv");
        var alternatePath = Path.Combine(root, "Feature - 1080p cd1.mkv");
        var secondPath = Path.Combine(root, "Feature - 1080p cd2.mkv");
        var partPath = Path.Combine(root, "Primary-part2.mkv");
        var paths = new[] { primaryPath, alternatePath, secondPath, partPath };
        var bytes = new byte[] { 19, 23, 29, 31 };
        foreach (var path in paths)
        {
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        }

        try
        {
            var folder = new Folder { Id = resolution.GetNewItemId(root, typeof(Folder)), Path = root, Name = "owned versions" };
            writes.CreateItem(folder, null);
            var owner = new Movie
            {
                Id = resolution.GetNewItemId(primaryPath, typeof(Movie)),
                Path = primaryPath,
                Name = "Feature",
                ParentId = folder.Id,
                IsInMixedFolder = true,
                VideoType = VideoType.VideoFile,
                Container = "mkv",
                Genres = ["version genre"],
                Overview = "primary description"
            };
            writes.CreateItem(owner, folder);
            var obsolete = new Video { Id = resolution.GetNewItemId(alternatePath, typeof(Video)), Path = alternatePath, OwnerId = owner.Id };
            writes.CreateItem(obsolete, folder);
            owner.AdditionalParts = [partPath, partPath];
            owner.LocalAlternateVersions = [alternatePath, alternatePath, Path.Combine(root, "missing.mkv")];
            var alternateId = resolution.GetNewItemId(alternatePath, typeof(Movie));
            await using (var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken))
            {
                await database.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_owned_version BEFORE INSERT ON BaseItems WHEN NEW.Path LIKE '%owned-videos-host-%' AND NEW.Path LIKE '%1080p cd1.mkv' BEGIN SELECT RAISE(ABORT, 'owned version rejected'); END;", TestContext.Current.CancellationToken);
            }

            try
            {
                var error = await Assert.ThrowsAnyAsync<Exception>(() => writes.RefreshOwnedVideosAsync(owner, metadata, deletions, Options(), TestContext.Current.CancellationToken));
                Assert.Contains("owned version rejected", error.ToString(), StringComparison.Ordinal);
                Assert.Null(repository.RetrieveItem(alternateId));
                Assert.Null(lookup.GetItemById(alternateId));
                Assert.Equal(3, owner.LocalAlternateVersions.Length);
                Assert.Empty(linked.GetLinkedChildrenIds(owner.Id, (int)LinkedChildType.LocalAlternateVersion));
            }
            finally
            {
                await using var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken);
                await database.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_owned_version;", TestContext.Current.CancellationToken);
            }

            await owner.RefreshMetadata(deletions, writes, metadata, Options(), TestContext.Current.CancellationToken);
            Assert.Equal(alternatePath, Assert.Single(owner.LocalAlternateVersions));
            var alternate = Assert.IsType<Movie>(repository.RetrieveItem(alternateId));
            Assert.Equal(owner.Id, alternate.OwnerId);
            Assert.Equal(owner.Id, alternate.PrimaryVersionId);
            Assert.Equal(secondPath, Assert.Single(alternate.AdditionalParts));
            Assert.Null(repository.RetrieveItem(obsolete.Id));
            Assert.Equal(alternateId, Assert.Single(linked.GetLinkedChildrenIds(owner.Id, (int)LinkedChildType.LocalAlternateVersion)));
            var part = Assert.IsType<Video>(repository.RetrieveItem(resolution.GetNewItemId(partPath, typeof(Video))));
            Assert.Equal(owner.Id, part.OwnerId);
            Assert.Equal(owner.Overview, part.Overview);
            Assert.False(await writes.RefreshOwnedVideosAsync(owner, metadata, deletions, Options(), TestContext.Current.CancellationToken));

            var detail = await client.GetFromJsonAsync<BaseItemDto>($"Items/{owner.Id}?fields=MediaSources", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(detail);
            Assert.Contains(detail.MediaSources, source => string.Equals(source.Id, alternateId.ToString("N"), StringComparison.OrdinalIgnoreCase));
            foreach (var path in paths)
            {
                Assert.Equal(bytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            }

            MetadataRefreshOptions Options() => new(new DirectoryService(files))
            {
                MetadataRefreshMode = MetadataRefreshMode.None,
                ImageRefreshMode = MetadataRefreshMode.None
            };
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
