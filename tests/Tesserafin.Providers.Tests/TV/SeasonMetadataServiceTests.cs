using System;
using System.Threading;
using System.Threading.Tasks;
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

    [Theory]
    [InlineData(null)]
    [InlineData(7)]
    public async Task BeforeRefresh_UsesTheParentPathOnlyForMissingSeasonNumbers(int? existingNumber)
    {
        var parent = new Series { Id = Guid.NewGuid(), Path = "/media/Example" };
        var season = new Season
        {
            Name = "Season",
            ParentId = parent.Id,
            Path = "/media/Example/Season 02",
            IndexNumber = existingNumber
        };
        var lookup = new Mock<IItemLookupService>(MockBehavior.Strict);
        var naming = new Mock<IItemNamingService>(MockBehavior.Strict);
        if (!existingNumber.HasValue)
        {
            lookup.Setup(l => l.GetItemById(parent.Id)).Returns(parent);
            naming.Setup(n => n.GetSeasonNumberFromPath(season.Path, parent.Path)).Returns(2);
        }

        var service = new TestSeasonMetadataService(Mock.Of<ILibraryItemOptions>(), lookup.Object, naming.Object);
        var changed = await service.PrepareRefreshAsync(season, TestContext.Current.CancellationToken);

        Assert.Equal(!existingNumber.HasValue, changed);
        Assert.Equal(existingNumber ?? 2, season.IndexNumber);
    }

    private static Season Specials() => new() { Id = Guid.NewGuid(), Name = "Original", IndexNumber = 0 };

    private sealed class TestSeasonMetadataService(
        ILibraryItemOptions itemOptions,
        IItemLookupService? lookup = null,
        IItemNamingService? naming = null)
        : SeasonMetadataService(
            Mock.Of<IItemWriteCommands>(),
            Mock.Of<IItemPeopleCommands>(),
            Mock.Of<IServerConfigurationManager>(),
            NullLogger<SeasonMetadataService>.Instance,
            Mock.Of<IProviderManager>(),
            Mock.Of<IFileSystem>(),
            itemOptions,
            naming ?? Mock.Of<IItemNamingService>(),
            Mock.Of<IExternalDataManager>(),
            Mock.Of<IItemRepository>(),
            Mock.Of<IItemSortService>(),
            lookup ?? Mock.Of<IItemLookupService>())
    {
        public ItemUpdateType Prepare(Season season) => BeforeSaveInternal(season, false, ItemUpdateType.None);

        public ValueTask<bool> PrepareRefreshAsync(Season season, CancellationToken cancellationToken)
            => BeforeMetadataRefreshAsync(season, new LibraryOptions(), false, cancellationToken);

        public SeasonInfo ReadLookupInfo(Season season) => GetLookupInfo(season);
    }
}
