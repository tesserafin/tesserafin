using System;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Sorting;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Providers.TV;
using Xunit;

namespace Tesserafin.Providers.Tests.TV;

public sealed class SeasonMetadataServiceTests
{
    [Fact]
    public void BeforeSave_UsesEachLibrarysSpecialsName_AndPreservesALockedName()
    {
        var first = Specials();
        var second = Specials();
        var locked = Specials();
        locked.LockedFields = [MetadataField.Name];
        var options = new Mock<ILibraryItemOptions>(MockBehavior.Strict);
        options.Setup(o => o.GetLibraryOptions(first)).Returns(new LibraryOptions { SeasonZeroDisplayName = "Hors-série" });
        options.Setup(o => o.GetLibraryOptions(second)).Returns(new LibraryOptions { SeasonZeroDisplayName = "Bonus" });
        var service = new TestSeasonMetadataService(options.Object);

        Assert.True(service.Prepare(first).HasFlag(ItemUpdateType.MetadataEdit));
        Assert.True(service.Prepare(second).HasFlag(ItemUpdateType.MetadataEdit));
        Assert.False(service.Prepare(locked).HasFlag(ItemUpdateType.MetadataEdit));

        Assert.Equal("Hors-série", first.Name);
        Assert.Equal("Bonus", second.Name);
        Assert.Equal("Original", locked.Name);
    }

    [Fact]
    public void BeforeSave_UsesTheInjectedSeriesForPresentationAndProviderKeys()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "Series", PresentationUniqueKey = "series-key" };
        series.ProviderIds["Tvdb"] = "series-provider";
        var season = new Season
        {
            Id = Guid.NewGuid(),
            ParentId = series.Id,
            IndexNumber = 2,
            PreferredMetadataLanguage = "en",
            PreferredMetadataCountryCode = "US"
        };
        var lookup = new Mock<IItemLookupService>(MockBehavior.Strict);
        lookup.Setup(l => l.GetItemById(series.Id)).Returns(series);
        var service = new TestSeasonMetadataService(Mock.Of<ILibraryItemOptions>(MockBehavior.Strict), lookup.Object);

        Assert.True(service.Prepare(season).HasFlag(ItemUpdateType.MetadataImport));
        Assert.Equal(series.Name, season.SeriesName);
        Assert.Equal(series.Id, season.SeriesId);
        Assert.Equal(series.PresentationUniqueKey, season.SeriesPresentationUniqueKey);
        Assert.Equal("series-key-002", season.PresentationUniqueKey);
        Assert.Equal("series-provider", service.ReadLookupInfo(season).SeriesProviderIds["Tvdb"]);
    }

    private static Season Specials() => new() { Id = Guid.NewGuid(), Name = "Original", IndexNumber = 0 };

    private sealed class TestSeasonMetadataService(ILibraryItemOptions itemOptions, IItemLookupService? lookup = null)
        : SeasonMetadataService(
            Mock.Of<IItemWriteCommands>(),
            Mock.Of<IItemPeopleCommands>(),
            Mock.Of<IServerConfigurationManager>(),
            NullLogger<SeasonMetadataService>.Instance,
            Mock.Of<IProviderManager>(),
            Mock.Of<IFileSystem>(),
            itemOptions,
            Mock.Of<IItemNamingService>(),
            Mock.Of<IExternalDataManager>(),
            Mock.Of<IItemRepository>(),
            Mock.Of<IItemSortService>(),
            lookup ?? Mock.Of<IItemLookupService>())
    {
        public ItemUpdateType Prepare(Season season) => BeforeSaveInternal(season, false, ItemUpdateType.None);

        public SeasonInfo ReadLookupInfo(Season season) => GetLookupInfo(season);
    }
}
