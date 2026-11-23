using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

public sealed class FolderChildReconcilerTests
{
    [Fact]
    public async Task FailedResolvedUpdate_LeavesThePublishedItemAndResolvedArraysUntouched_ThenRetries()
    {
        var context = new Context();
        var current = new DeletableVideo { Id = Guid.NewGuid(), Path = "/library/film.mkv", ParentId = context.Parent.Id, IsInMixedFolder = true, AdditionalParts = ["old part"] };
        var resolved = new DeletableVideo { Id = current.Id, Path = current.Path, AdditionalParts = ["new part"] };
        context.Writes.Setup(w => w.UpdateItemAsync(It.IsAny<BaseItem>(), context.Parent, ItemUpdateType.MetadataImport, It.IsAny<CancellationToken>()))
            .Callback((BaseItem item, BaseItem _, ItemUpdateType _, CancellationToken _) =>
            {
                Assert.NotSame(current, item);
                Assert.True(current.IsInMixedFolder);
                Assert.Equal("old part", Assert.Single(current.AdditionalParts));
                Assert.NotSame(resolved.AdditionalParts, Assert.IsType<DeletableVideo>(item).AdditionalParts);
            }).ThrowsAsync(new IOException("save rejected"));

        await Assert.ThrowsAsync<IOException>(() => context.RunAsync([current], [resolved]));

        Assert.True(current.IsInMixedFolder);
        context.Writes.Setup(w => w.UpdateItemAsync(It.IsAny<BaseItem>(), context.Parent, ItemUpdateType.MetadataImport, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var result = await context.RunAsync([current], [resolved]);
        var saved = Assert.IsType<DeletableVideo>(Assert.Single(result.ValidChildren));
        Assert.False(saved.IsInMixedFolder);
        Assert.Equal("new part", Assert.Single(saved.AdditionalParts));
        Assert.Same(saved, Assert.Single(result.AccessibleChildren));
        Assert.True(current.IsInMixedFolder);
        context.Images.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedItems_CommitOrFailTheirPreparedImageUpdate_AndAlwaysDispose(bool fail)
    {
        var context = new Context();
        var current = context.Item("same");
        if (fail)
        {
            context.ImageUpdate.Setup(u => u.CommitAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("image rejected"));
            await Assert.ThrowsAsync<IOException>(() => context.RunAsync([current], [current.CopyForWrite()]));
        }
        else
        {
            var result = await context.RunAsync([current], [current.CopyForWrite()]);
            Assert.Same(current, Assert.Single(result.ValidChildren));
        }

        context.ImageUpdate.Verify(u => u.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        context.ImageUpdate.Verify(u => u.DisposeAsync(), Times.Once);
        context.Writes.VerifyNoOtherCalls();
        context.Deletions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DuplicateDiscovery_CreatesOneDetachedChild_WithTheCapturedParent()
    {
        var context = new Context();
        var discovered = context.Item("new");
        BaseItem? created = null;
        context.Writes.Setup(w => w.CreateItems(It.IsAny<IReadOnlyList<BaseItem>>(), context.Parent, It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<BaseItem> items, BaseItem _, CancellationToken _) => created = Assert.Single(items));

        var result = await context.RunAsync([], [discovered, discovered]);

        Assert.NotNull(created);
        Assert.NotSame(discovered, created);
        Assert.Equal(context.Parent.Id, created.ParentId);
        Assert.Equal(Guid.Empty, discovered.ParentId);
        Assert.Same(created, Assert.Single(result.ValidChildren));
        context.Writes.Verify(w => w.CreateItems(It.IsAny<IReadOnlyList<BaseItem>>(), context.Parent, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CaseDistinctPathsWithDistinctIdentities_AreNotRejectedAsAnExactPathConflict()
    {
        var context = new Context();
        var first = context.Item("Film");
        var second = context.Item("film");

        var result = await context.RunAsync([], [first, second]);

        Assert.Equal(2, result.ValidChildren.Count);
        context.Writes.Verify(w => w.CreateItems(It.Is<IReadOnlyList<BaseItem>>(items => items.Count == 2), context.Parent, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictingIdentityOrPath_IsRefusedBeforeAnyMutation(bool sameIdentity)
    {
        var context = new Context();
        var first = context.Item("first");
        var second = context.Item("second");
        if (sameIdentity)
        {
            second.Id = first.Id;
        }
        else
        {
            second.Path = first.Path;
        }

        await Assert.ThrowsAsync<ArgumentException>(() => context.RunAsync([], [first, second]));

        context.Writes.VerifyNoOtherCalls();
        context.Deletions.VerifyNoOtherCalls();
        context.Images.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InaccessibleLibrary_SkipsAllWritesAndRefresh()
    {
        var context = new Context();
        var root = new AggregateFolder { Id = Guid.NewGuid() };
        context.Parent.ParentId = root.Id;
        context.Lookup.Setup(l => l.GetItemById(root.Id)).Returns(root);
        context.Directory.Setup(d => d.IsAccessible(context.Parent.Path)).Returns(false);

        var result = await context.RunAsync([context.Item("kept")], []);

        Assert.True(result.Skipped);
        context.Writes.VerifyNoOtherCalls();
        context.Deletions.VerifyNoOtherCalls();
        context.Images.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InaccessibleChangedType_PreservesTheOldPathIdentity_WithoutRefreshingIt()
    {
        var context = new Context();
        var root = new AggregateFolder { Id = Guid.NewGuid() };
        context.Lookup.Setup(l => l.GetItemById(root.Id)).Returns(root);
        var current = context.Item("unavailable");
        var resolved = new Folder { Id = Guid.NewGuid(), Path = current.Path, ParentId = root.Id };
        context.Directory.Setup(d => d.IsAccessible(resolved.Path)).Returns(false);

        var result = await context.RunAsync([current], [resolved]);

        Assert.Same(current, Assert.Single(result.ValidChildren));
        Assert.Empty(result.AccessibleChildren);
        context.Writes.VerifyNoOtherCalls();
        context.Deletions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FailedTypeReplacement_DoesNotDetachThePublishedStaleItem_OrCreateItsReplacement()
    {
        var context = new Context();
        var current = context.Item("retyped");
        current.ParentId = context.Parent.Id;
        var replacement = new DeletableVideo { Id = Guid.NewGuid(), Path = current.Path };
        context.Deletions.Setup(d => d.DeleteAsync(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>(), context.Parent, It.IsAny<CancellationToken>()))
            .Callback((BaseItem item, DeleteOptions options, BaseItem _, CancellationToken _) =>
            {
                Assert.NotSame(current, item);
                Assert.False(options.DeleteFileLocation);
                item.SetParent(null);
            }).ThrowsAsync(new IOException("delete rejected"));

        await Assert.ThrowsAsync<IOException>(() => context.RunAsync([current], [replacement]));

        Assert.Equal(context.Parent.Id, current.ParentId);
        Assert.Equal(Guid.Empty, replacement.ParentId);
        context.Writes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RemovedAndNewItems_UseCatalogueOnlyDeletion_ThenCreationThenHistoryReattachment()
    {
        var context = new Context();
        var removed = context.Item("removed");
        var incoming = context.Item("incoming");
        var order = new List<string>();
        context.Deletions.Setup(d => d.DeleteAsync(It.IsAny<BaseItem>(), It.Is<DeleteOptions>(o => !o.DeleteFileLocation), context.Parent, It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("delete")).Returns(Task.CompletedTask);
        context.Writes.Setup(w => w.CreateItems(It.IsAny<IReadOnlyList<BaseItem>>(), context.Parent, It.IsAny<CancellationToken>())).Callback(() => order.Add("create"));
        context.Writes.Setup(w => w.ReattachUserDataAsync(It.IsAny<BaseItem>(), It.IsAny<CancellationToken>())).Callback(() => order.Add("history")).Returns(Task.CompletedTask);

        await context.RunAsync([removed], [incoming]);

        Assert.Equal(new[] { "delete", "create", "history" }, order);
        context.Files.Verify(f => f.DeleteFile(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExistingSelectedPrimary_CanResumeDemotion_WithoutBeingCreatedAgain()
    {
        var context = new Context();
        var previous = new DeletableVideo { Id = Guid.NewGuid(), Path = "/library/previous.mkv" };
        var replacement = new DeletableVideo { Id = Guid.NewGuid(), Path = "/library/replacement.mkv", LocalAlternateVersions = [previous.Path] };

        await context.RunAsync([previous, replacement], [replacement.CopyForWrite()]);

        context.Writes.Verify(w => w.DemotePrimaryVersionAsync(previous, replacement, It.IsAny<CancellationToken>()), Times.Once);
        context.Writes.Verify(w => w.CreateItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<BaseItem>(), It.IsAny<CancellationToken>()), Times.Never);
        context.Deletions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CancelledAdmission_DoesNotReadOrMutate()
    {
        var context = new Context();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Service.ReconcileAsync(context.Parent, [], [], context.Directory.Object, false, context.Writes.Object, context.Deletions.Object, context.Images.Object, Mock.Of<IChannelManager>(), cancellation.Token));

        context.Lookup.VerifyNoOtherCalls();
        context.Files.VerifyNoOtherCalls();
        context.Writes.VerifyNoOtherCalls();
    }

    private sealed class Context
    {
        public Context()
        {
            Lookup.Setup(l => l.GetItemById(Parent.Id)).Returns(Parent);
            Directory.Setup(d => d.IsAccessible(It.IsAny<string>())).Returns(true);
            ImageUpdate.Setup(u => u.CommitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            Images.Setup(i => i.PrepareRefreshAsync(It.IsAny<BaseItem>(), false, true, It.IsAny<CancellationToken>())).ReturnsAsync(ImageUpdate.Object);
            Service = new FolderChildReconciler(Lookup.Object, NullLogger<FolderChildReconciler>.Instance);
        }

        public Folder Parent { get; } = new() { Id = Guid.NewGuid(), Path = "/library" };

        public Mock<IItemLookupService> Lookup { get; } = new();

        public Mock<IFileSystem> Files { get; } = new();

        public Mock<IDirectoryService> Directory { get; } = new();

        public Mock<IItemWriteCommands> Writes { get; } = new();

        public Mock<IItemDeletionCommands> Deletions { get; } = new();

        public Mock<IItemImageService> Images { get; } = new();

        public Mock<IItemImageUpdate> ImageUpdate { get; } = new();

        public FolderChildReconciler Service { get; }

        public BaseItem Item(string name) => new Leaf() { Id = Guid.NewGuid(), Path = "/library/" + name + ".mkv" };

        public Task<FolderChildReconciliationResult> RunAsync(IReadOnlyList<BaseItem> current, IReadOnlyList<BaseItem> discovered)
            => Service.ReconcileAsync(Parent, current, discovered, Directory.Object, false, Writes.Object, Deletions.Object, Images.Object, Mock.Of<IChannelManager>(), TestContext.Current.CancellationToken);
    }

    private sealed class Leaf : BaseItem
    {
        public override bool CanDelete(IChannelManager channels) => true;

        public override List<string> GetUserDataKeys() => ["shared history key"];
    }

    private sealed class DeletableVideo : Video
    {
        public override bool CanDelete(IChannelManager channels) => true;
    }
}
