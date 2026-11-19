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

public sealed class ShortcutLinksEndToEndTests(TesserafinApplicationFactory factory) : IClassFixture<TesserafinApplicationFactory>
{
    [Fact]
    public async Task Shortcuts_RejectFailedSave_RetryThroughUserRootRefresh_PreserveManualAndPendingTargets_AndNeverDeleteMedia()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var writes = factory.Services.GetRequiredService<IItemWriteCommands>();
        var metadata = factory.Services.GetRequiredService<IItemMetadataDispatcher>();
        var deletions = factory.Services.GetRequiredService<IItemDeletionCommands>();
        var resolution = factory.Services.GetRequiredService<IItemResolutionService>();
        var lookup = factory.Services.GetRequiredService<IItemLookupService>();
        var repository = factory.Services.GetRequiredService<IItemRepository>();
        var links = factory.Services.GetRequiredService<ILinkedChildrenService>();
        var files = factory.Services.GetRequiredService<IFileSystem>();
        var databases = factory.Services.GetRequiredService<IDbContextFactory<TesserafinDbContext>>();
        var root = Directory.CreateTempSubdirectory("shortcut-links-host-").FullName;
        var ownerPath = Directory.CreateDirectory(Path.Combine(root, "owner")).FullName;
        var paths = new[] { "first", "second", "manual", "pending" }.ToDictionary(name => name, name => Path.Combine(root, name + ".mkv"));
        var bytes = new byte[] { 71, 73, 79, 83 };
        foreach (var path in paths.Values)
        {
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        }

