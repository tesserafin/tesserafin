using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Threading.Tasks;
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Playlists;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Data;
using Tesserafin.Database.Implementations;
using Tesserafin.Model.Entities;
using Tesserafin.Server.Implementations.Item;
using Xunit;
using LinkedChildType = Tesserafin.Data.Entities.LinkedChildType;

namespace Tesserafin.Server.Integration.Tests;

public sealed class ItemDeletionCommandsEndToEndTests(TesserafinApplicationFactory factory) : IClassFixture<TesserafinApplicationFactory>
{
    private static string? _token;

    [Fact]
    public async Task HttpDeletion_RemovesSourceAndCachedItem_ButConservesUserHistory()
    {
        using var client = await ClientAsync();
        using var sources = new Sources();
        var library = factory.Services.GetRequiredService<ILibraryManager>();
        var libraryWrites = factory.Services.GetRequiredService<IItemWriteCommands>();
        var item = sources.Movie();
        var kept = sources.Movie();
        libraryWrites.CreateItems([item, kept], null, TestContext.Current.CancellationToken);
        var userId = (await AuthHelper.GetUserDtoAsync(client)).Id;
        var user = factory.Services.GetRequiredService<IUserManager>().GetUserById(userId)!;
        user.SetPermission(PermissionKind.EnableContentDeletion, true);
        var key = "deletion-" + item.Id;
        await HistoryAsync(item.Id, userId, key);
        Assert.Same(item, library.GetItemById(item.Id));
        using var response = await client.DeleteAsync("Items/" + item.Id, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(File.Exists(item.Path));
        Assert.True(File.Exists(kept.Path));
        Assert.Null(library.GetItemById(item.Id));
        Assert.NotNull(library.GetItemById(kept.Id));
        await AssertDetachedHistoryAsync(key, userId);
    }

    [Fact]
    public async Task CatalogueOnlyDeletion_PromotesAlternateAndReroutesPlaylist_WithoutDeletingEitherSource()
    {
        using var client = await ClientAsync();
        using var sources = new Sources();
        var library = factory.Services.GetRequiredService<ILibraryManager>();
        var libraryWrites = factory.Services.GetRequiredService<IItemWriteCommands>();
        var primary = sources.Movie();
        var alternate = sources.Movie();
        alternate.SetPrimaryVersionId(primary.Id);
        var playlist = new Playlist { Id = Guid.NewGuid(), Name = "deletion-playlist" };
        libraryWrites.CreateItems([primary, alternate, playlist], null, TestContext.Current.CancellationToken);
        var userId = (await AuthHelper.GetUserDtoAsync(client)).Id;
        var key = "deletion-primary-" + primary.Id;
        await HistoryAsync(primary.Id, userId, key);
        var databases = factory.Services.GetRequiredService<IDbContextFactory<TesserafinDbContext>>();
        await using (var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            database.LinkedChildren.AddRange(
                new LinkedChildEntity { ParentId = primary.Id, ChildId = alternate.Id, ChildType = LinkedChildType.LinkedAlternateVersion },
                new LinkedChildEntity { ParentId = playlist.Id, ChildId = primary.Id, ChildType = LinkedChildType.Manual });
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        primary.LinkedAlternateVersions = [new Tesserafin.Controller.Entities.LinkedChild { ItemId = alternate.Id }];
        var commands = factory.Services.GetRequiredService<IItemDeletionCommands>();
        await commands.DeleteAsync(primary, new DeleteOptions { DeleteFileLocation = false }, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(primary.Path));
        Assert.True(File.Exists(alternate.Path));
        Assert.Null(library.GetItemById(primary.Id));
        var promoted = Assert.IsType<Movie>(library.GetItemById(alternate.Id));
        Assert.Null(promoted.PrimaryVersionId);
        Assert.Equal(Guid.Empty, promoted.OwnerId);
        await using (var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            var reference = Assert.Single(await database.LinkedChildren.Where(link => link.ParentId.Equals(playlist.Id)).ToArrayAsync(TestContext.Current.CancellationToken));
            Assert.Equal(alternate.Id, reference.ChildId);
            Assert.DoesNotContain(await database.LinkedChildren.ToArrayAsync(TestContext.Current.CancellationToken), link => link.ChildId.Equals(primary.Id));
        }

        await AssertDetachedHistoryAsync(key, userId);
    }

    [Fact]
    public async Task ItemChanges_AreSharedAndFollowCatalogueAndCachePublication()
    {
        using var client = await ClientAsync();
        var changes = factory.Services.GetRequiredService<IItemChangeEvents>();
        Assert.Same(changes, factory.Services.GetRequiredService<IItemChangePublisher>());
        var library = factory.Services.GetRequiredService<ILibraryManager>();
        var libraryWrites = factory.Services.GetRequiredService<IItemWriteCommands>();
        var store = factory.Services.GetRequiredService<IItemStore>();
        var deletions = factory.Services.GetRequiredService<IItemDeletionCommands>();
        var lookup = factory.Services.GetRequiredService<IItemLookupService>();
        var databases = factory.Services.GetRequiredService<IDbContextFactory<TesserafinDbContext>>();
        var stored = new UserView { Id = Guid.NewGuid(), Name = "event-store" };
        var general = new UserView { Id = Guid.NewGuid(), Name = "event-general" };
        var observations = new List<(Guid Id, string Kind, bool InDatabase, bool InCache, ItemUpdateType Reason)>();
        EventHandler<ItemChangeEventArgs> broken = (_, args) =>
        {
            if (args.Item.Id.Equals(stored.Id) || args.Item.Id.Equals(general.Id))
            {
                throw new InvalidOperationException("observer failure must not affect writes or other observers");
            }
        };
        changes.ItemAdded += broken;
        changes.ItemUpdated += broken;
        changes.ItemRemoved += broken;
        changes.ItemAdded += Added;
        changes.ItemUpdated += Updated;
        changes.ItemRemoved += Removed;
        try
        {
            store.CreateItem(stored, null);
            libraryWrites.CreateItem(general, null);
            general.Name = "event-updated";
            await libraryWrites.UpdateItemAsync(general, library.RootFolder, ItemUpdateType.MetadataEdit, TestContext.Current.CancellationToken);
            await deletions.DeleteAsync(general, new DeleteOptions { DeleteFileLocation = false }, TestContext.Current.CancellationToken);
            await deletions.DeleteAsync(stored, new DeleteOptions { DeleteFileLocation = false }, TestContext.Current.CancellationToken);

            Assert.Equal(
                new[]
                {
                    (stored.Id, "added", true, true, default(ItemUpdateType)),
                    (general.Id, "added", true, true, default(ItemUpdateType)),
                    (general.Id, "updated", true, true, ItemUpdateType.MetadataEdit),
                    (general.Id, "removed", false, false, default(ItemUpdateType)),
                    (stored.Id, "removed", false, false, default(ItemUpdateType))
                },
                observations);
        }
        finally
        {
            changes.ItemAdded -= broken;
            changes.ItemUpdated -= broken;
            changes.ItemRemoved -= broken;
            changes.ItemAdded -= Added;
            changes.ItemUpdated -= Updated;
            changes.ItemRemoved -= Removed;
        }

        void Added(object? sender, ItemChangeEventArgs args) => Observe("added", args);
        void Updated(object? sender, ItemChangeEventArgs args) => Observe("updated", args);
        void Removed(object? sender, ItemChangeEventArgs args) => Observe("removed", args);
        void Observe(string kind, ItemChangeEventArgs args)
        {
            if (args.Item.Id.Equals(stored.Id) || args.Item.Id.Equals(general.Id))
            {
                using var database = databases.CreateDbContext();
                observations.Add((args.Item.Id, kind, database.BaseItems.Any(row => row.Id.Equals(args.Item.Id)), ReferenceEquals(args.Item, lookup.GetItemById(args.Item.Id)), args.UpdateReason));
            }
        }
    }

    private async Task<HttpClient> ClientAsync()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_token ??= await AuthHelper.CompleteStartupAsync(client));
        return client;
    }

