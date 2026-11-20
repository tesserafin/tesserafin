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
using Tesserafin.Model.Configuration;
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

    [Theory]
    [InlineData("it", "nl", "de", "pt", "it")]
    [InlineData("", "nl", "de", "pt", "nl")]
    [InlineData("", "", "de", "pt", "de")]
    [InlineData("", "", "", "pt", "pt")]
    [InlineData("", "", "", "", "fr")]
    public void PresentationKey_PreservesProviderPriorityLanguageInheritanceAndLibraryOrder(
        string itemLanguage,
        string parentLanguage,
        string folderLanguage,
        string libraryLanguage,
        string expectedLanguage)
    {
        var parent = new Folder { Id = Guid.NewGuid(), PreferredMetadataLanguage = parentLanguage };
        var series = new Series
        {
            Id = Guid.NewGuid(),
            ParentId = parent.Id,
            PreferredMetadataLanguage = itemLanguage
        };
        series.ProviderIds["Imdb"] = "imdb-1";
        series.ProviderIds["Tvdb"] = "tvdb-2";
        series.ProviderIds["Custom"] = "chosen-id";
        var first = new CollectionFolder { Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff") };
        var second = new CollectionFolder { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), PreferredMetadataLanguage = folderLanguage };
        var lookup = new Mock<IItemLookupService>(MockBehavior.Strict);
        lookup.Setup(l => l.GetItemById(parent.Id)).Returns(parent);
        var collections = new Mock<ICollectionFolderResolver>(MockBehavior.Strict);
        collections.Setup(c => c.GetCollectionFolders(series)).Returns([first, second]);
        var options = new LibraryOptions { EnableAutomaticSeriesGrouping = true, PreferredMetadataLanguage = libraryLanguage };
        var itemOptions = Mock.Of<ILibraryItemOptions>(o => o.GetLibraryOptions(series) == options);
        var configuration = new ServerConfiguration { PreferredMetadataLanguage = "fr" };
        var server = Mock.Of<IServerConfigurationManager>(c => c.Configuration == configuration);
        var service = new TestSeriesMetadataService(
            Mock.Of<IItemRepository>(MockBehavior.Strict),
            lookup.Object,
            CreateLinks(lookup.Object),
            itemOptions,
            collections.Object,
            server);

        var key = service.ReadPresentationKey(series);

        Assert.Equal($"chosen-id-{expectedLanguage}-{first.Id:N}-{second.Id:N}", key);
        collections.Verify(c => c.GetCollectionFolders(series), Times.Once());
    }

    [Fact]
    public void PresentationKey_UsesItemIdWhenGroupingIsDisabled()
    {
        var series = new Series { Id = Guid.NewGuid() };
        series.ProviderIds["Tvdb"] = "tvdb-2";
        var lookup = Mock.Of<IItemLookupService>(MockBehavior.Strict);
        var options = new LibraryOptions { EnableAutomaticSeriesGrouping = false };
        var service = new TestSeriesMetadataService(
            Mock.Of<IItemRepository>(MockBehavior.Strict),
            lookup,
            CreateLinks(lookup),
            Mock.Of<ILibraryItemOptions>(o => o.GetLibraryOptions(series) == options));

        Assert.Equal(series.Id.ToString("N"), service.ReadPresentationKey(series));
    }

    [Theory]
    [InlineData(0, 1, "it", "NL")]
    [InlineData(1, 0, "nl", "IT")]
    [InlineData(2, 3, "de", "PT")]
    [InlineData(3, 4, "pt", "FR")]
    [InlineData(4, 2, "fr", "DE")]
    public void LookupInfo_ResolvesLanguageAndCountryIndependently(
        int languageSource,
        int countrySource,
        string expectedLanguage,
        string expectedCountry)
    {
        var parent = new Folder
        {
            Id = Guid.NewGuid(),
            PreferredMetadataLanguage = languageSource <= 1 ? "nl" : null,
            PreferredMetadataCountryCode = countrySource <= 1 ? "NL" : null
        };
        var series = new Series
        {
            Id = Guid.NewGuid(),
            ParentId = parent.Id,
            PreferredMetadataLanguage = languageSource == 0 ? "it" : null,
            PreferredMetadataCountryCode = countrySource == 0 ? "IT" : null
        };
        var folder = new CollectionFolder
        {
            PreferredMetadataLanguage = languageSource <= 2 ? "de" : null,
            PreferredMetadataCountryCode = countrySource <= 2 ? "DE" : null
        };
        var options = new LibraryOptions
        {
            PreferredMetadataLanguage = languageSource <= 3 ? "pt" : null,
            MetadataCountryCode = countrySource <= 3 ? "PT" : null
        };
        var configuration = new ServerConfiguration { PreferredMetadataLanguage = "fr", MetadataCountryCode = "FR" };
        var lookup = new Mock<IItemLookupService>(MockBehavior.Strict);
        lookup.Setup(l => l.GetItemById(parent.Id)).Returns(parent);
        var collections = new Mock<ICollectionFolderResolver>(MockBehavior.Strict);
        collections.Setup(c => c.GetCollectionFolders(series)).Returns([folder]);
        var service = new TestSeriesMetadataService(
            Mock.Of<IItemRepository>(MockBehavior.Strict),
            lookup.Object,
            CreateLinks(lookup.Object),
            collections: collections.Object,
            configuration: Mock.Of<IServerConfigurationManager>(c => c.Configuration == configuration));

        var info = service.ReadLookupInfo(series, options);

        Assert.Equal(expectedLanguage, info.MetadataLanguage);
        Assert.Equal(expectedCountry, info.MetadataCountryCode);
        collections.Verify(c => c.GetCollectionFolders(series), languageSource >= 2 || countrySource >= 2 ? Times.Once() : Times.Never());
    }

    private static LinkedChildResolver CreateLinks(IItemLookupService lookup)
        => new(
            Mock.Of<IItemRepository>(MockBehavior.Strict),
            lookup,
            Mock.Of<IItemPathLookupService>(MockBehavior.Strict),
            Mock.Of<IFileSystem>(MockBehavior.Strict),
            NullLogger<LinkedChildResolver>.Instance);

    private sealed class TestSeries : Series
    {
        public override string GetClientTypeName() => nameof(Series);
    }

    private sealed class TestSeriesMetadataService(
        IItemRepository repository,
        IItemLookupService lookup,
        LinkedChildResolver links,
        ILibraryItemOptions? itemOptions = null,
        ICollectionFolderResolver? collections = null,
        IServerConfigurationManager? configuration = null)
        : SeriesMetadataService(
            Mock.Of<IItemWriteCommands>(),
            Mock.Of<IItemPeopleCommands>(),
            Mock.Of<IItemDeletionCommands>(),
            configuration ?? Mock.Of<IServerConfigurationManager>(),
            NullLogger<SeriesMetadataService>.Instance,
            Mock.Of<IProviderManager>(),
            Mock.Of<IFileSystem>(),
            itemOptions ?? Mock.Of<ILibraryItemOptions>(o => o.GetLibraryOptions(It.IsAny<BaseItem>()) == new LibraryOptions()),
            Mock.Of<IItemStore>(),
            Mock.Of<IItemNamingService>(),
            Mock.Of<ILocalizationManager>(),
            Mock.Of<IExternalDataManager>(),
            repository,
            Mock.Of<IItemListingServices>(),
            lookup,
            links,
            collections ?? Mock.Of<ICollectionFolderResolver>(MockBehavior.Strict))
    {
        public ItemUpdateType Prepare(Series series) => BeforeSaveInternal(series, false, ItemUpdateType.None);

        public string ReadPresentationKey(Series series) => CreatePresentationUniqueKey(series);

        public SeriesInfo ReadLookupInfo(Series series, LibraryOptions libraryOptions) => GetLookupInfo(series, libraryOptions);
    }
}
