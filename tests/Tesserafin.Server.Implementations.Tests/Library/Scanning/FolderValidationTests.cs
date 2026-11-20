using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LibraryTaskScheduler;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.IO;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library.Scanning;

public sealed class FolderValidationTests
{
    [Fact]
    public async Task ValidateChildren_RejectsAncestorCycleAndCompletesProgress()
    {
        var first = new Folder { Id = Guid.NewGuid() };
        var second = new Folder { Id = Guid.NewGuid(), ParentId = first.Id };
        first.ParentId = second.Id;
        var folder = new Folder { Id = Guid.NewGuid(), ParentId = first.Id };

        var lookup = new Mock<IItemLookupService>(MockBehavior.Strict);
        lookup.SetupSequence(service => service.GetItemById(first.Id)).Returns(first).Returns(first);
        lookup.SetupSequence(service => service.GetItemById(second.Id)).Returns(second);

        var refreshProgress = new Mock<ILibraryRefreshProgress>(MockBehavior.Strict);
        refreshProgress.Setup(progress => progress.OnRefreshStart(folder));
        refreshProgress.Setup(progress => progress.OnRefreshComplete(folder));
        var context = new Mock<IFolderValidationServices>(MockBehavior.Strict);
        context.SetupGet(services => services.Lookup).Returns(lookup.Object);
        context.SetupGet(services => services.RefreshProgress).Returns(refreshProgress.Object);
        var options = new MetadataRefreshOptions(Mock.Of<IDirectoryService>(MockBehavior.Strict));

        await Assert.ThrowsAsync<InvalidOperationException>(() => folder.ValidateChildren(
            context.Object, new Progress<double>(), options, cancellationToken: TestContext.Current.CancellationToken));

        lookup.Verify(service => service.GetItemById(first.Id), Times.Exactly(2));
        lookup.Verify(service => service.GetItemById(second.Id), Times.Once);
        refreshProgress.Verify(progress => progress.OnRefreshStart(folder), Times.Once);
        refreshProgress.Verify(progress => progress.OnRefreshComplete(folder), Times.Once);
    }

    [Fact]
    public async Task ValidateUserRoot_RegistersFreshLibraryDefinitionsAndResolvesTheirCachedIds()
    {
        var root = new UserRootFolder { Id = Guid.NewGuid() };
        var original = new CollectionFolder { Id = Guid.NewGuid(), Name = "Original" };
        var refreshed = new CollectionFolder { Id = original.Id, Name = "Refreshed" };
        var repository = new Mock<IItemRepository>(MockBehavior.Strict);
        repository.SetupSequence(r => r.GetItemList(It.Is<InternalItemsQuery>(query =>
            query.ParentId.Equals(root.Id) && !query.GroupByPresentationUniqueKey)))
            .Returns([original])
            .Returns([refreshed]);

        var lookup = new Mock<IItemLookupService>(MockBehavior.Strict);
        lookup.Setup(service => service.GetItemById(refreshed.Id)).Returns(refreshed);
        var store = new Mock<IItemStore>(MockBehavior.Strict);
        store.Setup(service => service.RegisterItem(It.Is<BaseItem>(item => ReferenceEquals(item, refreshed))));

        var scheduler = new Mock<ILimitedConcurrencyLibraryScheduler>(MockBehavior.Strict);
        scheduler.Setup(service => service.Enqueue(
            It.Is<BaseItem[]>(items => items.Length == 0),
            It.IsAny<Func<BaseItem, IProgress<double>, Task>>(),
            It.IsAny<IProgress<double>>(),
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var context = CreateServices(
            Mock.Of<IItemDeletionCommands>(MockBehavior.Strict),
            Mock.Of<IItemWriteCommands>(MockBehavior.Strict),
            Mock.Of<IItemImageService>(MockBehavior.Strict),
            Mock.Of<IItemResolutionService>(MockBehavior.Strict),
            Mock.Of<ILibraryRefreshProgress>(MockBehavior.Strict),
            scheduler.Object,
            lookup.Object,
            repository.Object,
            store.Object,
            new Lazy<IItemMetadataDispatcher>(() => Mock.Of<IItemMetadataDispatcher>(MockBehavior.Strict)),
            CreateLinks(repository.Object, lookup.Object));

        await root.ValidateChildren(
            context,
            new Progress<double>(),
            new MetadataRefreshOptions(Mock.Of<IDirectoryService>(MockBehavior.Strict)),
            recursive: false,
            cancellationToken: TestContext.Current.CancellationToken);

        store.Verify(service => service.RegisterItem(It.Is<BaseItem>(item => ReferenceEquals(item, refreshed))), Times.Once);
        Assert.Same(refreshed, Assert.Single(root.GetCatalogueChildren(repository.Object, lookup.Object)));
        lookup.Verify(service => service.GetItemById(refreshed.Id), Times.Once);
    }

    [Fact]
    public void InvalidateCollectionChildren_ReloadsThePhysicalFoldersThroughTheSuppliedCatalogue()
    {
        var physical = new Folder { Id = Guid.NewGuid() };
        var library = new CollectionFolder { PhysicalFolderIds = [physical.Id] };
        var original = new Folder { Id = Guid.NewGuid() };
        var refreshed = new Folder { Id = Guid.NewGuid() };
        var repository = new Mock<IItemRepository>(MockBehavior.Strict);
        repository.SetupSequence(r => r.GetItemList(It.Is<InternalItemsQuery>(query => query.ParentId.Equals(physical.Id))))
            .Returns([original])
            .Returns([refreshed]);
        var lookup = new Mock<IItemLookupService>(MockBehavior.Strict);
        lookup.Setup(service => service.GetItemById(physical.Id)).Returns(physical);

        Assert.Same(original, Assert.Single(library.GetCatalogueChildren(repository.Object, lookup.Object)));
        library.InvalidateCatalogueChildren(lookup.Object);
        Assert.Same(refreshed, Assert.Single(library.GetCatalogueChildren(repository.Object, lookup.Object)));
    }

    [Fact]
    public async Task RefreshSeries_ReadsCatalogueChildrenAndFollowsOnlyTheSeriesLinks()
    {
        var episode = new Episode { Id = Guid.NewGuid() };
        var linkedEpisode = new Episode { Id = episode.Id };
        var linkedFolder = new Folder { Id = Guid.NewGuid() };
        var season = new Season { Id = Guid.NewGuid(), LinkedChildren = [new LinkedChild { ItemId = Guid.NewGuid() }] };
        var series = new Series
        {
            Id = Guid.NewGuid(),
            LinkedChildren = [new LinkedChild { ItemId = episode.Id }, new LinkedChild { ItemId = linkedFolder.Id }]
        };
        var repository = new Mock<IItemRepository>(MockBehavior.Strict);
        repository.Setup(r => r.GetItemList(It.Is<InternalItemsQuery>(query => query.ParentId.Equals(series.Id)))).Returns([season]);
        repository.Setup(r => r.GetItemList(It.Is<InternalItemsQuery>(query => query.ParentId.Equals(season.Id)))).Returns([episode]);
        repository.Setup(r => r.GetItemList(It.Is<InternalItemsQuery>(query =>
            query.ItemIds.Length == 2 && query.ItemIds[0].Equals(episode.Id) && query.ItemIds[1].Equals(linkedFolder.Id))))
            .Returns([linkedEpisode, linkedFolder]);

        var token = TestContext.Current.CancellationToken;
        var refreshed = new List<BaseItem>();
        var metadata = new Mock<IItemMetadataDispatcher>(MockBehavior.Strict);
        metadata.Setup(dispatcher => dispatcher.RefreshSingleItem(It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), token))
            .Callback((BaseItem item, MetadataRefreshOptions _, CancellationToken _) => refreshed.Add(item))
            .ReturnsAsync(ItemUpdateType.None);

        var lookup = Mock.Of<IItemLookupService>(MockBehavior.Strict);
        var context = CreateServices(
            Mock.Of<IItemDeletionCommands>(MockBehavior.Strict),
            Mock.Of<IItemWriteCommands>(MockBehavior.Strict),
            Mock.Of<IItemImageService>(MockBehavior.Strict),
            Mock.Of<IItemResolutionService>(MockBehavior.Strict),
            Mock.Of<ILibraryRefreshProgress>(MockBehavior.Strict),
            Mock.Of<ILimitedConcurrencyLibraryScheduler>(MockBehavior.Strict),
            lookup,
            repository.Object,
            Mock.Of<IItemStore>(MockBehavior.Strict),
            new Lazy<IItemMetadataDispatcher>(() => metadata.Object),
            CreateLinks(repository.Object, lookup));
        var options = new MetadataRefreshOptions(Mock.Of<IDirectoryService>(MockBehavior.Strict))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh
        };

        await series.RefreshAllMetadata(context, options, new Progress<double>(), token);

        Assert.Collection(
            refreshed,
            item => Assert.Same(season, item),
            item => Assert.Same(linkedEpisode, item),
            item => Assert.Same(linkedFolder, item),
            item => Assert.Same(series, item));
    }

