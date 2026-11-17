using System;
using System.Collections.Generic;
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
using Tesserafin.Controller.Playlists;
using Tesserafin.Controller.Providers;
using Tesserafin.Database.Implementations;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Xunit;

namespace Tesserafin.Server.Integration.Tests;

public sealed class PrimaryVersionTransferEndToEndTests(TesserafinApplicationFactory factory) : IClassFixture<TesserafinApplicationFactory>
{
    [Fact]
    public async Task FolderScan_FailedTransferRetries_DemotesAndReplacesPrimary_PreservingLinkedVersionsReferencesAndMedia()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var writes = factory.Services.GetRequiredService<IItemWriteCommands>();
        var deletions = factory.Services.GetRequiredService<IItemDeletionCommands>();
        var images = factory.Services.GetRequiredService<IItemImageService>();
        var resolution = factory.Services.GetRequiredService<IItemResolutionService>();
        var repository = factory.Services.GetRequiredService<IItemRepository>();
        var lookup = factory.Services.GetRequiredService<IItemLookupService>();
        var links = factory.Services.GetRequiredService<ILinkedChildrenService>();
        var files = factory.Services.GetRequiredService<IFileSystem>();
        var databases = factory.Services.GetRequiredService<IDbContextFactory<TesserafinDbContext>>();
        var root = Directory.CreateTempSubdirectory("primary-transfer-host-").FullName;
        var names = new[] { "previous", "replacement", "local", "linked", "final" };
        var paths = names.ToDictionary(name => name, name => Path.Combine(root, name + ".mkv"));
        var bytes = new byte[] { 37, 41, 43, 47 };
        foreach (var path in paths.Values)
        {
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        }

        try
        {
            var folder = new Folder { Id = resolution.GetNewItemId(root, typeof(Folder)), Path = root, Name = "transfer folder" };
            writes.CreateItem(folder, null);
            var previous = Movie("previous");
            var local = Movie("local");
            var linked = Movie("linked");
            local.OwnerId = previous.Id;
            local.SetPrimaryVersionId(previous.Id);
            linked.ParentId = Guid.Empty;
            linked.SetPrimaryVersionId(previous.Id);
            previous.LocalAlternateVersions = [local.Path];
            previous.LinkedAlternateVersions = [new LinkedChild { ItemId = linked.Id, Type = LinkedChildType.LinkedAlternateVersion }];
            writes.CreateItems([previous, local, linked], folder, TestContext.Current.CancellationToken);
            var originalLocal = Assert.IsType<Movie>(lookup.GetItemById(local.Id));
            var playlist = new Playlist { Id = Guid.NewGuid(), Name = "transfer references" };
            writes.CreateItem(playlist, null);
            writes.UpsertLinkedChild(playlist.Id, previous.Id, LinkedChildType.Manual, TestContext.Current.CancellationToken);
            var replacement = Movie("replacement");
            replacement.LocalAlternateVersions = [previous.Path, local.Path];
            var scan = new ControlledScanFolder { Id = folder.Id, Path = root, Resolved = [replacement] };
            await using (var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken))
            {
                await database.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_primary_transfer BEFORE UPDATE OF OwnerId ON BaseItems WHEN OLD.Name = 'primary transfer local' AND NEW.OwnerId <> OLD.OwnerId BEGIN SELECT RAISE(ABORT, 'primary transfer rejected'); END;", TestContext.Current.CancellationToken);
            }

