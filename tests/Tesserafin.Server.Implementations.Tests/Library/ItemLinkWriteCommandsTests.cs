using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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

public sealed class ItemLinkWriteCommandsTests
{
    [Fact]
    public void Upsert_PersistsBeforeInvalidatingTheParent_AndDoesNotSaveTheWholeItem()
    {
        var context = new Context();
        var parent = Guid.NewGuid();
        var child = Guid.NewGuid();
        context.Linked.Setup(l => l.UpsertLinkedChild(parent, child, LinkedChildType.LinkedAlternateVersion))
            .Callback(() => Assert.Empty(context.Cache.Removed));

        context.Commands.UpsertLinkedChild(parent, child, LinkedChildType.LinkedAlternateVersion, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { parent }, context.Cache.Removed);
        context.Linked.Verify(l => l.UpsertLinkedChild(parent, child, LinkedChildType.LinkedAlternateVersion), Times.Once);
        context.Metadata.VerifyNoOtherCalls();
        context.Persistence.VerifyNoOtherCalls();
    }

    [Fact]
    public void FailedUpsert_DoesNotEvictThePublishedParent()
    {
        var context = new Context();
        context.Linked.Setup(l => l.UpsertLinkedChild(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<LinkedChildType>())).Throws(new IOException("database failure"));

        Assert.Throws<IOException>(() => context.Commands.UpsertLinkedChild(Guid.NewGuid(), Guid.NewGuid(), LinkedChildType.Manual, TestContext.Current.CancellationToken));

        Assert.Empty(context.Cache.Removed);
    }

