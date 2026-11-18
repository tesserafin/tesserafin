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

    private static Season Specials() => new() { Id = Guid.NewGuid(), Name = "Original", IndexNumber = 0 };

    private sealed class TestSeasonMetadataService(ILibraryItemOptions itemOptions)
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
            Mock.Of<IItemSortService>())
    {
        public ItemUpdateType Prepare(Season season) => BeforeSaveInternal(season, false, ItemUpdateType.None);
    }
}
