using System;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LibraryTaskScheduler;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
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
        var context = new LibraryScanContext(
            Mock.Of<IItemDeletionCommands>(MockBehavior.Strict),
            Mock.Of<IItemWriteCommands>(MockBehavior.Strict),
            Mock.Of<IItemImageService>(MockBehavior.Strict),
            Mock.Of<IItemResolutionService>(MockBehavior.Strict),
            Mock.Of<ILibraryRefreshProgress>(MockBehavior.Strict),
            scheduler.Object,
            lookup.Object,
            repository.Object,
            store.Object,
            new Lazy<IItemMetadataDispatcher>(() => Mock.Of<IItemMetadataDispatcher>(MockBehavior.Strict)));

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
}
