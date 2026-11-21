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
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Model.MediaInfo;
using Tesserafin.Model.Providers;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

[Collection(Tesserafin.Server.Implementations.Tests.Library.LibraryManager.LibraryManagerStaticStateFixture.Name)]
public sealed class OwnedExtrasCommandsTests
{
    [Fact]
    public async Task Refresh_ReconcilesBonusIds_LeavesAlternateVersionsAndSources_AndRefreshesDetachedCopiesOnce()
    {
        using var context = new Context();
        var extra = new Video { Id = Guid.NewGuid(), Name = "new bonus", ExtraType = ExtraType.Trailer, ParentId = Guid.NewGuid() };
        var removed = new Video { Id = Guid.NewGuid(), OwnerId = context.Owner.Id, ExtraType = ExtraType.Clip };
        var alternate = new Video { Id = Guid.NewGuid(), OwnerId = context.Owner.Id };
        context.Candidates = [extra, extra];
        context.Owned = [removed, alternate];
        var options = context.Options();

        Assert.True(await context.Commands.RefreshExtrasAsync(context.Owner, context.Provider.Object, context.Deletions.Object, options, [], TestContext.Current.CancellationToken));

        context.Deletions.Verify(d => d.DeleteAsync(removed, It.Is<DeleteOptions>(o => !o.DeleteFileLocation), It.IsAny<CancellationToken>()), Times.Once);
        context.Deletions.Verify(d => d.DeleteAsync(alternate, It.IsAny<DeleteOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        var refresh = Assert.Single(context.Refreshed);
        Assert.NotSame(extra, refresh.Item);
        Assert.Equal(context.Owner.Id, refresh.Item.OwnerId);
        Assert.Equal(Guid.Empty, refresh.Item.ParentId);
        Assert.NotEqual(Guid.Empty, extra.ParentId);
        Assert.Equal(Guid.Empty, extra.OwnerId);
        Assert.Equal(context.Owner.Genres, refresh.Item.Genres);
        Assert.NotSame(context.Owner.Genres, refresh.Item.Genres);
        Assert.True(refresh.Options.ForceSave);
        Assert.False(options.ForceSave);
    }

    [Fact]
    public async Task UnchangedExtras_WithOwnedAlternateVersions_DoNotRefreshOrDelete()
    {
        using var context = new Context();
        var extra = new Video { Id = Guid.NewGuid(), OwnerId = context.Owner.Id, ExtraType = ExtraType.Trailer };
        context.Candidates = [extra];
        context.Owned = [extra, new Video { Id = Guid.NewGuid(), OwnerId = context.Owner.Id }];

        Assert.False(await context.Commands.RefreshExtrasAsync(context.Owner, context.Provider.Object, context.Deletions.Object, context.Options(), [], TestContext.Current.CancellationToken));

        Assert.Empty(context.Refreshed);
        context.Deletions.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedMetadata_InheritsOnlyWhenRequested_AndDoesNotMutateCallerOptions(bool inherit)
    {
        using var context = new Context();
        var extra = new Video { Id = Guid.NewGuid(), Overview = "original" };
        var options = context.Options();
        options.SearchResult = new RemoteSearchResult { Name = "owner lookup" };

        await context.Commands.RefreshOwnedMetadataAsync(context.Owner, extra, inherit, context.Provider.Object, context.Deletions.Object, options, TestContext.Current.CancellationToken);

        var refresh = Assert.Single(context.Refreshed);
        Assert.Equal(inherit ? context.Owner.Overview : "original", extra.Overview);
        Assert.Equal(inherit, refresh.Options.ForceSave);
        Assert.NotSame(options, refresh.Options);
        Assert.False(options.ForceSave);
        Assert.Null(refresh.Options.SearchResult);
        Assert.Equal("owner lookup", options.SearchResult.Name);
    }

    [Fact]
    public async Task FailedRefresh_DoesNotPublishStagedOwnershipOrMetadataToTheOriginalCandidate()
    {
        using var context = new Context();
        var extra = new Video { Id = Guid.NewGuid(), ExtraType = ExtraType.Trailer, Overview = "saved" };
        context.Candidates = [extra];
        context.Provider.Setup(p => p.RefreshSingleItem(It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("refresh failed"));

        await Assert.ThrowsAsync<IOException>(() => context.Commands.RefreshExtrasAsync(context.Owner, context.Provider.Object, context.Deletions.Object, context.Options(), [], TestContext.Current.CancellationToken));

        Assert.Equal(Guid.Empty, extra.OwnerId);
        Assert.Equal("saved", extra.Overview);
        Assert.Empty(extra.Genres);
    }

    [Fact]
    public async Task CancelledAdmission_DoesNotDiscoverReadDeleteOrRefresh()
    {
        using var context = new Context();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Commands.RefreshExtrasAsync(context.Owner, context.Provider.Object, context.Deletions.Object, context.Options(), [], cancellation.Token));

        context.Resolution.VerifyNoOtherCalls();
        context.Repository.VerifyNoOtherCalls();
        context.Deletions.VerifyNoOtherCalls();
        context.Provider.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CancellationDuringRefresh_DrainsAlreadyStartedWorkBeforePropagating()
    {
        using var context = new Context();
        using var cancellation = new CancellationTokenSource();
        var first = new Video { Id = Guid.NewGuid(), ExtraType = ExtraType.Trailer };
        context.Candidates = [first, new Video { Id = Guid.NewGuid(), ExtraType = ExtraType.Clip }];
        var release = new TaskCompletionSource<ItemUpdateType>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Provider.Setup(p => p.RefreshSingleItem(It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>())).Returns(CancelAndWaitAsync);

        var refresh = context.Commands.RefreshExtrasAsync(context.Owner, context.Provider.Object, context.Deletions.Object, context.Options(), [], cancellation.Token);
        Assert.False(refresh.IsCompleted);
        release.SetResult(ItemUpdateType.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);

        async Task<ItemUpdateType> CancelAndWaitAsync()
        {
            await cancellation.CancelAsync();
            return await release.Task;
        }
    }

    [Fact]
    public async Task EntityRefresh_PropagatesOwnedRefreshCancellation_WithoutRefreshingTheOwner()
    {
        using var context = new Context();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var owner = new CancelledOwner { Path = "/Owner.mkv" };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.RefreshMetadata(context.Deletions.Object, context.Commands, context.Provider.Object, context.Options(), cancellation.Token));

        context.Provider.VerifyNoOtherCalls();
    }

    [Fact]
    public void Ownership_ManagerAndEntityHaveNoBonusLifecycle_AndResolutionHasNoWriter()
    {
        Assert.DoesNotContain(typeof(ILibraryManager).GetMethods(), m => m.Name == "FindExtras");
        Assert.DoesNotContain(typeof(Tesserafin.Server.Core.Library.LibraryManager).GetMethods(), m => m.Name == "FindExtras");
        Assert.DoesNotContain(typeof(BaseItem).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic), m => m.Name is "RefreshExtras" or "RefreshMetadataForOwnedItem");
        var dependencies = typeof(ItemResolutionService).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.DoesNotContain(typeof(IItemWriteCommands), dependencies);
        Assert.DoesNotContain(typeof(ILibraryManager), dependencies);
        Assert.DoesNotContain(typeof(IProviderManager), dependencies);
    }

    private sealed class CancelledOwner : BaseItem
    {
        protected override bool SupportsOwnedItems => true;

        protected override Task<bool> RefreshedOwnedItems(IItemDeletionCommands deletions, IItemWriteCommands writes, IItemMetadataDispatcher providers, MetadataRefreshOptions options, IReadOnlyList<FileSystemMetadata> fileSystemChildren, CancellationToken cancellationToken)
            => Task.FromCanceled<bool>(cancellationToken);
    }

    private sealed class Context : IDisposable
    {
        private readonly ILibraryManager _previousManager = BaseItem.LibraryManager;

        public Context()
        {
            var configuration = new Mock<IServerConfigurationManager>();
            configuration.SetupGet(c => c.Configuration).Returns(new ServerConfiguration());
            var lookup = new ItemLookupService(Repository.Object, configuration.Object);
            Resolution.Setup(r => r.FindExtras(It.IsAny<BaseItem>(), It.IsAny<IReadOnlyList<FileSystemMetadata>>(), It.IsAny<IDirectoryService>())).Returns(() => Candidates);
            Repository.Setup(r => r.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(() => Owned);
            Provider.Setup(p => p.RefreshSingleItem(It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()))
                .Callback((BaseItem item, MetadataRefreshOptions options, CancellationToken _) => Refreshed.Add((item, options))).ReturnsAsync(ItemUpdateType.MetadataEdit);
            BaseItem.LibraryManager = new Mock<ILibraryManager>(MockBehavior.Strict).Object;
            Commands = new ItemWriteCommands(new ItemVersionGraphPlanner(Resolution.Object, Mock.Of<ILibraryItemOptions>(), lookup, Mock.Of<ILinkedChildrenService>()), Mock.Of<IItemPersistenceService>(), lookup, lookup, Mock.Of<ILinkedChildrenService>(), Mock.Of<IItemMetadataExporter>(), Mock.Of<IFileSystem>(), Mock.Of<IItemChangePublisher>(), new ActiveRecordings(), Mock.Of<INamedItemCatalog>(), new NamedItemIdentity(configuration.Object, Mock.Of<IFileSystem>()), Resolution.Object, Repository.Object, Mock.Of<ILibraryItemOptions>(), new FolderChildReconciler(lookup, NullLogger<FolderChildReconciler>.Instance), LibraryWriteFixture.CreateCollectionMembershipResolver(), Mock.Of<IItemStore>(), NullLogger<ItemWriteCommands>.Instance);
        }

        public Movie Owner { get; } = new() { Id = Guid.NewGuid(), Genres = ["genre"], Studios = ["studio"], Overview = "owner overview" };

        public BaseItem[] Candidates { get; set; } = [];

        public BaseItem[] Owned { get; set; } = [];

        public List<(BaseItem Item, MetadataRefreshOptions Options)> Refreshed { get; } = [];

        public Mock<IItemResolutionService> Resolution { get; } = new();

        public Mock<IItemRepository> Repository { get; } = new();

        public Mock<IItemDeletionCommands> Deletions { get; } = new();

        public Mock<IItemMetadataDispatcher> Provider { get; } = new();

        public ItemWriteCommands Commands { get; }

        public MetadataRefreshOptions Options() => new(Mock.Of<IDirectoryService>());

        public void Dispose()
        {
            BaseItem.LibraryManager = _previousManager;
        }
    }
}