        try
        {
            var owner = new UserRootFolder
            {
                Id = resolution.GetNewItemId(ownerPath, typeof(UserRootFolder)),
                Path = ownerPath,
                Name = "shortcut lifecycle owner",
                DateLastRefreshed = DateTime.UtcNow
            };
            var first = Movie("first");
            var second = Movie("second");
            var manual = Movie("manual");
            writes.CreateItems([owner, first, second, manual], null, TestContext.Current.CancellationToken);
            writes.UpsertLinkedChild(owner.Id, manual.Id, LinkedChildType.Manual, TestContext.Current.CancellationToken);
            var before = owner.LinkedChildren;
            var shortcut = Path.Combine(ownerPath, "a.mblink");
            var duplicate = Path.Combine(ownerPath, "a2.mblink");
            var manualShortcut = Path.Combine(ownerPath, "manual.mblink");
            var note = Path.Combine(ownerPath, "note.txt");
            await File.WriteAllTextAsync(shortcut, Path.GetRelativePath(ownerPath, first.Path), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(duplicate, first.Path, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(manualShortcut, manual.Path, TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(note, bytes, TestContext.Current.CancellationToken);
            await using (var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken))
            {
                await database.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_shortcut_links BEFORE INSERT ON LinkedChildren WHEN NEW.ChildType = 1 AND EXISTS (SELECT 1 FROM BaseItems WHERE Id = NEW.ParentId AND Name = 'shortcut lifecycle owner') BEGIN SELECT RAISE(ABORT, 'shortcut links rejected'); END;", TestContext.Current.CancellationToken);
            }

            try
            {
                var failure = await Assert.ThrowsAnyAsync<Exception>(() => RefreshAsync());
                Assert.Contains("shortcut links rejected", failure.ToString(), StringComparison.Ordinal);
                Assert.Same(before, owner.LinkedChildren);
                Assert.Empty(links.GetLinkedChildrenIds(owner.Id, (int)LinkedChildType.Shortcut));
                Assert.Equal(manual.Id, Assert.Single(links.GetLinkedChildrenIds(owner.Id, (int)LinkedChildType.Manual)));
            }
            finally
            {
                await using var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken);
                await database.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_shortcut_links;", TestContext.Current.CancellationToken);
            }

            await owner.RefreshMetadata(metadata, deletions, writes, Options(), TestContext.Current.CancellationToken);
            Assert.Equal(first.Id, Assert.Single(links.GetLinkedChildrenIds(owner.Id, (int)LinkedChildType.Shortcut)));
            Assert.Equal(manual.Id, Assert.Single(links.GetLinkedChildrenIds(owner.Id, (int)LinkedChildType.Manual)));
            Assert.True(owner.GetLinkedChildren().Select(item => item.Id).ToHashSet().SetEquals([first.Id, manual.Id]));
            var detail = await client.GetFromJsonAsync<BaseItemDto>($"Items/{first.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(detail);
            Assert.Equal(first.Id, detail.Id);

            // One valid retarget followed by an invalid file must leave the old complete set.
            await File.WriteAllTextAsync(shortcut, second.Path, TestContext.Current.CancellationToken);
            var bad = Path.Combine(ownerPath, "broken.mblink");
            await File.WriteAllTextAsync(bad, string.Empty, TestContext.Current.CancellationToken);
            var previousLinks = owner.LinkedChildren;
            await Assert.ThrowsAsync<IOException>(() => RefreshAsync());
            Assert.Same(previousLinks, owner.LinkedChildren);
            Assert.Equal(first.Id, Assert.Single(links.GetLinkedChildrenIds(owner.Id, (int)LinkedChildType.Shortcut)));
            File.Delete(bad);
            File.Delete(duplicate);
            await owner.RefreshMetadata(metadata, deletions, writes, Options(), TestContext.Current.CancellationToken);
            Assert.Equal(second.Id, Assert.Single(links.GetLinkedChildrenIds(owner.Id, (int)LinkedChildType.Shortcut)));

            var pendingShortcut = Path.Combine(ownerPath, "pending.mblink");
            await File.WriteAllTextAsync(pendingShortcut, paths["pending"], TestContext.Current.CancellationToken);
            Assert.True(await RefreshAsync());
            var pending = Movie("pending");
            Assert.Null(repository.RetrieveItem(pending.Id));
            Assert.Equal(second.Id, Assert.Single(links.GetLinkedChildrenIds(owner.Id, (int)LinkedChildType.Shortcut)));
            writes.CreateItem(pending, null);
            // The existing lazy reader can cache the new ID; the writer still compares SQL
            // and must persist the relation that was absent while the target was unscanned.
            Assert.Contains(owner.GetLinkedChildren(), item => item.Id.Equals(pending.Id));
            Assert.True(await RefreshAsync());
            Assert.True(links.GetLinkedChildrenIds(owner.Id, (int)LinkedChildType.Shortcut).ToHashSet().SetEquals([second.Id, pending.Id]));
            var savedDate = owner.DateLastSaved;
            var savedArray = owner.LinkedChildren;
            Assert.False(await RefreshAsync());
            Assert.Equal(savedDate, owner.DateLastSaved);
            Assert.Same(savedArray, owner.LinkedChildren);
            Assert.NotNull(lookup.GetItemById(first.Id));

            File.Delete(shortcut);
            File.Delete(manualShortcut);
            File.Delete(pendingShortcut);
            Assert.True(await RefreshAsync());
            Assert.Empty(links.GetLinkedChildrenIds(owner.Id, (int)LinkedChildType.Shortcut));
            Assert.Equal(manual.Id, Assert.Single(links.GetLinkedChildrenIds(owner.Id, (int)LinkedChildType.Manual)));
            foreach (var path in paths.Values)
            {
                Assert.Equal(bytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            }

            Assert.Equal(bytes, await File.ReadAllBytesAsync(note, TestContext.Current.CancellationToken));

            Movie Movie(string name) => new()
            {
                Id = resolution.GetNewItemId(paths[name], typeof(Movie)),
                Path = paths[name],
                Name = name,
                Container = "mkv",
                IsInMixedFolder = true,
                VideoType = VideoType.VideoFile,
                DateLastRefreshed = DateTime.UtcNow
            };

            Task<bool> RefreshAsync() => writes.RefreshShortcutLinksAsync(owner, files.GetFileSystemEntries(ownerPath).ToArray(), TestContext.Current.CancellationToken);

            MetadataRefreshOptions Options() => new(new DirectoryService(files)) { MetadataRefreshMode = MetadataRefreshMode.None, ImageRefreshMode = MetadataRefreshMode.None };
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
