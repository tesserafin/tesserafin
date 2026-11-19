using System;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Sorting;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.IO;
using Tesserafin.Providers.TV;
using Xunit;

namespace Tesserafin.Providers.Tests.TV;

public sealed class SeriesMetadataServiceTests
{
    [Fact]
    public void BeforeSave_AggregatesNestedEpisodesAndSavedLinksFromTheCatalogue()
    {
        var newest = new DateTime(2026, 1, 17, 0, 0, 0, DateTimeKind.Utc);
        var nested = new Episode { Id = Guid.NewGuid(), DateCreated = newest.AddDays(-1) };
        var linked = new Episode { Id = Guid.NewGuid(), DateCreated = newest };
        var placeholder = new Episode { Id = Guid.NewGuid(), IsVirtualItem = true, DateCreated = newest.AddDays(1) };
        var season = new Season { Id = Guid.NewGuid() };
        var series = new TestSeries
        {
            Id = Guid.NewGuid(),
            LinkedChildren = [new LinkedChild { ItemId = linked.Id }]
        };
        var repository = new Mock<IItemRepository>(MockBehavior.Strict);
        repository.Setup(r => r.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId.Equals(series.Id)))).Returns([season, placeholder]);
        repository.Setup(r => r.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId.Equals(season.Id)))).Returns([nested]);
        repository.Setup(r => r.GetItemList(It.Is<InternalItemsQuery>(q => q.ItemIds.Length == 1 && q.ItemIds[0].Equals(linked.Id)))).Returns([linked]);
        var lookup = Mock.Of<IItemLookupService>(MockBehavior.Strict);
        var resolver = new LinkedChildResolver(
            repository.Object,
            lookup,
            Mock.Of<IItemPathLookupService>(MockBehavior.Strict),
            Mock.Of<IFileSystem>(MockBehavior.Strict),
            NullLogger<LinkedChildResolver>.Instance);
        var service = new TestSeriesMetadataService(repository.Object, lookup, resolver);

        Assert.True(service.Prepare(series).HasFlag(ItemUpdateType.MetadataImport));
        Assert.Equal(newest, series.DateLastMediaAdded);
        repository.Verify(r => r.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId.Equals(season.Id))), Times.Once());
    }

    private sealed class TestSeries : Series
    {
        public override string GetClientTypeName() => nameof(Series);

        public override string CreatePresentationUniqueKey() => "series-key";
    }

    private sealed class TestSeriesMetadataService(
        IItemRepository repository,
        IItemLookupService lookup,
        LinkedChildResolver links)
        : SeriesMetadataService(
            Mock.Of<IItemWriteCommands>(),
            Mock.Of<IItemPeopleCommands>(),
            Mock.Of<IItemDeletionCommands>(),
            Mock.Of<IServerConfigurationManager>(),
            NullLogger<SeriesMetadataService>.Instance,
            Mock.Of<IProviderManager>(),
            Mock.Of<IFileSystem>(),
            Mock.Of<ILibraryItemOptions>(),
            Mock.Of<IItemStore>(),
            Mock.Of<IItemNamingService>(),
            Mock.Of<ILocalizationManager>(),
            Mock.Of<IExternalDataManager>(),
            repository,
            Mock.Of<IItemSortService>(),
            lookup,
            links)
    {
        public ItemUpdateType Prepare(Series series) => BeforeSaveInternal(series, false, ItemUpdateType.None);
    }
}
