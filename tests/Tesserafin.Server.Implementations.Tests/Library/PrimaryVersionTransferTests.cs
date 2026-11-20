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
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

public sealed class PrimaryVersionTransferTests
{
    [Fact]
    public async Task Demotion_SavesOneDetachedGraph_KeepingLocalOwnershipAndIndependentLinkedVersions()
    {
        var context = new Context();
        var previous = context.Movie("previous");
        var replacement = context.Movie("replacement");
        var local = context.Movie("local");
        var linked = context.Movie("linked");
        context.Link(previous, local, true);
        context.Link(previous, linked, false);
        context.Metadata.Setup(m => m.SaveAsync(It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>>()))
            .Callback(() =>
            {
                Assert.Equal(previous.Id, local.OwnerId);
                Assert.Equal(previous.Id, linked.PrimaryVersionId);
                Assert.Single(previous.LocalAlternateVersions);
            }).Returns(Task.CompletedTask);

        await context.Commands.DemotePrimaryVersionAsync(previous, replacement, TestContext.Current.CancellationToken);

        Assert.Equal(4, Assert.Single(context.Batches).Count);
        Assert.Equal(replacement.Id, context.Rows[local.Id].OwnerId);
        Assert.Equal(Guid.Empty, context.Rows[linked.Id].OwnerId);
        Assert.Equal(replacement.Id, context.Rows[linked.Id].PrimaryVersionId);
        Assert.Equal(replacement.Id, previous.OwnerId);
        Assert.Equal(replacement.Id, previous.PrimaryVersionId);
        Assert.Empty(previous.LocalAlternateVersions);
        Assert.Equal(new[] { local.Path, previous.Path }.Order(), replacement.LocalAlternateVersions.Order());
        Assert.Equal(linked.Id, Assert.Single(replacement.LinkedAlternateVersions).ItemId);
        Assert.Equal(previous.Id, local.OwnerId);
        Assert.Equal(replacement.Id, Assert.IsType<Movie>(context.Cached[local.Id]).OwnerId);
        context.Linked.Verify(l => l.RerouteLinkedChildren(previous.Id, replacement.Id), Times.Once);
    }

    [Fact]
    public async Task FailedGraphSave_KeepsEveryPublishedRelationshipAndCallerSnapshot_ThenRetries()
    {
        var context = new Context();
        var previous = context.Movie("previous");
        var replacement = context.Movie("replacement");
        var local = context.Movie("local");
        context.Link(previous, local, true);
        context.FailSave = true;

        await Assert.ThrowsAsync<IOException>(() => context.Commands.DemotePrimaryVersionAsync(previous, replacement, TestContext.Current.CancellationToken));

        Assert.Equal(previous.Id, context.Rows[local.Id].OwnerId);
        Assert.Equal(previous.Id, context.Cached[local.Id].OwnerId);
        Assert.Equal(Guid.Empty, previous.OwnerId);
        Assert.Single(previous.LocalAlternateVersions);
        Assert.Empty(replacement.LocalAlternateVersions);
        Assert.Equal(0, context.CacheRegistrations);
        // Reference rerouting is a separate earlier operation; the version graph stays intact.
        context.Linked.Verify(l => l.RerouteLinkedChildren(previous.Id, replacement.Id), Times.Once);
        context.FailSave = false;

        await context.Commands.DemotePrimaryVersionAsync(previous, replacement, TestContext.Current.CancellationToken);

        Assert.Equal(replacement.Id, context.Rows[local.Id].OwnerId);
        Assert.Equal(replacement.Id, previous.OwnerId);
    }