    private static LinkedChildResolver CreateLinks(IItemRepository repository, IItemLookupService lookup)
        => new(repository, lookup, Mock.Of<IItemPathLookupService>(MockBehavior.Strict), Mock.Of<IFileSystem>(MockBehavior.Strict), NullLogger<LinkedChildResolver>.Instance);

    private static IFolderValidationServices CreateServices(
        IItemDeletionCommands deletions,
        IItemWriteCommands writes,
        IItemImageService images,
        IItemResolutionService resolution,
        ILibraryRefreshProgress refreshProgress,
        ILimitedConcurrencyLibraryScheduler scheduler,
        IItemLookupService lookup,
        IItemRepository repository,
        IItemStore store,
        Lazy<IItemMetadataDispatcher> metadata,
        LinkedChildResolver links)
    {
        var services = new Mock<IFolderValidationServices>(MockBehavior.Strict);
        services.SetupGet(s => s.Deletions).Returns(deletions);
        services.SetupGet(s => s.Writes).Returns(writes);
        services.SetupGet(s => s.Images).Returns(images);
        services.SetupGet(s => s.Resolution).Returns(resolution);
        services.SetupGet(s => s.RefreshProgress).Returns(refreshProgress);
        services.SetupGet(s => s.Scheduler).Returns(scheduler);
        services.SetupGet(s => s.Lookup).Returns(lookup);
        services.SetupGet(s => s.Repository).Returns(repository);
        services.SetupGet(s => s.Store).Returns(store);
        services.SetupGet(s => s.Links).Returns(links);
        var providers = new Mock<IProviderManager>(MockBehavior.Strict);
        providers.Setup(p => p.RefreshSingleItem(It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()))
            .Returns((BaseItem item, MetadataRefreshOptions options, CancellationToken token) => metadata.Value.RefreshSingleItem(item, options, token));
        services.SetupGet(s => s.Providers).Returns(providers.Object);
        services.SetupGet(s => s.Channels).Returns(Mock.Of<Tesserafin.Controller.Channels.IChannelManager>(MockBehavior.Strict));
        return services.Object;
    }
}