    [Fact]
    public async Task Reroute_ReloadsCanonicalLinksAndReconcilesAllParentsBeforeExport()
    {
        var context = new Context();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var manual = new LinkedChild { ItemId = source, Type = LinkedChildType.Manual };
        var shortcut = new LinkedChild { ItemId = source, Type = LinkedChildType.Shortcut };
        var first = context.Parent(manual, shortcut);
        var existingTarget = new LinkedChild { ItemId = target, Type = LinkedChildType.Manual };
        var second = context.Parent(new LinkedChild { ItemId = source, Type = LinkedChildType.Manual }, existingTarget);
        context.Linked.Setup(l => l.RerouteLinkedChildren(source, target)).Returns([first.Id, second.Id, first.Id]);
        context.Linked.Setup(l => l.GetLinkedChildren(first.Id)).Returns([new LinkedChild { ItemId = target, Type = LinkedChildType.Manual }, shortcut]);
        context.Linked.Setup(l => l.GetLinkedChildren(second.Id)).Returns([existingTarget]);
        context.Metadata.Setup(m => m.SaveAsync(It.IsAny<BaseItem>(), ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>>()))
            .Callback(() =>
            {
                Assert.Null(first.UserData);
                Assert.Null(second.UserData);
                Assert.Equal(target, first.LinkedChildren[0].ItemId);
                Assert.Same(existingTarget, Assert.Single(second.LinkedChildren));
            }).Returns(Task.CompletedTask);

        await context.Commands.RerouteLinkedChildReferencesAsync(source, target, TestContext.Current.CancellationToken);

        Assert.Equal(source, manual.ItemId);
        Assert.Same(shortcut, first.LinkedChildren[1]);
        context.Metadata.Verify(m => m.SaveAsync(It.IsAny<BaseItem>(), ItemUpdateType.MetadataEdit, TestContext.Current.CancellationToken, It.IsAny<IEnumerable<string>>()), Times.Exactly(2));
        context.Persistence.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FailedReroute_LeavesCacheAndExportsUntouched()
    {
        var context = new Context();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var parent = context.Parent(new LinkedChild { ItemId = source, Type = LinkedChildType.Manual });
        var before = parent.LinkedChildren;
        context.Linked.Setup(l => l.RerouteLinkedChildren(source, target)).Throws(new IOException("SQL rejected"));

        await Assert.ThrowsAsync<IOException>(() => context.Commands.RerouteLinkedChildReferencesAsync(source, target, TestContext.Current.CancellationToken));

        Assert.Same(before, parent.LinkedChildren);
        Assert.NotNull(parent.UserData);
        Assert.Empty(context.Cache.Removed);
        context.Metadata.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CacheReconciliationFailure_EvictsEveryCommittedParent_AndSkipsExports()
    {
        var context = new Context();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        context.Linked.Setup(l => l.RerouteLinkedChildren(source, target)).Returns([first, second]);
        context.Lookup.Setup(l => l.GetItemById(first)).Throws(new IOException("lookup failure after commit"));

        await Assert.ThrowsAsync<IOException>(() => context.Commands.RerouteLinkedChildReferencesAsync(source, target, TestContext.Current.CancellationToken));

        Assert.Equal(new[] { first, second }, context.Cache.Removed);
        context.Metadata.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CancellationDuringExport_LeavesEveryCommittedParentReconciled()
    {
        var context = new Context();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var first = context.Parent(new LinkedChild { ItemId = source, Type = LinkedChildType.Manual });
        var second = context.Parent(new LinkedChild { ItemId = source, Type = LinkedChildType.Manual });
        context.Linked.Setup(l => l.RerouteLinkedChildren(source, target)).Returns([first.Id, second.Id]);
        context.Linked.Setup(l => l.GetLinkedChildren(first.Id)).Returns([new LinkedChild { ItemId = target, Type = LinkedChildType.Manual }]);
        context.Linked.Setup(l => l.GetLinkedChildren(second.Id)).Returns([new LinkedChild { ItemId = target, Type = LinkedChildType.Manual }]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        context.Metadata.Setup(m => m.SaveAsync(It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), cancellation.Token, It.IsAny<IEnumerable<string>>()))
            .Returns(async (BaseItem _, ItemUpdateType _, CancellationToken token, IEnumerable<string> _) =>
            {
                await cancellation.CancelAsync();
                token.ThrowIfCancellationRequested();
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Commands.RerouteLinkedChildReferencesAsync(source, target, cancellation.Token));

        Assert.Equal(target, Assert.Single(first.LinkedChildren).ItemId);
        Assert.Equal(target, Assert.Single(second.LinkedChildren).ItemId);
        context.Metadata.Verify(m => m.SaveAsync(It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>>()), Times.Once);
    }

    [Fact]
    public async Task InvalidOrCancelledAdmission_AndSelfReroute_DoNotMutateAnything()
    {
        var context = new Context();
        var id = Guid.NewGuid();
        await context.Commands.RerouteLinkedChildReferencesAsync(id, id, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ArgumentException>(() => context.Commands.RerouteLinkedChildReferencesAsync(Guid.Empty, id, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => context.Commands.UpsertLinkedChild(id, Guid.Empty, LinkedChildType.Manual, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => context.Commands.UpsertLinkedChild(id, Guid.NewGuid(), (LinkedChildType)99, TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Commands.RerouteLinkedChildReferencesAsync(id, Guid.NewGuid(), cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => context.Commands.UpsertLinkedChild(id, Guid.NewGuid(), LinkedChildType.Manual, cancellation.Token));
        context.Linked.VerifyNoOtherCalls();
        context.Metadata.VerifyNoOtherCalls();
        Assert.Empty(context.Cache.Removed);
    }

    [Fact]
    public void Ownership_RemovesManagerWriteFacadesAndItsMetadataExporterDependency()
    {
        foreach (var owner in new[] { typeof(ILibraryManager), typeof(Tesserafin.Server.Core.Library.LibraryManager) })
        {
            Assert.DoesNotContain(owner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), m => m.Name is "UpsertLinkedChild" or "RerouteLinkedChildReferencesAsync");
        }

        var dependencies = typeof(Tesserafin.Server.Core.Library.LibraryManager).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.DoesNotContain(typeof(IItemMetadataExporter), dependencies);
    }

    private sealed class Context
    {
        public Context()
        {
            Commands = new ItemWriteCommands(new ItemVersionGraphPlanner(Mock.Of<IItemResolutionService>(), Mock.Of<ILibraryItemOptions>(), Lookup.Object, Linked.Object), Persistence.Object, Cache, Lookup.Object, Linked.Object, Metadata.Object, Mock.Of<IFileSystem>(), Mock.Of<IItemChangePublisher>(), new ActiveRecordings(), Mock.Of<INamedItemCatalog>(), new NamedItemIdentity(Mock.Of<IServerConfigurationManager>(), Mock.Of<IFileSystem>()), Mock.Of<IItemResolutionService>(), Mock.Of<IItemRepository>(), Mock.Of<ILibraryItemOptions>(), new FolderChildReconciler(Lookup.Object, Mock.Of<IFileSystem>(), NullLogger<FolderChildReconciler>.Instance), LibraryWriteFixture.CreateCollectionMembershipResolver(), NullLogger<ItemWriteCommands>.Instance);
        }

        public Mock<ILinkedChildrenService> Linked { get; } = new();

        public Mock<IItemLookupService> Lookup { get; } = new();

        public Mock<IItemMetadataExporter> Metadata { get; } = new();

        public Mock<IItemPersistenceService> Persistence { get; } = new();

        public RecordingCache Cache { get; } = new();

        public ItemWriteCommands Commands { get; }

        public Folder Parent(params LinkedChild[] children)
        {
            var parent = new Folder { Id = Guid.NewGuid(), Name = "linked parent", LinkedChildren = children };
            Lookup.Setup(l => l.GetItemById(parent.Id)).Returns(parent);
            return parent;
        }
    }

    private sealed class RecordingCache : IItemCacheStore
    {
        public List<Guid> Removed { get; } = [];

        public void Register(BaseItem item) => throw new InvalidOperationException("Link mutation must not save or register a whole item.");

        public void Remove(Guid id) => Removed.Add(id);

        public void RemoveRange(IEnumerable<Guid> ids) => Removed.AddRange(ids);
    }
}
