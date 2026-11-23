using System;
using Moq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

/// <summary>
/// Characterization tests for <c>Season.GetSeries(IItemLookupService)</c>, the service-aware
/// sibling of the <c>Season.Series</c> property added in PR88. They prove series resolution goes
/// through the injected lookup and never the static <c>BaseItem.LibraryManager</c> (set to a
/// <see cref="MockBehavior.Strict"/> mock that throws if any member is touched). The parameterless
/// <c>Season.Series</c> property is intentionally left as a static compatibility wrapper; only
/// DynamicImageProvider, the lookup-bearing consumer found in the PR88 audit, was migrated to the
/// new overload. Mirrors <see cref="EpisodeGetSeriesTests"/>.
/// </summary>
[Collection(Tesserafin.Server.Implementations.Tests.Library.LibraryManager.LibraryManagerStaticStateFixture.Name)]
public class SeasonGetSeriesTests
{
    [Fact]
    public void GetSeries_SeriesIdSet_ResolvesViaLookupNotStatic()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "Show" };
        var season = new Season { Id = Guid.NewGuid(), Name = "S1", SeriesId = series.Id };
        var lookup = new Mock<IItemLookupService>();
        lookup.Setup(l => l.GetItemById(series.Id)).Returns(series);

        Assert.Same(series, season.GetSeries(lookup.Object));
    }

    [Fact]
    public void GetSeries_EmptySeriesId_ResolvesViaParentChainThroughLookup()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "Show" };
        var season = new Season { Id = Guid.NewGuid(), Name = "S1", SeriesId = Guid.Empty, ParentId = series.Id };
        var lookup = new Mock<IItemLookupService>();
        lookup.Setup(l => l.GetItemById(series.Id)).Returns(series);

        // FindSeriesId(lookup) walks parents via the lookup (season -> series), then GetSeries resolves
        // the series by id via the lookup. The strict static proves neither hop fell back to the
        // static BaseItem.LibraryManager.
        Assert.Same(series, season.GetSeries(lookup.Object));
    }

    [Fact]
    public void GetSeries_NoSeriesIdAndNoParent_ReturnsNullWithoutLookupOrStatic()
    {
        var season = new Season { Id = Guid.NewGuid(), Name = "S1", SeriesId = Guid.Empty, ParentId = Guid.Empty };
        var lookup = new Mock<IItemLookupService>();

        Assert.Null(season.GetSeries(lookup.Object));
        lookup.Verify(l => l.GetItemById(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public void GetSeries_NullLookup_Throws()
    {
        var season = new Season { Id = Guid.NewGuid(), SeriesId = Guid.NewGuid() };
        Assert.Throws<ArgumentNullException>(() => season.GetSeries(null!));
    }
}