    private async Task HistoryAsync(Guid itemId, Guid userId, string key)
    {
        await using var database = await factory.Services.GetRequiredService<IDbContextFactory<TesserafinDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);
        database.UserData.Add(new UserData { ItemId = itemId, Item = null, UserId = userId, User = null, CustomDataKey = key, PlaybackPositionTicks = 12345678, PlayCount = 4, IsFavorite = true });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task AssertDetachedHistoryAsync(string key, Guid userId)
    {
        await using var database = await factory.Services.GetRequiredService<IDbContextFactory<TesserafinDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);
        var history = await database.UserData.SingleAsync(row => row.CustomDataKey == key && row.UserId.Equals(userId), TestContext.Current.CancellationToken);
        Assert.Equal(BaseItemRepository.PlaceholderId, history.ItemId);
        Assert.Equal(12345678, history.PlaybackPositionTicks);
        Assert.Equal(4, history.PlayCount);
        Assert.True(history.IsFavorite);
        Assert.NotNull(history.RetentionDate);
    }

    private sealed class Sources : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("deletion-host-").FullName;

        public Movie Movie()
        {
            var id = Guid.NewGuid();
            var path = Path.Combine(_root, id + ".mp4");
            File.WriteAllBytes(path, [1, 3, 5, 7]);
            return new Movie { Id = id, Name = "deletion-" + id, Path = path, Container = "mp4", IsInMixedFolder = true, VideoType = VideoType.VideoFile };
        }

        public void Dispose() => Directory.Delete(_root, true);
    }
}