            try
            {
                var failure = await Assert.ThrowsAnyAsync<Exception>(() => ScanAsync(scan));
                Assert.Contains("primary transfer rejected", failure.ToString(), StringComparison.Ordinal);
                Assert.Equal(previous.Id, repository.RetrieveItem(local.Id).OwnerId);
                Assert.Equal(previous.Id, originalLocal.OwnerId);
                Assert.Equal(Guid.Empty, previous.OwnerId);
                Assert.Equal(local.Path, Assert.Single(previous.LocalAlternateVersions));
                // Rerouting precedes the graph save and can survive its failure.
                Assert.Equal(replacement.Id, Assert.Single(links.GetLinkedChildrenIds(playlist.Id, (int)LinkedChildType.Manual)));
            }
            finally
            {
                await using var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken);
                await database.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_primary_transfer;", TestContext.Current.CancellationToken);
            }

            await ScanAsync(scan);
            Assert.Equal(replacement.Id, repository.RetrieveItem(previous.Id).OwnerId);
            Assert.Equal(replacement.Id, repository.RetrieveItem(local.Id).OwnerId);
            Assert.Equal(Guid.Empty, repository.RetrieveItem(linked.Id).OwnerId);
            Assert.Equal(replacement.Id, Assert.IsType<Movie>(repository.RetrieveItem(linked.Id)).PrimaryVersionId);
            Assert.Equal(linked.Id, Assert.Single(links.GetLinkedChildrenIds(replacement.Id, (int)LinkedChildType.LinkedAlternateVersion)));
            Assert.Equal(replacement.Id, Assert.Single(links.GetLinkedChildrenIds(playlist.Id, (int)LinkedChildType.Manual)));

            var final = Movie("final");
            final.LocalAlternateVersions = [local.Path, previous.Path];
            var finalScan = new ControlledScanFolder { Id = folder.Id, Path = root, Resolved = [final] };
            await using (var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken))
            {
                await database.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_transfer_reroute BEFORE UPDATE OF ChildId ON LinkedChildren WHEN OLD.ChildType = 0 BEGIN SELECT RAISE(ABORT, 'transfer reroute rejected'); END;", TestContext.Current.CancellationToken);
            }

            try
            {
                var failure = await Assert.ThrowsAnyAsync<Exception>(() => ScanAsync(finalScan));
                Assert.Contains("transfer reroute rejected", failure.ToString(), StringComparison.Ordinal);
                Assert.Null(Assert.IsType<Movie>(repository.RetrieveItem(replacement.Id)).PrimaryVersionId);
                Assert.Equal(replacement.Id, repository.RetrieveItem(local.Id).OwnerId);
                Assert.Equal(replacement.Id, Assert.Single(links.GetLinkedChildrenIds(playlist.Id, (int)LinkedChildType.Manual)));
            }
            finally
            {
                await using var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken);
                await database.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_transfer_reroute;", TestContext.Current.CancellationToken);
            }

            // Retry the folder scan, not only the command. Both saved primaries remain visible
            // until the transfer can finish, so the existing replacement is selected again.
            await ScanAsync(finalScan);
            Assert.Null(repository.RetrieveItem(replacement.Id));
            Assert.Null(lookup.GetItemById(replacement.Id));
            Assert.Equal(final.Id, repository.RetrieveItem(previous.Id).OwnerId);
            Assert.Equal(final.Id, repository.RetrieveItem(local.Id).OwnerId);
            Assert.Equal(Guid.Empty, repository.RetrieveItem(linked.Id).OwnerId);
            Assert.Equal(linked.Id, Assert.Single(links.GetLinkedChildrenIds(final.Id, (int)LinkedChildType.LinkedAlternateVersion)));
            Assert.Equal(final.Id, Assert.Single(links.GetLinkedChildrenIds(playlist.Id, (int)LinkedChildType.Manual)));
            var detail = await client.GetFromJsonAsync<BaseItemDto>($"Items/{final.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(detail);
            foreach (var id in new[] { previous.Id, local.Id, linked.Id })
            {
                Assert.Contains(detail.MediaSources, source => string.Equals(source.Id, id.ToString("N"), StringComparison.OrdinalIgnoreCase));
            }

            foreach (var path in paths.Values)
            {
                Assert.Equal(bytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            }

            Movie Movie(string name) => new()
            {
                Id = resolution.GetNewItemId(paths[name], typeof(Movie)),
                Path = paths[name],
                Name = "primary transfer " + name,
                ParentId = folder.Id,
                IsInMixedFolder = true,
                Container = "mkv",
                VideoType = VideoType.VideoFile
            };

            Task ScanAsync(ControlledScanFolder target) => target.ValidateChildren(
                deletions,
                writes,
                images,
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

    // Control discovery/order only: the entity's validation hook and all write, SQL, cache,
    // metadata, reference and HTTP services are the real application services.
    private sealed class ControlledScanFolder : Folder
    {
        public IReadOnlyList<BaseItem> Resolved { get; init; } = [];

        public override string GetClientTypeName() => nameof(Folder);

        protected override IEnumerable<BaseItem> GetNonCachedChildren(IDirectoryService directoryService) => Resolved;
    }
}