    [Fact]
    public async Task Replacement_CommitsTransferAndReroutesBeforeCatalogueOnlyDeletion_WithCapturedParent()
    {
        var context = new Context();
        var previous = context.Movie("previous");
        var replacement = context.Movie("replacement");
        var local = context.Movie("local");
        context.Link(previous, local, true);
        var parent = new Folder { Id = Guid.NewGuid() };
        previous.ParentId = parent.Id;
        var rerouted = false;
        context.Linked.Setup(l => l.RerouteLinkedChildren(previous.Id, replacement.Id)).Callback(() => rerouted = true).Returns([]);
        var deletions = new Mock<IItemDeletionCommands>();
        deletions.Setup(d => d.DeleteAsync(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>(), parent, It.IsAny<CancellationToken>()))
            .Callback((BaseItem item, DeleteOptions options, BaseItem _, CancellationToken _) =>
            {
                Assert.True(rerouted);
                Assert.False(options.DeleteFileLocation);
                Assert.Equal(previous.Id, item.Id);
                Assert.Empty(Assert.IsType<Movie>(item).LocalAlternateVersions);
                Assert.Null(Assert.IsType<Movie>(item).PrimaryVersionId);
                Assert.Equal(replacement.Id, context.Rows[local.Id].OwnerId);
                Assert.Empty(context.Links[(previous.Id, LinkedChildType.LocalAlternateVersion)]);
            }).Returns(Task.CompletedTask);

        await context.Commands.ReplacePrimaryVersionAsync(previous, replacement, parent, deletions.Object, TestContext.Current.CancellationToken);

        deletions.Verify(d => d.DeleteAsync(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>(), parent, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(parent.Id, previous.ParentId);
        Assert.Equal(local.Path, Assert.Single(replacement.LocalAlternateVersions));
    }

    [Fact]
    public async Task FailedReferenceReroute_LeavesGraphAndDeletionUntouched_ThenRetriesWithoutLosingLinkedVersions()
    {
        var context = new Context();
        var previous = context.Movie("previous");
        var replacement = context.Movie("replacement");
        var local = context.Movie("local");
        var linked = context.Movie("linked");
        context.Link(previous, local, true);
        context.Link(previous, linked, false);
        context.Linked.Setup(l => l.RerouteLinkedChildren(previous.Id, replacement.Id)).Throws(new IOException("reference write rejected"));
        var deletions = new Mock<IItemDeletionCommands>();

        await Assert.ThrowsAsync<IOException>(() => context.Commands.ReplacePrimaryVersionAsync(previous, replacement, new Folder(), deletions.Object, TestContext.Current.CancellationToken));

        Assert.Equal(previous.Id, context.Rows[local.Id].OwnerId);
        Assert.Empty(replacement.LinkedAlternateVersions);
        Assert.Single(previous.LinkedAlternateVersions);
        deletions.VerifyNoOtherCalls();
        context.Linked.Setup(l => l.RerouteLinkedChildren(previous.Id, replacement.Id)).Returns([]);

        await context.Commands.ReplacePrimaryVersionAsync(previous, replacement, new Folder(), deletions.Object, TestContext.Current.CancellationToken);

        Assert.Equal(replacement.Id, context.Rows[local.Id].OwnerId);
        Assert.Equal(Guid.Empty, context.Rows[linked.Id].OwnerId);
        Assert.Equal(linked.Id, Assert.Single(replacement.LinkedAlternateVersions).ItemId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignOwnerOrPrimary_IsRejectedBeforeExportsOrPersistence(bool primaryOnly)
    {
        var context = new Context();
        var previous = context.Movie("previous");
        var replacement = context.Movie("replacement");
        var local = context.Movie("foreign");
        context.Link(previous, local, true);
        if (primaryOnly)
        {
            local.PrimaryVersionId = Guid.NewGuid();
        }
        else
        {
            local.OwnerId = Guid.NewGuid();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Commands.DemotePrimaryVersionAsync(previous, replacement, TestContext.Current.CancellationToken));

        context.Metadata.VerifyNoOtherCalls();
        context.Persistence.VerifyNoOtherCalls();
        Assert.Equal(0, context.CacheRegistrations);
    }

    [Fact]
    public async Task CancelledAdmission_AndSelfTransfer_DoNotReadOrWrite()
    {
        var context = new Context();
        var previous = context.Movie("previous");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Commands.DemotePrimaryVersionAsync(previous, previous, cancellation.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => context.Commands.DemotePrimaryVersionAsync(previous, previous, TestContext.Current.CancellationToken));

        context.Lookup.VerifyNoOtherCalls();
        context.Persistence.VerifyNoOtherCalls();
        context.Metadata.VerifyNoOtherCalls();
    }

    private sealed class Context : IItemCacheStore
    {
        public Context()
        {
            Lookup.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => (Cached.GetValueOrDefault(id) ?? Rows.GetValueOrDefault(id))!);
            Linked.Setup(l => l.GetLinkedChildrenIds(It.IsAny<Guid>(), It.IsAny<int?>()))
                .Returns((Guid id, int? type) => Links.GetValueOrDefault((id, (LinkedChildType)type!.Value)) ?? []);
            Linked.Setup(l => l.RerouteLinkedChildren(It.IsAny<Guid>(), It.IsAny<Guid>())).Returns([]);
            var resolution = new Mock<IItemResolutionService>();
            resolution.Setup(r => r.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>()))
                .Returns((string path, Type type) => Rows.Values.First(item => string.Equals(item.Path, path, StringComparison.Ordinal) && item.GetType() == type).Id);
            var files = new Mock<IFileSystem>();
            files.Setup(f => f.IsPathFile(It.IsAny<string>())).Returns(true);
            Metadata.Setup(m => m.SaveAsync(It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>>())).Returns(Task.CompletedTask);
            Persistence.Setup(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>())).Callback((IReadOnlyList<BaseItem> items, CancellationToken _) =>
            {
                if (FailSave)
                {
                    throw new IOException("graph save rejected");
                }

                Batches.Add(items);
                foreach (var item in items.Cast<Video>())
                {
                    Rows[item.Id] = (Video)item.CopyForWrite();
                }

                foreach (var item in items.Cast<Video>())
                {
                    Links[(item.Id, LinkedChildType.LocalAlternateVersion)] = item.LocalAlternateVersions.Select(path => Rows.Values.First(row => string.Equals(row.Path, path, StringComparison.Ordinal)).Id).ToArray();
                    Links[(item.Id, LinkedChildType.LinkedAlternateVersion)] = item.LinkedAlternateVersions.Select(child => child.ItemId!.Value).ToArray();
                }
            });
            Commands = new ItemWriteCommands(new ItemVersionGraphPlanner(resolution.Object, Mock.Of<ILibraryItemOptions>(), Lookup.Object, Linked.Object), Persistence.Object, this, Lookup.Object, Linked.Object, Metadata.Object, files.Object, Mock.Of<IItemChangePublisher>(), new ActiveRecordings(), Mock.Of<INamedItemCatalog>(), new NamedItemIdentity(Mock.Of<IServerConfigurationManager>(), files.Object), resolution.Object, Mock.Of<IItemRepository>(), Mock.Of<ILibraryItemOptions>(), new FolderChildReconciler(Lookup.Object, Mock.Of<IFileSystem>(), NullLogger<FolderChildReconciler>.Instance), LibraryWriteFixture.CreateCollectionMembershipResolver(), NullLogger<ItemWriteCommands>.Instance);
        }

        public Dictionary<Guid, Video> Rows { get; } = [];

        public Dictionary<Guid, Video> Cached { get; } = [];

        public Dictionary<(Guid Id, LinkedChildType Type), IReadOnlyList<Guid>> Links { get; } = [];

        public List<IReadOnlyList<BaseItem>> Batches { get; } = [];

        public Mock<IItemLookupService> Lookup { get; } = new();

        public Mock<ILinkedChildrenService> Linked { get; } = new();

        public Mock<IItemPersistenceService> Persistence { get; } = new();

        public Mock<IItemMetadataExporter> Metadata { get; } = new();

        public ItemWriteCommands Commands { get; }

        public bool FailSave { get; set; }

        public int CacheRegistrations { get; private set; }

        public void Register(BaseItem item)
        {
            CacheRegistrations++;
            Cached[item.Id] = (Video)item;
        }

        public void Remove(Guid id) => Cached.Remove(id);

        public void RemoveRange(IEnumerable<Guid> ids)
        {
            foreach (var id in ids)
            {
                Cached.Remove(id);
            }
        }

        public Movie Movie(string name)
        {
            var movie = new Movie { Id = Guid.NewGuid(), Name = name, Path = "/media/" + name + ".mkv", DateLastSaved = DateTime.UnixEpoch };
            Rows[movie.Id] = movie;
            Cached[movie.Id] = movie;
            return movie;
        }

        public void Link(Video primary, Video alternate, bool local)
        {
            alternate.SetPrimaryVersionId(primary.Id);
            alternate.OwnerId = local ? primary.Id : Guid.Empty;
            var type = local ? LinkedChildType.LocalAlternateVersion : LinkedChildType.LinkedAlternateVersion;
            Links[(primary.Id, type)] = [alternate.Id];
            if (local)
            {
                primary.LocalAlternateVersions = [alternate.Path];
            }
            else
            {
                primary.LinkedAlternateVersions = [new LinkedChild { ItemId = alternate.Id, Type = type }];
            }
        }
    }
}
