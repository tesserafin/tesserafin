using System;
using System.Collections.Generic;
using System.IO;
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
using Tesserafin.Data.Enums;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Model.Providers;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

[Collection(Tesserafin.Server.Implementations.Tests.Library.LibraryManager.LibraryManagerStaticStateFixture.Name)]
public sealed class OwnedVideosCommandsTests
{
    [Fact]
    public async Task Refresh_UsesTypedAlternatesAndGenericParts_OnceEach_WithoutSharingTitleMetadataOrOptions()
    {
        using var context = new Context();
        var part = context.Candidate<Video>("/media/part.mkv");
        var alternate = context.Candidate<Movie>("/media/alternate.mkv");
        alternate.Overview = "alternate description";
        context.Owner.AdditionalParts = [part.Path, part.Path];
        context.Owner.LocalAlternateVersions = [alternate.Path, alternate.Path, "/media/missing.mkv"];
        context.Files.Setup(f => f.FileExists("/media/missing.mkv")).Returns(false);
        var options = context.Options();
        options.SearchResult = new RemoteSearchResult { Name = "owner search" };

        Assert.True(await context.Commands.RefreshOwnedVideosAsync(context.Owner, context.Provider.Object, context.Deletions.Object, options, TestContext.Current.CancellationToken));

        Assert.Equal(new[] { alternate.Path }, context.Owner.LocalAlternateVersions);
        Assert.Equal(2, context.Refreshed.Count);
        var refreshedPart = Assert.Single(context.Refreshed, r => r.Item.Id.Equals(part.Id));
        Assert.IsType<Video>(refreshedPart.Item);
        Assert.Equal(context.Owner.Overview, refreshedPart.Item.Overview);
        Assert.NotSame(context.Owner.Genres, refreshedPart.Item.Genres);
        var refreshedAlternate = Assert.Single(context.Refreshed, r => r.Item.Id.Equals(alternate.Id));
        Assert.IsType<Movie>(refreshedAlternate.Item);
        Assert.Equal("alternate description", refreshedAlternate.Item.Overview);
        Assert.Equal(context.Owner.Id, refreshedAlternate.Item.PrimaryVersionId);
        Assert.All(context.Refreshed, r =>
        {
            Assert.Equal(context.Owner.Id, r.Item.OwnerId);
            Assert.NotSame(options, r.Options);
            Assert.Null(r.Options.SearchResult);
        });
        Assert.Equal("owner search", options.SearchResult.Name);
        Assert.False(options.ForceSave);
        Assert.Equal(Guid.Empty, alternate.OwnerId);
        context.Linked.Verify(l => l.UpsertLinkedChild(context.Owner.Id, alternate.Id, LinkedChildType.LocalAlternateVersion), Times.Once);
        context.Resolution.Verify(r => r.ResolveAlternateVersion(part.Path, typeof(Video), null, CollectionType.movies), Times.Once);
        context.Resolution.Verify(r => r.ResolveAlternateVersion(alternate.Path, typeof(Movie), null, CollectionType.movies), Times.Once);
    }

