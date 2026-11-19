using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http.Json;
using System.Security;
using System.Threading;
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

public sealed class FolderChildReconciliationEndToEndTests(TesserafinApplicationFactory factory) : IClassFixture<TesserafinApplicationFactory>
{
    [Fact]
    public async Task CompletedDiscovery_RejectsPartialReads_RetriesFailedUpdates_ReplacesTypesAndReattachesHistory_WithoutDeletingMedia()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var userId = (await AuthHelper.GetUserDtoAsync(client)).Id;
        var writes = factory.Services.GetRequiredService<IItemWriteCommands>();
        var scanContext = factory.Services.GetRequiredService<LibraryScanContext>();
        var resolution = factory.Services.GetRequiredService<IItemResolutionService>();
        var repository = factory.Services.GetRequiredService<IItemRepository>();
        var lookup = factory.Services.GetRequiredService<IItemLookupService>();
        var files = factory.Services.GetRequiredService<IFileSystem>();
        var databases = factory.Services.GetRequiredService<IDbContextFactory<TesserafinDbContext>>();
        var root = Directory.CreateTempSubdirectory("folder-children-host-").FullName;
        var path = Path.Combine(root, "Existing.mkv");
        var potentialPath = Path.Combine(root, "Potential.mkv");
        var bytes = new byte[] { 53, 59, 61, 67 };
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(potentialPath, bytes, TestContext.Current.CancellationToken);
        try
        {
            var folder = new Folder { Id = resolution.GetNewItemId(root, typeof(Folder)), Path = root, Name = "child reconciliation" };
            writes.CreateItem(folder, null);
            var existing = new Movie
            {
                Id = resolution.GetNewItemId(path, typeof(Movie)),
                ParentId = folder.Id,
                Path = path,
                Name = "child reconciliation existing",
                IsInMixedFolder = true,
                Container = "mkv",
                VideoType = VideoType.VideoFile,
                DateLastRefreshed = DateTime.UtcNow
            };
            const string historyKey = "tt-child-reconciliation";
            existing.ProviderIds["Imdb"] = historyKey;
            writes.CreateItem(existing, folder);
            var potential = new Movie { Id = resolution.GetNewItemId(potentialPath, typeof(Movie)), Path = potentialPath, IsInMixedFolder = true };
            int countBefore;
            await using (var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken))
            {
                countBefore = await database.BaseItems.CountAsync(TestContext.Current.CancellationToken);
            }

            foreach (var error in new Exception[] { new IOException("discovery interrupted"), new SecurityException("discovery refused") })
            {
                await ScanAsync(() => IncompleteDiscovery(error));
                Assert.NotNull(repository.RetrieveItem(existing.Id));
                Assert.Null(repository.RetrieveItem(potential.Id));
                await using var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken);
                Assert.Equal(countBefore, await database.BaseItems.CountAsync(TestContext.Current.CancellationToken));
            }

            using (var cancellation = new CancellationTokenSource())
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScanAsync(() => IncompleteDiscovery(new OperationCanceledException(cancellation.Token))));
            }

            var resolved = (Movie)existing.CopyForWrite();
            resolved.IsInMixedFolder = false;
            await using (var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken))
            {
                await database.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_resolved_child BEFORE UPDATE OF IsInMixedFolder ON BaseItems WHEN OLD.Name = 'child reconciliation existing' AND NEW.IsInMixedFolder = 0 BEGIN SELECT RAISE(ABORT, 'resolved child rejected'); END;", TestContext.Current.CancellationToken);
            }

            try
            {
                var failure = await Assert.ThrowsAnyAsync<Exception>(() => ScanAsync(() => [resolved]));
                Assert.Contains("resolved child rejected", failure.ToString(), StringComparison.Ordinal);
                Assert.True(repository.RetrieveItem(existing.Id).IsInMixedFolder);
                Assert.True(lookup.GetItemById(existing.Id)!.IsInMixedFolder);
            }
            finally
            {
                await using var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken);
                await database.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_resolved_child;", TestContext.Current.CancellationToken);
            }

            await ScanAsync(() => [resolved]);
            Assert.False(repository.RetrieveItem(existing.Id).IsInMixedFolder);
            await using (var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken))
            {
                database.UserData.Add(new Tesserafin.Data.Entities.UserData
                {
                    ItemId = existing.Id,
                    Item = null,
                    UserId = userId,
                    User = null,
                    CustomDataKey = historyKey,
                    PlaybackPositionTicks = 11223344,
                    PlayCount = 4,
                    IsFavorite = true
                });
                await database.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var retyped = new Video
            {
                Id = resolution.GetNewItemId(path, typeof(Video)),
                Path = path,
                Name = "retyped child",
                IsInMixedFolder = true,
                Container = "mkv",
                VideoType = VideoType.VideoFile,
                DateLastRefreshed = DateTime.UtcNow
            };
            retyped.ProviderIds["Imdb"] = historyKey;
            await ScanAsync(() => [retyped, retyped]);
            Assert.Null(repository.RetrieveItem(existing.Id));
            Assert.Null(lookup.GetItemById(existing.Id));
            Assert.IsType<Video>(repository.RetrieveItem(retyped.Id));
            Assert.Equal(Guid.Empty, retyped.ParentId);
            var detail = await client.GetFromJsonAsync<BaseItemDto>($"Items/{retyped.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(detail);
            Assert.Equal(retyped.Id, detail.Id);
            await using (var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken))
            {
                var history = await database.UserData.SingleAsync(row => row.CustomDataKey == historyKey && row.UserId.Equals(userId), TestContext.Current.CancellationToken);
                Assert.Equal(retyped.Id, history.ItemId);
                Assert.Equal(11223344, history.PlaybackPositionTicks);
                Assert.Equal(4, history.PlayCount);
                Assert.True(history.IsFavorite);
                Assert.Null(history.RetentionDate);
            }

            await ScanAsync(() => []);
            Assert.Null(repository.RetrieveItem(retyped.Id));
            Assert.Null(lookup.GetItemById(retyped.Id));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(potentialPath, TestContext.Current.CancellationToken));

            IEnumerable<BaseItem> IncompleteDiscovery(Exception error)
            {
                yield return potential;
                throw error;
            }

            Task ScanAsync(Func<IEnumerable<BaseItem>> discover) => new ControlledDiscoveryFolder { Id = folder.Id, Path = root, Discover = discover }.ValidateChildren(
                scanContext,
                new Progress<double>(),
                new MetadataRefreshOptions(new DirectoryService(files)) { MetadataRefreshMode = MetadataRefreshMode.None, ImageRefreshMode = MetadataRefreshMode.None },
                recursive: false,
                cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class ControlledDiscoveryFolder : Folder
    {
        public required Func<IEnumerable<BaseItem>> Discover { get; init; }

        public override string GetClientTypeName() => nameof(Folder);

        protected override IEnumerable<BaseItem> GetNonCachedChildren(IItemResolutionService resolution, IDirectoryService directoryService, CancellationToken cancellationToken) => Discover();
    }
}
