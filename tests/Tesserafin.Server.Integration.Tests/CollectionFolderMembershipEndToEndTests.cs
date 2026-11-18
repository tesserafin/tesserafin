using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Database.Implementations;
using Tesserafin.Model.IO;
using Xunit;

namespace Tesserafin.Server.Integration.Tests;

public sealed class CollectionFolderMembershipEndToEndTests(TesserafinApplicationFactory factory) : IClassFixture<TesserafinApplicationFactory>
{
    [Fact]
    public async Task LibraryMembership_FailedLinkSaveRollsBackLocationsAndIds_RetryPublishesNestedFolder()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var token = TestContext.Current.CancellationToken;
        var writes = factory.Services.GetRequiredService<IItemWriteCommands>();
        var deletions = factory.Services.GetRequiredService<IItemDeletionCommands>();
        var lookup = factory.Services.GetRequiredService<IItemLookupService>();
        var repository = factory.Services.GetRequiredService<IItemRepository>();
        var links = factory.Services.GetRequiredService<ILinkedChildrenService>();
        var databases = factory.Services.GetRequiredService<IDbContextFactory<TesserafinDbContext>>();
        var directoryService = new DirectoryService(factory.Services.GetRequiredService<IFileSystem>());
        var physicalRoot = factory.Services.GetRequiredService<ILibraryRootFolderProvider>().GetRootFolder();
        var temporary = Directory.CreateTempSubdirectory("collection-membership-").FullName;

        try
        {
            var firstPath = Directory.CreateDirectory(Path.Combine(temporary, "first")).FullName;
            var nestedPath = Directory.CreateDirectory(Path.Combine(firstPath, "nested")).FullName;
            var definitions = Directory.CreateDirectory(Path.Combine(temporary, "definitions")).FullName;
            var libraryPath = Directory.CreateDirectory(Path.Combine(definitions, "library")).FullName;
            var firstSource = Path.Combine(firstPath, "First.mp4");
            var secondSource = Path.Combine(nestedPath, "Second.mp4");
            byte[] bytes = [5, 8, 13, 21];
            await File.WriteAllBytesAsync(firstSource, bytes, token);
            await File.WriteAllBytesAsync(secondSource, bytes, token);

            var first = FolderAt(firstPath, physicalRoot);
            writes.CreateItem(first, physicalRoot);
            var nested = FolderAt(nestedPath, first);
            writes.CreateItem(nested, first);
            var firstMovie = MovieAt(firstSource, first);
            var secondMovie = MovieAt(secondSource, nested);
            writes.CreateItems([firstMovie, secondMovie], null, token);
            first.LinkedChildren = [LinkedChild.Create(firstMovie)];
            nested.LinkedChildren = [LinkedChild.Create(secondMovie)];
            await writes.UpdateItemAsync(first, physicalRoot, ItemUpdateType.MetadataEdit, token);
            await writes.UpdateItemAsync(nested, first, ItemUpdateType.MetadataEdit, token);

            var parent = FolderAt(definitions, null);
            writes.CreateItem(parent, null);
            var library = new CollectionFolder
            {
                Id = Guid.NewGuid(),
                Path = libraryPath,
                Name = "membership transaction fixture",
                DateLastRefreshed = DateTime.UtcNow
            };
            library.SetParent(parent);
            writes.CreateItem(library, parent);
            var shortcut = Path.Combine(libraryPath, "source.mblink");
            await File.WriteAllTextAsync(shortcut, firstPath, token);
            Assert.True(await RefreshAsync());
            AssertMembership(library, firstPath, first.Id, firstMovie.Id);
            var previousLocations = library.PhysicalLocations;
            var previousIds = library.PhysicalFolderIds;
            var previousLinks = library.LinkedChildren;
            var previousSave = library.DateLastSaved;

            await File.WriteAllTextAsync(shortcut, nestedPath, token);
            await using (var database = await databases.CreateDbContextAsync(token))
            {
                await database.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_membership_link BEFORE INSERT ON LinkedChildren WHEN EXISTS (SELECT 1 FROM BaseItems WHERE Id = NEW.ParentId AND Name = 'membership transaction fixture') BEGIN SELECT RAISE(ABORT, 'membership link rejected'); END;", token);
            }

            try
            {
                var failure = await Assert.ThrowsAnyAsync<Exception>(() => RefreshAsync());
                Assert.Contains("membership link rejected", failure.ToString(), StringComparison.Ordinal);
                Assert.Same(previousLocations, library.PhysicalLocations);
                Assert.Same(previousIds, library.PhysicalFolderIds);
                Assert.Same(previousLinks, library.LinkedChildren);
                Assert.Equal(previousSave, library.DateLastSaved);
                AssertMembership(Assert.IsType<CollectionFolder>(lookup.GetItemById(library.Id)), firstPath, first.Id, firstMovie.Id);
                AssertMembership(Assert.IsType<CollectionFolder>(repository.RetrieveItem(library.Id)), firstPath, first.Id, firstMovie.Id);
                Assert.Equal(firstMovie.Id, Assert.Single(links.GetLinkedChildrenIds(library.Id, (int)LinkedChildType.Manual)));
            }
            finally
            {
                await using var database = await databases.CreateDbContextAsync(token);
                await database.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_membership_link;", token);
            }

            var options = new MetadataRefreshOptions(directoryService) { MetadataRefreshMode = MetadataRefreshMode.None, ImageRefreshMode = MetadataRefreshMode.None };
            await library.RefreshMetadata(deletions, writes, options, token);
            AssertMembership(library, nestedPath, nested.Id, secondMovie.Id);
            AssertMembership(Assert.IsType<CollectionFolder>(repository.RetrieveItem(library.Id)), nestedPath, nested.Id, secondMovie.Id);
            Assert.Equal(nested.Id, Assert.Single(library.GetPhysicalFolders(lookup)).Id);
            Assert.Equal(secondMovie.Id, Assert.Single(library.GetLinkedChildren()).Id);
            Assert.Equal(secondMovie.Id, Assert.Single(links.GetLinkedChildrenIds(library.Id, (int)LinkedChildType.Manual)));
            Assert.False(await RefreshAsync());
            Assert.Equal(bytes, await File.ReadAllBytesAsync(firstSource, token));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(secondSource, token));

            Task<bool> RefreshAsync() => writes.RefreshCollectionFolderMembershipAsync(library, directoryService, token);

            void AssertMembership(CollectionFolder item, string path, Guid folderId, Guid movieId)
            {
                Assert.Equal(new[] { libraryPath, path }, item.PhysicalLocations);
                Assert.Equal(folderId, Assert.Single(item.PhysicalFolderIds));
                Assert.Equal(movieId, Assert.Single(item.LinkedChildren).ItemId);
            }
        }
        finally
        {
            Directory.Delete(temporary, true);
        }
    }

    private static Folder FolderAt(string path, Folder? parent)
        => new() { Id = Guid.NewGuid(), Name = Path.GetFileName(path), Path = path, ParentId = parent?.Id ?? Guid.Empty };

    private static Movie MovieAt(string path, Folder parent)
        => new() { Id = Guid.NewGuid(), Name = Path.GetFileNameWithoutExtension(path), Path = path, ParentId = parent.Id, DateLastRefreshed = DateTime.UtcNow };
}
