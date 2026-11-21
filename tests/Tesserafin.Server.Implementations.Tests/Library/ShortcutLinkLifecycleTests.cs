using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.IO;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

public sealed class ShortcutLinkLifecycleTests
{
    [Fact]
    public async Task FailedSave_PreservesPublishedLinks_ThenRetriesWithCanonicalManualLinksAndDeduplicatedTargets()
    {
        var context = new Context();
        var manualId = Guid.NewGuid();
        context.StoredLinks = [new LinkedChild { ItemId = manualId, Type = LinkedChildType.Manual }];
        var oldArray = context.Owner.LinkedChildren;
        var target = context.Target("/media/film.mkv");
        context.Targets["/media/manual.mkv"] = new Folder { Id = manualId };
        context.Resolved = [new("a.mblink", target.Path), new("duplicate.mblink", target.Path), new("manual.mblink", "/media/manual.mkv")];
        context.FailSave = true;

        await Assert.ThrowsAsync<IOException>(() => context.RefreshAsync());

        Assert.Same(oldArray, context.Owner.LinkedChildren);
        Assert.Equal(manualId, Assert.Single(context.StoredLinks).ItemId);
        Assert.Equal(0, context.CachePublications);
        context.Persistence.Verify(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()), Times.Once);
        context.FailSave = false;

        Assert.True(await context.RefreshAsync());

