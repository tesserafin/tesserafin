using System;
using Moq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

/// <summary>
/// Characterization tests for <c>Episode.GetSeries(IItemLookupService)</c>, the service-aware
/// sibling of the <c>Episode.Series</c> property added in PR81. They prove series resolution goes
/// through the injected lookup and never the static <c>BaseItem.LibraryManager</c> (set to a
/// <see cref="MockBehavior.Strict"/> mock that throws if any member is touched). The parameterless
/// <c>Episode.Series</c> property is intentionally left as a static compatibility wrapper: only
/// SessionManager, the single lookup-bearing consumer found in the PR81 audit, was migrated to the
/// new overload; the other simple-ID relations (Season.Series, folder children, ...) have no
/// lookup-bearing caller yet and were deferred.
/// </summary>
[Collection(Tesserafin.Server.Implementations.Tests.Library.LibraryManager.LibraryManagerStaticStateFixture.Name)]
public class EpisodeGetSeriesTests
{
    [Fact]
    public void GetSeries_SeriesIdSet_ResolvesViaLookupNotStatic()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "Show" };
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id };
        var lookup = new Mock<IItemLookupService>();
        lookup.Setup(l => l.GetItemById(series.Id)).Returns(series);

        Assert.Same(series, episode.GetSeries(lookup.Object));
    }

    [Fact]
    public void GetSeries_EmptySeriesId_ResolvesViaParentChainThroughLookup()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "Show" };
        var season = new Season { Id = Guid.NewGuid(), Name = "S1", ParentId = series.Id };
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = Guid.Empty, ParentId = season.Id };
        var lookup = new Mock<IItemLookupService>();
        lookup.Setup(l => l.GetItemById(season.Id)).Returns(season);
        lookup.Setup(l => l.GetItemById(series.Id)).Returns(series);

        // FindSeriesId(lookup) walks parents via the lookup (season -> series), then GetSeries resolves
        // the series by id via the lookup. The strict static proves neither hop fell back to the
        // static BaseItem.LibraryManager.
        Assert.Same(series, episode.GetSeries(lookup.Object));
    }

    [Fact]
    public void GetSeries_NoSeriesIdAndNoParent_ReturnsNullWithoutLookupOrStatic()
    {
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = Guid.Empty, ParentId = Guid.Empty };
        var lookup = new Mock<IItemLookupService>();

        Assert.Null(episode.GetSeries(lookup.Object));
        lookup.Verify(l => l.GetItemById(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public void GetSeries_NullLookup_Throws()
    {
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = Guid.NewGuid() };
        Assert.Throws<ArgumentNullException>(() => episode.GetSeries(null!));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FindSeasonId_PrefersThePhysicalParentThenReadsTheSeriesCatalogue(bool hasSeasonParent)
    {
        var series = new Series { Id = Guid.NewGuid() };
        var physicalParent = new Season { Id = Guid.NewGuid(), IndexNumber = 3 };
        var numberedSeason = new Season { Id = Guid.NewGuid(), IndexNumber = 2 };
        var episode = new Episode
        {
            ParentId = hasSeasonParent ? physicalParent.Id : series.Id,
            ParentIndexNumber = numberedSeason.IndexNumber
        };
        var lookup = new Mock<IItemLookupService>(MockBehavior.Strict);
        lookup.Setup(l => l.GetItemById(series.Id)).Returns(series);
        lookup.Setup(l => l.GetItemById(physicalParent.Id)).Returns(physicalParent);
        var repository = new Mock<IItemRepository>(MockBehavior.Strict);
        repository.Setup(r => r.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId.Equals(series.Id)))).Returns([numberedSeason]);

        Assert.Equal(hasSeasonParent ? physicalParent.Id : numberedSeason.Id, episode.FindSeasonId(lookup.Object, repository.Object));
        repository.Verify(r => r.GetItemList(It.IsAny<InternalItemsQuery>()), hasSeasonParent ? Times.Never() : Times.Once());
    }
}