    [Fact]
    public async Task ExistingVersion_FailedRefreshLeavesPublishedOwnershipLinksAndPathsUntouched_ThenRetries()
    {
        using var context = new Context();
        var alternate = context.Candidate<Movie>("/media/existing.mkv");
        context.Persisted[alternate.Id] = alternate;
        context.Cache.Register(alternate);
        context.Owner.LocalAlternateVersions = [alternate.Path, "/media/missing.mkv"];
        context.Files.Setup(f => f.FileExists("/media/missing.mkv")).Returns(false);
        context.Provider.Setup(p => p.RefreshSingleItem(It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("refresh rejected"));

        await Assert.ThrowsAsync<IOException>(() => context.Commands.RefreshOwnedVideosAsync(context.Owner, context.Provider.Object, context.Deletions.Object, context.Options(), TestContext.Current.CancellationToken));

        Assert.Equal(Guid.Empty, alternate.OwnerId);
        Assert.Null(alternate.PrimaryVersionId);
        Assert.Same(alternate, context.Lookup.GetItemById(alternate.Id));
        Assert.Equal(2, context.Owner.LocalAlternateVersions.Length);
        context.Linked.Verify(l => l.UpsertLinkedChild(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<LinkedChildType>()), Times.Never);
        context.Persistence.Verify(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()), Times.Never);

        context.Provider.Setup(p => p.RefreshSingleItem(It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async (BaseItem item, MetadataRefreshOptions _, CancellationToken token) =>
            {
                await context.Commands.UpdateItemAsync(item, null, ItemUpdateType.MetadataEdit, token);
                return ItemUpdateType.MetadataEdit;
            });
        Assert.True(await context.Commands.RefreshOwnedVideosAsync(context.Owner, context.Provider.Object, context.Deletions.Object, context.Options(), TestContext.Current.CancellationToken));
        var saved = Assert.IsType<Movie>(context.Persisted[alternate.Id]);
        Assert.Equal(context.Owner.Id, saved.OwnerId);
        Assert.Equal(context.Owner.Id, saved.PrimaryVersionId);
        Assert.Equal(Guid.Empty, alternate.OwnerId);
        Assert.Single(context.Owner.LocalAlternateVersions);
        context.Linked.Verify(l => l.UpsertLinkedChild(context.Owner.Id, alternate.Id, LinkedChildType.LocalAlternateVersion), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignOwnership_IsRejectedBeforeRefreshOrLinking(bool primaryOnly)
    {
        using var context = new Context();
        var alternate = context.Candidate<Movie>("/media/foreign.mkv");
        if (primaryOnly)
        {
            alternate.PrimaryVersionId = Guid.NewGuid();
        }
        else
        {
            alternate.OwnerId = Guid.NewGuid();
        }

        context.Persisted[alternate.Id] = alternate;
        context.Owner.LocalAlternateVersions = [alternate.Path];

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Commands.RefreshOwnedVideosAsync(context.Owner, context.Provider.Object, context.Deletions.Object, context.Options(), TestContext.Current.CancellationToken));

        context.Provider.VerifyNoOtherCalls();
        context.Persistence.VerifyNoOtherCalls();
        context.Deletions.VerifyNoOtherCalls();
        context.Linked.Verify(l => l.UpsertLinkedChild(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<LinkedChildType>()), Times.Never);
    }

    [Fact]
    public async Task ForeignGenericVersion_IsNotDeletedToMakeRoomForATypedAlternate()
    {
        using var context = new Context();
        const string path = "/media/foreign-generic.mkv";
        var generic = context.Candidate<Video>(path);
        generic.OwnerId = Guid.NewGuid();
        context.Persisted[generic.Id] = generic;
        context.Owner.LocalAlternateVersions = [path];

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Commands.RefreshOwnedVideosAsync(context.Owner, context.Provider.Object, context.Deletions.Object, context.Options(), TestContext.Current.CancellationToken));

        context.Deletions.VerifyNoOtherCalls();
        context.Provider.VerifyNoOtherCalls();
        context.Persistence.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelfOwnership_IsRejectedBeforePersistence(bool part)
    {
        using var context = new Context();
        if (part)
        {
            context.Owner.AdditionalParts = [context.Owner.Path];
        }
        else
        {
            context.Owner.LocalAlternateVersions = [context.Owner.Path];
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Commands.RefreshOwnedVideosAsync(context.Owner, context.Provider.Object, context.Deletions.Object, context.Options(), TestContext.Current.CancellationToken));

        context.Provider.VerifyNoOtherCalls();
        context.Persistence.VerifyNoOtherCalls();
        context.Deletions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CreatedPart_FailedProviderRefreshDoesNotChangeItsAlreadyPublishedCreation()
    {
        using var context = new Context();
        var part = context.Candidate<Video>("/media/new-part.mkv");
        context.Owner.AdditionalParts = [part.Path];
        context.Provider.Setup(p => p.RefreshSingleItem(It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("provider rejected"));

        await Assert.ThrowsAsync<IOException>(() => context.Commands.RefreshOwnedVideosAsync(context.Owner, context.Provider.Object, context.Deletions.Object, context.Options(), TestContext.Current.CancellationToken));

        var published = Assert.IsType<Video>(context.Lookup.GetItemById(part.Id));
        Assert.Equal(context.Owner.Id, published.OwnerId);
        Assert.Null(published.Overview);
        Assert.Empty(published.Genres);
        Assert.Null(context.Persisted[part.Id].Overview);
        context.Persistence.Verify(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MissingPart_RemovesOnlyItsOwnedCatalogueEntry()
    {
        using var context = new Context();
        var part = context.Candidate<Video>("/media/gone.mkv");
        part.OwnerId = context.Owner.Id;
        context.Persisted[part.Id] = part;
        context.Owner.AdditionalParts = [part.Path];
        context.Files.Setup(f => f.FileExists(part.Path)).Returns(false);

        Assert.False(await context.Commands.RefreshOwnedVideosAsync(context.Owner, context.Provider.Object, context.Deletions.Object, context.Options(), TestContext.Current.CancellationToken));

        context.Deletions.Verify(d => d.DeleteAsync(part, It.Is<DeleteOptions>(o => !o.DeleteFileLocation), It.IsAny<CancellationToken>()), Times.Once);
        context.Provider.VerifyNoOtherCalls();
        context.Files.Verify(f => f.DeleteFile(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CancellationDuringParts_DrainsStartedRefresh_AndDoesNotPublishPrunedPaths()
    {
        using var context = new Context();
        using var cancellation = new CancellationTokenSource();
        var first = context.Candidate<Video>("/media/first.mkv");
        var second = context.Candidate<Video>("/media/second.mkv");
        context.Persisted[first.Id] = first;
        context.Persisted[second.Id] = second;
        context.Owner.AdditionalParts = [first.Path, second.Path];
        context.Owner.LocalAlternateVersions = ["/media/missing.mkv"];
        context.Files.Setup(f => f.FileExists("/media/missing.mkv")).Returns(false);
        var release = new TaskCompletionSource<ItemUpdateType>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Provider.Setup(p => p.RefreshSingleItem(It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>())).Returns(CancelAndWaitAsync);

        var refresh = context.Commands.RefreshOwnedVideosAsync(context.Owner, context.Provider.Object, context.Deletions.Object, context.Options(), cancellation.Token);
        Assert.False(refresh.IsCompleted);
        release.SetResult(ItemUpdateType.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.Single(context.Owner.LocalAlternateVersions);

        async Task<ItemUpdateType> CancelAndWaitAsync()
        {
            await cancellation.CancelAsync();
            return await release.Task;
        }
    }

    [Fact]
    public async Task CancelledAdmission_DoesNotReadResolveRefreshOrWrite()
    {
        using var context = new Context();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Commands.RefreshOwnedVideosAsync(context.Owner, context.Provider.Object, context.Deletions.Object, context.Options(), cancellation.Token));

        context.Files.VerifyNoOtherCalls();
        context.Resolution.VerifyNoOtherCalls();
        context.Repository.VerifyNoOtherCalls();
        context.Provider.VerifyNoOtherCalls();
        context.Persistence.VerifyNoOtherCalls();
    }

    private sealed class Context : IDisposable
    {
        private readonly ILibraryManager _previousManager = BaseItem.LibraryManager;
        private readonly Dictionary<(string Path, Type Type), Guid> _identities = [];

        public Context()
        {
            var configuration = new Mock<IServerConfigurationManager>();
            configuration.SetupGet(c => c.Configuration).Returns(new ServerConfiguration());
            Repository.Setup(r => r.RetrieveItem(It.IsAny<Guid>())).Returns((Guid id) => Persisted.GetValueOrDefault(id)!);
            Lookup = new ItemLookupService(Repository.Object, configuration.Object);
            Cache = Lookup;
            Files.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
            Files.Setup(f => f.AreEqual(It.IsAny<string>(), It.IsAny<string>())).Returns((string a, string b) => string.Equals(a, b, StringComparison.Ordinal));
            Resolution.Setup(r => r.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>())).Returns((string path, Type type) =>
            {
                if (!_identities.TryGetValue((path, type), out var id))
                {
                    id = Guid.NewGuid();
                    _identities[(path, type)] = id;
                }

                return id;
            });
            Linked.Setup(l => l.GetLinkedChildrenIds(It.IsAny<Guid>(), It.IsAny<int?>())).Returns([]);
            var itemOptions = new Mock<ILibraryItemOptions>();
            itemOptions.Setup(o => o.GetContentType(Owner)).Returns(CollectionType.movies);
            Persistence.Setup(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()))
                .Callback((IReadOnlyList<BaseItem> items, CancellationToken _) =>
                {
                    foreach (var item in items)
                    {
                        Persisted[item.Id] = item.CopyForWrite();
                    }
                });
            Provider.Setup(p => p.RefreshSingleItem(It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()))
                .Callback((BaseItem item, MetadataRefreshOptions options, CancellationToken _) => Refreshed.Add(((Video)item, options))).ReturnsAsync(ItemUpdateType.None);
            BaseItem.LibraryManager = new Mock<ILibraryManager>(MockBehavior.Strict).Object;
            Commands = new ItemWriteCommands(new ItemVersionGraphPlanner(Resolution.Object, itemOptions.Object, Lookup, Linked.Object), Persistence.Object, Cache, Lookup, Linked.Object, Mock.Of<IItemMetadataExporter>(), Files.Object, Mock.Of<IItemChangePublisher>(), new ActiveRecordings(), Mock.Of<INamedItemCatalog>(), new NamedItemIdentity(configuration.Object, Files.Object), Resolution.Object, Repository.Object, itemOptions.Object, new FolderChildReconciler(Lookup, NullLogger<FolderChildReconciler>.Instance), LibraryWriteFixture.CreateCollectionMembershipResolver(), Mock.Of<IItemStore>(), NullLogger<ItemWriteCommands>.Instance);
        }

        public Movie Owner { get; } = new() { Id = Guid.NewGuid(), ParentId = Guid.NewGuid(), Path = "/media/owner.mkv", Overview = "owner description", Genres = ["owner genre"] };

        public Dictionary<Guid, BaseItem> Persisted { get; } = [];

        public ItemLookupService Lookup { get; }

        public IItemCacheStore Cache { get; }

        public Mock<IItemRepository> Repository { get; } = new();

        public Mock<IItemResolutionService> Resolution { get; } = new();

        public Mock<IItemPersistenceService> Persistence { get; } = new();

        public Mock<IFileSystem> Files { get; } = new();

        public Mock<ILinkedChildrenService> Linked { get; } = new();

        public Mock<IItemDeletionCommands> Deletions { get; } = new();

        public Mock<IItemMetadataDispatcher> Provider { get; } = new();

        public List<(Video Item, MetadataRefreshOptions Options)> Refreshed { get; } = [];

        public ItemWriteCommands Commands { get; }

        public MetadataRefreshOptions Options() => new(Mock.Of<IDirectoryService>());

        public T Candidate<T>(string path)
            where T : Video, new()
        {
            var candidate = new T { Id = Guid.NewGuid(), Path = path };
            Resolution.Setup(r => r.GetNewItemId(path, typeof(T))).Returns(candidate.Id);
            Resolution.Setup(r => r.ResolveAlternateVersion(path, typeof(T), null, CollectionType.movies)).Returns(candidate);
            return candidate;
        }

        public void Dispose()
        {
            BaseItem.LibraryManager = _previousManager;
        }
    }
}