        Assert.Equal(2, context.StoredLinks.Count);
        Assert.Contains(context.StoredLinks, link => link.ItemId.GetValueOrDefault().Equals(manualId) && link.Type == LinkedChildType.Manual);
        Assert.Contains(context.StoredLinks, link => link.ItemId.GetValueOrDefault().Equals(target.Id) && link.Type == LinkedChildType.Shortcut);
        Assert.Equal(2, context.Owner.LinkedChildren.Length);
        Assert.Equal(1, context.CachePublications);
        Assert.False(await context.RefreshAsync());
        Assert.Equal(1, context.CachePublications);
    }

    [Fact]
    public async Task UnchangedLinks_RepairAStaleCallerFromSql_WithoutResavingTheFolder()
    {
        var context = new Context();
        var target = context.Target("/media/film.mkv");
        var manualId = Guid.NewGuid();
        context.StoredLinks = [new LinkedChild { ItemId = manualId, Type = LinkedChildType.Manual }, new LinkedChild { ItemId = target.Id, Type = LinkedChildType.Shortcut }];
        context.Resolved = [new("a.mblink", target.Path)];

        Assert.False(await context.RefreshAsync());

        Assert.Equal(2, context.Owner.LinkedChildren.Length);
        Assert.Contains(context.Owner.LinkedChildren, link => link.ItemId.GetValueOrDefault().Equals(manualId));
        context.Persistence.VerifyNoOtherCalls();
        context.Metadata.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnscannedTargets_AreNotMaterialized_AndGetPersistedWhenTheCatalogueCatchesUp()
    {
        var context = new Context();
        const string path = "/media/not-scanned.mkv";
        context.Resolved = [new("pending.mblink", path)];

        Assert.True(await context.RefreshAsync());

        Assert.Empty(context.Targets);
        Assert.Empty(context.StoredLinks);
        Assert.Null(Assert.Single(context.Owner.LinkedChildren).ItemId);
        var target = context.Target(path);

        Assert.True(await context.RefreshAsync());

        Assert.Equal(target.Id, Assert.Single(context.StoredLinks).ItemId);
        Assert.False(await context.RefreshAsync());
    }

    [Fact]
    public async Task EmptySuccessfulDiscovery_RemovesShortcutsAndKeepsManualLinks()
    {
        var context = new Context();
        var manualId = Guid.NewGuid();
        context.StoredLinks = [new LinkedChild { ItemId = Guid.NewGuid(), Type = LinkedChildType.Shortcut }, new LinkedChild { ItemId = manualId, Type = LinkedChildType.Manual }];

        Assert.True(await context.RefreshAsync());

        var kept = Assert.Single(context.StoredLinks);
        Assert.Equal(manualId, kept.ItemId);
        Assert.Equal(LinkedChildType.Manual, kept.Type);
        context.Files.Verify(f => f.DeleteFile(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task FailedShortcutRead_DoesNotReadOrWriteTheCatalogue()
    {
        var context = new Context();
        context.Resolution.Setup(r => r.ResolveShortcutTargets(context.Owner, It.IsAny<IReadOnlyList<FileSystemMetadata>>(), It.IsAny<CancellationToken>())).Throws(new IOException("shortcut unreadable"));

        await Assert.ThrowsAsync<IOException>(() => context.RefreshAsync());

        context.Lookup.VerifyNoOtherCalls();
        context.Repository.VerifyNoOtherCalls();
        context.Links.VerifyNoOtherCalls();
        context.Persistence.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SelfShortcut_IsRefusedBeforePersistence()
    {
        var context = new Context();
        context.Resolved = [new("self.mblink", context.Owner.Path)];

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.RefreshAsync());

        context.Persistence.VerifyNoOtherCalls();
        context.Metadata.VerifyNoOtherCalls();
        Assert.Empty(context.Owner.LinkedChildren);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterTargetLookup_DoesNotPublishPreparedLinks(bool alreadyCommitted)
    {
        var context = new Context();
        using var cancellation = new CancellationTokenSource();
        var target = context.Target("/media/film.mkv");
        if (alreadyCommitted)
        {
            context.StoredLinks = [new LinkedChild { ItemId = target.Id, Type = LinkedChildType.Shortcut }];
        }

        context.Resolved = [new("a.mblink", target.Path)];
        context.Repository.Setup(r => r.GetItemList(It.IsAny<InternalItemsQuery>())).Callback(() => cancellation.Cancel()).Returns([target]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Commands.RefreshShortcutLinksAsync(context.Owner, [], cancellation.Token));

        Assert.Empty(context.Owner.LinkedChildren);
        context.Persistence.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CancelledAdmission_DoesNotResolveOrRead()
    {
        var context = new Context();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Commands.RefreshShortcutLinksAsync(context.Owner, [], cancellation.Token));

        context.Resolution.VerifyNoOtherCalls();
        context.Lookup.VerifyNoOtherCalls();
        context.Persistence.VerifyNoOtherCalls();
    }

    private sealed class Context : IItemCacheStore
    {
        public Context()
        {
            Resolution.Setup(r => r.ResolveShortcutTargets(Owner, It.IsAny<IReadOnlyList<FileSystemMetadata>>(), It.IsAny<CancellationToken>())).Returns(() => Resolved);
            Lookup.Setup(l => l.GetItemById(Owner.Id)).Returns(() => Published ?? Owner);
            Links.Setup(l => l.GetLinkedChildren(Owner.Id)).Returns(() => StoredLinks);
            Repository.Setup(r => r.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.Path is { } path && Targets.TryGetValue(path, out var target) ? new[] { target } : []);
            Files.Setup(f => f.AreEqual(It.IsAny<string>(), It.IsAny<string>())).Returns((string a, string b) => string.Equals(a, b, StringComparison.Ordinal));
            Metadata.Setup(m => m.SaveAsync(It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>>())).Returns(Task.CompletedTask);
            Persistence.Setup(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>())).Callback((IReadOnlyList<BaseItem> items, CancellationToken _) =>
            {
                if (FailSave)
                {
                    throw new IOException("link save rejected");
                }

                var folder = Assert.IsType<Folder>(Assert.Single(items));
                StoredLinks = folder.LinkedChildren.Where(link => link.ItemId.HasValue).ToArray();
            });
            Commands = new ItemWriteCommands(new ItemVersionGraphPlanner(Resolution.Object, Mock.Of<ILibraryItemOptions>(), Lookup.Object, Links.Object), Persistence.Object, this, Lookup.Object, Links.Object, Metadata.Object, Files.Object, Mock.Of<IItemChangePublisher>(), new ActiveRecordings(), Mock.Of<INamedItemCatalog>(), new NamedItemIdentity(Mock.Of<IServerConfigurationManager>(), Files.Object), Resolution.Object, Repository.Object, Mock.Of<ILibraryItemOptions>(), new FolderChildReconciler(Lookup.Object, NullLogger<FolderChildReconciler>.Instance), LibraryWriteFixture.CreateCollectionMembershipResolver(), NullLogger<ItemWriteCommands>.Instance);
        }

        public Folder Owner { get; } = new() { Id = Guid.NewGuid(), Path = "/shortcut-owner", Name = "owner" };

        public Folder? Published { get; private set; }

        public Dictionary<string, BaseItem> Targets { get; } = [];

        public IReadOnlyList<ResolvedShortcutTarget> Resolved { get; set; } = [];

        public IReadOnlyList<LinkedChild> StoredLinks { get; set; } = [];

        public Mock<IItemResolutionService> Resolution { get; } = new();

        public Mock<IItemLookupService> Lookup { get; } = new();

        public Mock<IItemRepository> Repository { get; } = new();

        public Mock<ILinkedChildrenService> Links { get; } = new();

        public Mock<IItemPersistenceService> Persistence { get; } = new();

        public Mock<IItemMetadataExporter> Metadata { get; } = new();

        public Mock<IFileSystem> Files { get; } = new();

        public ItemWriteCommands Commands { get; }

        public bool FailSave { get; set; }

        public int CachePublications { get; private set; }

        public BaseItem Target(string path)
        {
            var target = new Folder { Id = Guid.NewGuid(), Path = path };
            Targets[path] = target;
            return target;
        }

        public Task<bool> RefreshAsync() => Commands.RefreshShortcutLinksAsync(Owner, [], TestContext.Current.CancellationToken);

        public void Register(BaseItem item)
        {
            CachePublications++;
            Published = (Folder)item;
        }

        public void Remove(Guid id)
        {
        }

        public void RemoveRange(IEnumerable<Guid> ids)
        {
        }
    }
}
