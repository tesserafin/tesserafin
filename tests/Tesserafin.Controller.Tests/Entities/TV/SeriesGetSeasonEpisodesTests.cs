using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Sorting;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Configuration;
using Xunit;

namespace Tesserafin.Controller.Tests.Entities.TV;

/// <summary>
/// Characterization tests for the <see cref="Series.GetSeasonEpisodes"/>/<see cref="Series.GetEpisodes"/>/
/// <see cref="Season.GetEpisodes"/> chain (docs/major-rewrite-plan-v13.md § PR49/N, § PR50/N). These
/// entities read <c>BaseItem.LibraryManager</c>/<c>BaseItem.ConfigurationManager</c> statics, so this
/// class joins <see cref="BaseItemStaticStateFixture"/> like <c>FolderTests</c>.
///
/// This suite deliberately exercises the obsolete static <c>ILibraryManager.Sort</c> fallback
/// (`#pragma warning disable CS0618`, see § PR48/N) -- that fallback is exactly what PR50/N threads
/// an optional <c>IItemSortService</c> alongside, without removing it.
/// </summary>
#pragma warning disable CS0618
[Collection(BaseItemStaticStateFixture.Name)]
public class SeriesGetSeasonEpisodesTests
{
    private static Series CreateSeries(string presentationUniqueKey = "series-key")
        => new Series { Id = Guid.NewGuid(), Name = "Test Series", PresentationUniqueKey = presentationUniqueKey };

    private static Season CreateSeason(int? indexNumber, string presentationUniqueKey = "season-key")
        => new Season
        {
            Id = Guid.NewGuid(),
            Name = indexNumber is null ? "Unassigned Season" : $"Season {indexNumber}",
            IndexNumber = indexNumber,
            PresentationUniqueKey = presentationUniqueKey,
            // Path deliberately left null: LocationType then resolves to Virtual without
            // touching the BaseItem.FileSystem static (see PR50/N test notes below).
        };

    // ParentId/SeasonId are deliberately left unset (Guid.Empty) on every Episode created by this
    // suite: Episode.Season / BaseItem.GetParent() both short-circuit to null when ParentId is
    // empty, without ever touching the BaseItem.LibraryManager static. This lets
    // Series.FilterEpisodesBySeason be exercised with a realistic multi-season episode set while
    // keeping the static surface limited to exactly what each test intends to characterize.
    private static Episode CreateEpisode(
        string name,
        int? parentIndexNumber,
        int? indexNumber,
        int? airsBeforeSeasonNumber = null,
        int? airsAfterSeasonNumber = null,
        int? airsBeforeEpisodeNumber = null)
        => new Episode
        {
            Id = Guid.NewGuid(),
            Name = name,
            ParentIndexNumber = parentIndexNumber,
            IndexNumber = indexNumber,
            AirsBeforeSeasonNumber = airsBeforeSeasonNumber,
            AirsAfterSeasonNumber = airsAfterSeasonNumber,
            AirsBeforeEpisodeNumber = airsBeforeEpisodeNumber,
        };

    private static void SetDisplaySpecialsWithinSeasons(bool value)
    {
        BaseItem.ConfigurationManager = Mock.Of<IServerConfigurationManager>(
            x => x.Configuration == new ServerConfiguration { DisplaySpecialsWithinSeasons = value });
    }

    // ---- Series.FilterEpisodesBySeason (public static, pure) --------------------------------

    [Fact]
    public void FilterEpisodesBySeason_Standard_ReturnsOnlyEpisodesOfThatSeason()
    {
        var season1 = CreateSeason(1);
        var e1x1 = CreateEpisode("S01E01", 1, 1);
        var e1x2 = CreateEpisode("S01E02", 1, 2);
        var e2x1 = CreateEpisode("S02E01", 2, 1);

        var result = Series.FilterEpisodesBySeason([e1x1, e1x2, e2x1], season1, includeSpecials: false).ToList();

        Assert.Equal(new BaseItem[] { e1x1, e1x2 }, result);
    }

    [Fact]
    public void FilterEpisodesBySeason_SpecialsExcludedWhenNotRequested()
    {
        var season1 = CreateSeason(1);
        var e1x1 = CreateEpisode("S01E01", 1, 1);
        var special = CreateEpisode("Special", 0, 1); // season 0, no Airs* redirection

        var result = Series.FilterEpisodesBySeason([e1x1, special], season1, includeSpecials: false).ToList();

        Assert.Equal(new BaseItem[] { e1x1 }, result);
    }

    [Fact]
    public void FilterEpisodesBySeason_SpecialsInterleavedViaAirsAfterSeasonNumber()
    {
        var season1 = CreateSeason(1);
        var e1x1 = CreateEpisode("S01E01", 1, 1);
        // Special that airs after season 1 (AiredSeasonNumber = AirsAfterSeasonNumber ?? AirsBeforeSeasonNumber ?? ParentIndexNumber).
        var special = CreateEpisode("Special", 0, 1, airsAfterSeasonNumber: 1);
        var otherSeasonSpecial = CreateEpisode("Special2", 0, 2, airsAfterSeasonNumber: 2);

        var result = Series.FilterEpisodesBySeason([e1x1, special, otherSeasonSpecial], season1, includeSpecials: true).ToList();

        Assert.Equal(new BaseItem[] { e1x1, special }, result);
    }

    [Fact]
    public void FilterEpisodesBySeason_SpecialsInterleavedViaAirsBeforeSeasonNumber()
    {
        var season2 = CreateSeason(2);
        var e2x1 = CreateEpisode("S02E01", 2, 1);
        var special = CreateEpisode("Special", 0, 1, airsBeforeSeasonNumber: 2, airsBeforeEpisodeNumber: 1);

        var result = Series.FilterEpisodesBySeason([e2x1, special], season2, includeSpecials: true).ToList();

        Assert.Equal(new BaseItem[] { e2x1, special }, result);
    }

    [Fact]
    public void FilterEpisodesBySeason_VirtualSeasonWithUnassignedEpisode_IncludesOrphanEpisode()
    {
        // Neither the season nor the episode carry a season number, and the season has no Path
        // (LocationType.Virtual): the "orphan virtual episode" fallback in FilterEpisodesBySeason
        // applies (episodeItem.Season is null, since ParentId/SeasonId are left unset above).
        var unassignedSeason = CreateSeason(null);
        var orphanEpisode = CreateEpisode("Orphan", null, 1);
        var otherSeasonEpisode = CreateEpisode("S01E01", 1, 1);

        var result = Series.FilterEpisodesBySeason([orphanEpisode, otherSeasonEpisode], unassignedSeason, includeSpecials: true).ToList();

        Assert.Equal(new BaseItem[] { orphanEpisode }, result);
    }

    // ---- Series.GetSeasonEpisodes(Season, User, IEnumerable<BaseItem>, DtoOptions, bool) -------
    // (the overload carrying the CS0618-pragmatized LibraryManager.Sort call, TV/Series.cs:418)

    [Fact]
    public void GetSeasonEpisodes_SeasonZero_SortsBySortName_ViaStaticFallback()
    {
        SetDisplaySpecialsWithinSeasons(true);

        var series = CreateSeries();
        var season0 = CreateSeason(0);
        var special1 = CreateEpisode("Special B", 0, 2);
        var special2 = CreateEpisode("Special A", 0, 1);

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(x => x.Sort(It.IsAny<IEnumerable<BaseItem>>(), It.IsAny<User>(), It.IsAny<IEnumerable<ItemSortBy>>(), It.IsAny<SortOrder>()))
            .Returns((IEnumerable<BaseItem> items, User u, IEnumerable<ItemSortBy> sortBy, SortOrder order) =>
                order == SortOrder.Descending
                    ? items.OrderByDescending(i => i.SortName, StringComparer.OrdinalIgnoreCase)
                    : items.OrderBy(i => i.SortName, StringComparer.OrdinalIgnoreCase));
        BaseItem.LibraryManager = libraryManager.Object;

        var result = series.GetSeasonEpisodes(season0, null, new BaseItem[] { special1, special2 }, new DtoOptions(true), true);

        libraryManager.Verify(
            x => x.Sort(
                It.IsAny<IEnumerable<BaseItem>>(),
                null,
                It.Is<IEnumerable<ItemSortBy>>(s => s.SequenceEqual(new[] { ItemSortBy.SortName })),
                SortOrder.Ascending),
            Times.Once);

        // "ordre des specials (saison 0)": confirms the resulting order, not just the sortBy
        // selection -- special2 ("Special A") sorts before special1 ("Special B") by SortName.
        Assert.Equal(new BaseItem[] { special2, special1 }, result);
    }

    [Fact]
    public void GetSeasonEpisodes_NonZeroSeason_SortsByAiredEpisodeOrder_ViaStaticFallback()
    {
        SetDisplaySpecialsWithinSeasons(true);

        var series = CreateSeries();
        var season1 = CreateSeason(1);
        var e1 = CreateEpisode("S01E01", 1, 1);
        var e2 = CreateEpisode("S01E02", 1, 2);

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(x => x.Sort(It.IsAny<IEnumerable<BaseItem>>(), It.IsAny<User>(), It.IsAny<IEnumerable<ItemSortBy>>(), It.IsAny<SortOrder>()))
            .Returns((IEnumerable<BaseItem> items, User u, IEnumerable<ItemSortBy> sortBy, SortOrder order) => items);
        BaseItem.LibraryManager = libraryManager.Object;

        _ = series.GetSeasonEpisodes(season1, null, new BaseItem[] { e1, e2 }, new DtoOptions(true), true);

        libraryManager.Verify(
            x => x.Sort(
                It.IsAny<IEnumerable<BaseItem>>(),
                null,
                It.Is<IEnumerable<ItemSortBy>>(s => s.SequenceEqual(new[] { ItemSortBy.AiredEpisodeOrder })),
                SortOrder.Ascending),
            Times.Once);
    }

    [Fact]
    public void GetSeasonEpisodes_FiltersBeforeSorting_OnlyPassesMatchingSeasonEpisodesToStaticSort()
    {
        SetDisplaySpecialsWithinSeasons(true);

        var series = CreateSeries();
        var season1 = CreateSeason(1);
        var e1x1 = CreateEpisode("S01E01", 1, 1);
        var e2x1 = CreateEpisode("S02E01", 2, 1); // must be filtered out before Sort is invoked

        IEnumerable<BaseItem>? sortedInput = null;
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(x => x.Sort(It.IsAny<IEnumerable<BaseItem>>(), It.IsAny<User>(), It.IsAny<IEnumerable<ItemSortBy>>(), It.IsAny<SortOrder>()))
            .Returns((IEnumerable<BaseItem> items, User u, IEnumerable<ItemSortBy> sortBy, SortOrder order) =>
            {
                sortedInput = items.ToList();
                return items;
            });
        BaseItem.LibraryManager = libraryManager.Object;

        _ = series.GetSeasonEpisodes(season1, null, new BaseItem[] { e1x1, e2x1 }, new DtoOptions(true), true);

        Assert.Equal(new BaseItem[] { e1x1 }, sortedInput);
    }

    // ---- Series.GetSeasonEpisodes(Season, User, DtoOptions, bool) -- query construction --------
    // (TV/Series.cs:372, delegates to LibraryManager.GetItemList then to the overload above)

    [Fact]
    public void GetSeasonEpisodes_4Arg_ShouldIncludeMissingEpisodesFalse_SetsQueryIsMissingFalse()
    {
        SetDisplaySpecialsWithinSeasons(true);

        var series = CreateSeries();
        var season1 = CreateSeason(1);

        InternalItemsQuery? capturedQuery = null;
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem>());
        libraryManager
            .Setup(x => x.Sort(It.IsAny<IEnumerable<BaseItem>>(), It.IsAny<User>(), It.IsAny<IEnumerable<ItemSortBy>>(), It.IsAny<SortOrder>()))
            .Returns((IEnumerable<BaseItem> items, User u, IEnumerable<ItemSortBy> sortBy, SortOrder order) => items);
        BaseItem.LibraryManager = libraryManager.Object;

        _ = series.GetSeasonEpisodes(season1, null, new DtoOptions(true), shouldIncludeMissingEpisodes: false);

        Assert.NotNull(capturedQuery);
        Assert.False(capturedQuery.IsMissing);
    }

    [Fact]
    public void GetSeasonEpisodes_4Arg_ShouldIncludeMissingEpisodesTrue_LeavesQueryIsMissingUnset()
    {
        SetDisplaySpecialsWithinSeasons(true);

        var series = CreateSeries();
        var season1 = CreateSeason(1);

        InternalItemsQuery? capturedQuery = null;
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem>());
        libraryManager
            .Setup(x => x.Sort(It.IsAny<IEnumerable<BaseItem>>(), It.IsAny<User>(), It.IsAny<IEnumerable<ItemSortBy>>(), It.IsAny<SortOrder>()))
            .Returns((IEnumerable<BaseItem> items, User u, IEnumerable<ItemSortBy> sortBy, SortOrder order) => items);
        BaseItem.LibraryManager = libraryManager.Object;

        _ = series.GetSeasonEpisodes(season1, null, new DtoOptions(true), shouldIncludeMissingEpisodes: true);

        Assert.NotNull(capturedQuery);
        Assert.Null(capturedQuery.IsMissing);
    }

    [Fact]
    public void GetSeasonEpisodes_4Arg_DisplaySpecialsWithinSeasonsTrue_QueriesBySeriesPresentationUniqueKey()
    {
        SetDisplaySpecialsWithinSeasons(true);

        var series = CreateSeries("series-key");
        var season1 = CreateSeason(1, "season-key");

        InternalItemsQuery? capturedQuery = null;
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem>());
        BaseItem.LibraryManager = libraryManager.Object;

        _ = series.GetSeasonEpisodes(season1, null, new DtoOptions(true), true);

        Assert.NotNull(capturedQuery);
        Assert.Equal("series-key", capturedQuery.SeriesPresentationUniqueKey);
        Assert.Null(capturedQuery.AncestorWithPresentationUniqueKey);
    }

    [Fact]
    public void GetSeasonEpisodes_4Arg_DisplaySpecialsWithinSeasonsFalse_QueriesByAncestorWithPresentationUniqueKey()
    {
        SetDisplaySpecialsWithinSeasons(false);

        var series = CreateSeries("series-key");
        var season1 = CreateSeason(1, "season-key");

        InternalItemsQuery? capturedQuery = null;
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem>());
        BaseItem.LibraryManager = libraryManager.Object;

        _ = series.GetSeasonEpisodes(season1, null, new DtoOptions(true), true);

        Assert.NotNull(capturedQuery);
        Assert.Equal("season-key", capturedQuery.AncestorWithPresentationUniqueKey);
        Assert.Null(capturedQuery.SeriesPresentationUniqueKey);
    }

    // ---- PR50/N: optional IItemSortService threaded alongside the static fallback -------------
    // (TV/Series.cs GetSeasonEpisodes/GetEpisodes, TV/Season.cs GetEpisodes)

    [Fact]
    public void GetSeasonEpisodes_WithItemSortServiceProvided_NeverTouchesStaticLibraryManagerSort()
    {
        SetDisplaySpecialsWithinSeasons(true);

        var series = CreateSeries();
        var season1 = CreateSeason(1);
        var e1 = CreateEpisode("S01E01", 1, 1);
        var e2 = CreateEpisode("S01E02", 1, 2);

        // Strict mock with no Sort setup: any call to the static facade throws immediately,
        // proving the service path is used instead when itemSortService is supplied.
        var libraryManager = new Mock<ILibraryManager>(MockBehavior.Strict);
        BaseItem.LibraryManager = libraryManager.Object;

        var sortService = new RecordingItemSortService();

        var result = series.GetSeasonEpisodes(season1, null, new BaseItem[] { e2, e1 }, new DtoOptions(true), true, Mock.Of<IItemListingServices>(l => l.Sort == sortService));

        Assert.Single(sortService.Calls);
        Assert.Equal(new BaseItem[] { e1, e2 }, result);
        libraryManager.VerifyNoOtherCalls();
    }

    [Fact]
    public void GetSeasonEpisodes_BeforeAfterComparison_ServicePathAndStaticFallbackProduceSameOrder()
    {
        // Characterizes the PR50/N wiring itself: the same episode set, sorted through the
        // static ILibraryManager.Sort fallback (pre-existing path, still reachable when
        // itemSortService is null) versus the injected IItemSortService (new path), must yield
        // an identical order. In production, LibraryManager.Sort is a pure 1-line delegation to
        // IItemSortService.Sort (see § PR30/N/PR46/N) -- this test mirrors that relationship by
        // backing the static mock with the exact same RecordingItemSortService instance used for
        // the service path, so any wiring bug (wrong sortBy, wrong sortOrder, unfiltered items)
        // would surface as a mismatch between the two calls rather than being masked by two
        // independently-behaving fakes.
        SetDisplaySpecialsWithinSeasons(true);

        var series = CreateSeries();
        var season1 = CreateSeason(1);
        var e1 = CreateEpisode("S01E01", 1, 1);
        var e2 = CreateEpisode("S01E02", 1, 2);
        var special = CreateEpisode("Special", 0, 1, airsAfterSeasonNumber: 1);
        var otherSeason = CreateEpisode("S02E01", 2, 1);
        var allEpisodes = new BaseItem[] { e2, special, e1, otherSeason };

        var sharedSortService = new RecordingItemSortService();

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(x => x.Sort(It.IsAny<IEnumerable<BaseItem>>(), It.IsAny<User>(), It.IsAny<IEnumerable<ItemSortBy>>(), It.IsAny<SortOrder>()))
            .Returns((IEnumerable<BaseItem> items, User u, IEnumerable<ItemSortBy> sortBy, SortOrder order) => sharedSortService.Sort(items, u, sortBy, order));
        BaseItem.LibraryManager = libraryManager.Object;

        var beforeResult = series.GetSeasonEpisodes(season1, null, allEpisodes, new DtoOptions(true), true);
        var afterResult = series.GetSeasonEpisodes(season1, null, allEpisodes, new DtoOptions(true), true, Mock.Of<IItemListingServices>(l => l.Sort == sharedSortService));

        Assert.Equal(beforeResult, afterResult);
        Assert.Equal(2, sharedSortService.Calls.Count);
    }

    [Fact]
    public void SeriesGetSeasonEpisodes_LegacyOverloadMatchesExplicitNullFallback()
    {
        SetDisplaySpecialsWithinSeasons(true);

        var series = CreateSeries();
        var season1 = CreateSeason(1);
        var e1 = CreateEpisode("S01E01", 1, 1);
        var e2 = CreateEpisode("S01E02", 1, 2);

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(x => x.Sort(It.IsAny<IEnumerable<BaseItem>>(), It.IsAny<User>(), It.IsAny<IEnumerable<ItemSortBy>>(), It.IsAny<SortOrder>()))
            .Returns((IEnumerable<BaseItem> items, User u, IEnumerable<ItemSortBy> sortBy, SortOrder order) => items);
        BaseItem.LibraryManager = libraryManager.Object;

        var legacyResult = series.GetSeasonEpisodes(season1, null, new BaseItem[] { e1, e2 }, new DtoOptions(true), true);
        var explicitNullResult = series.GetSeasonEpisodes(season1, null, new BaseItem[] { e1, e2 }, new DtoOptions(true), true, null);

        Assert.Equal(legacyResult, explicitNullResult);
        libraryManager.Verify(
            x => x.Sort(It.IsAny<IEnumerable<BaseItem>>(), It.IsAny<User>(), It.IsAny<IEnumerable<ItemSortBy>>(), It.IsAny<SortOrder>()),
            Times.Exactly(2));
    }

    [Fact]
    public void SeasonGetEpisodes_LegacyOverloadMatchesExplicitNullFallback()
    {
        SetDisplaySpecialsWithinSeasons(true);

        var series = CreateSeries();
        var season1 = CreateSeason(1);
        var e1 = CreateEpisode("S01E01", 1, 1);

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { e1 });
        libraryManager
            .Setup(x => x.Sort(It.IsAny<IEnumerable<BaseItem>>(), It.IsAny<User>(), It.IsAny<IEnumerable<ItemSortBy>>(), It.IsAny<SortOrder>()))
            .Returns((IEnumerable<BaseItem> items, User u, IEnumerable<ItemSortBy> sortBy, SortOrder order) => items);
        BaseItem.LibraryManager = libraryManager.Object;

        var legacyResult = season1.GetEpisodes(series, null, new DtoOptions(true), true);
        var explicitNullResult = season1.GetEpisodes(series, null, new DtoOptions(true), true, null);

        Assert.Equal(legacyResult, explicitNullResult);
        libraryManager.Verify(
            x => x.Sort(It.IsAny<IEnumerable<BaseItem>>(), It.IsAny<User>(), It.IsAny<IEnumerable<ItemSortBy>>(), It.IsAny<SortOrder>()),
            Times.Exactly(2));
    }

    [Fact]
    public void SeasonGetEpisodes_NoArgOverload_OrphanSeason_ReturnsEmptyWithoutAnyLibraryManagerCall()
    {
        // Season.GetEpisodes() / Season.GetChildren have no IItemSortService to give (fixed
        // virtual GetChildren contract) and must keep compiling/working unchanged after PR50/N.
        // On an orphan Season (no Parent set), Season.Series resolves to null without touching
        // BaseItem.LibraryManager (ParentId.IsEmpty() short-circuit in BaseItem.GetParent()), so
        // Season.GetEpisodes(Series series, ...) short-circuits on "series is null" before ever
        // reaching Series.GetSeasonEpisodes/LibraryManager.Sort. Strict mock proves no repository
        // or sort call happens on this path.
        var season = CreateSeason(1);

        var libraryManager = new Mock<ILibraryManager>(MockBehavior.Strict);
        BaseItem.LibraryManager = libraryManager.Object;

        var result = season.GetEpisodes();

        Assert.Empty(result);
        libraryManager.VerifyNoOtherCalls();
    }

    [Fact]
    public void SeasonGetEpisodes_WithExplicitSeriesAndNoItemSortService_StillFallsBackToStaticLibraryManagerSort()
    {
        // Same chain as the no-arg overload above, but with a real Series supplied directly
        // (Season.GetEpisodes(Series, User, DtoOptions, bool)) so it actually reaches
        // Series.GetSeasonEpisodes and the pragmatized static Sort call -- proving the fallback
        // survives PR50/N through the full Season.GetEpisodes -> Series.GetSeasonEpisodes chain,
        // not just at the Series.GetSeasonEpisodes entry point tested above.
        SetDisplaySpecialsWithinSeasons(true);

        var series = CreateSeries();
        var season1 = CreateSeason(1);
        var e1 = CreateEpisode("S01E01", 1, 1);

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { e1 });
        libraryManager
            .Setup(x => x.Sort(It.IsAny<IEnumerable<BaseItem>>(), It.IsAny<User>(), It.IsAny<IEnumerable<ItemSortBy>>(), It.IsAny<SortOrder>()))
            .Returns((IEnumerable<BaseItem> items, User u, IEnumerable<ItemSortBy> sortBy, SortOrder order) => items);
        BaseItem.LibraryManager = libraryManager.Object;

        var result = season1.GetEpisodes(series, null, new DtoOptions(true), true);

        Assert.Equal(new BaseItem[] { e1 }, result);
        libraryManager.Verify(
            x => x.Sort(It.IsAny<IEnumerable<BaseItem>>(), It.IsAny<User>(), It.IsAny<IEnumerable<ItemSortBy>>(), It.IsAny<SortOrder>()),
            Times.Once);
    }

    [Fact]
    public void SeriesGetCatalogueEpisodes_ReadsPresentationGroupAndDeduplicatesSpecials()
    {
        SetDisplaySpecialsWithinSeasons(true);
        BaseItem.LibraryManager = Mock.Of<ILibraryManager>(MockBehavior.Strict);
        var series = CreateSeries();
        var specials = CreateSeason(0);
        var season = CreateSeason(1);
        var special = CreateEpisode("Special", 0, 1, airsAfterSeasonNumber: 1);
        var missing = CreateEpisode("Missing", 1, 2);
        missing.IsVirtualItem = true;
        var episode = CreateEpisode("S01E01", 1, 1);
        var repository = new Mock<IItemRepository>(MockBehavior.Strict);
        repository.Setup(r => r.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.User == null && q.SeriesPresentationUniqueKey == "series-key" && q.IsMissing == null
                && q.IncludeItemTypes.SequenceEqual(new[] { BaseItemKind.Episode, BaseItemKind.Season }))))
            .Returns([specials, season, missing, special, episode]);

        var result = series.GetCatalogueEpisodes(new DtoOptions(), repository.Object, new RecordingItemSortService());

        Assert.Equal(new BaseItem[] { special, episode, missing }, result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SeasonGetCatalogueEpisodes_ResolvesSeriesAndReadsConfiguredGroup(bool includeSpecials)
    {
        SetDisplaySpecialsWithinSeasons(includeSpecials);
        BaseItem.LibraryManager = Mock.Of<ILibraryManager>(MockBehavior.Strict);
        var series = CreateSeries();
        var season = CreateSeason(1);
        season.SeriesId = series.Id;
        var episode = CreateEpisode("S01E01", 1, 1);
        var special = CreateEpisode("Special", 0, 1, airsAfterSeasonNumber: 1);
        var lookup = new Mock<IItemLookupService>(MockBehavior.Strict);
        lookup.Setup(l => l.GetItemById(series.Id)).Returns(series);
        var repository = new Mock<IItemRepository>(MockBehavior.Strict);
        repository.Setup(r => r.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.User == null && q.IsMissing == null
                && q.IncludeItemTypes.SequenceEqual(new[] { BaseItemKind.Episode })
                && q.SeriesPresentationUniqueKey == (includeSpecials ? "series-key" : null)
                && q.AncestorWithPresentationUniqueKey == (includeSpecials ? null : "season-key"))))
            .Returns([episode, special]);

        var result = season.GetCatalogueEpisodes(new DtoOptions(true), repository.Object, lookup.Object, new RecordingItemSortService());

        BaseItem[] expected = includeSpecials ? [special, episode] : [episode];
        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Records every <see cref="Sort"/> call and applies a deterministic ordering that actually
    /// depends on the requested <c>sortBy</c>/<c>sortOrder</c>/items (SortName lexical order, or a
    /// (ParentIndexNumber, IndexNumber) key for AiredEpisodeOrder) -- not a stand-in for the
    /// production <c>AiredEpisodeOrderComparer</c> (already covered by
    /// <c>AiredEpisodeOrderComparerTests</c> in Tesserafin.Server.Implementations.Tests), but enough to
    /// catch a wiring bug (wrong sortBy, wrong sortOrder, unfiltered items) in either call path.
    /// </summary>
    [Fact]
    public void ChannelSeries_ListsItsEpisodesThroughTheListingChannels()
    {
        SetDisplaySpecialsWithinSeasons(false);
        var series = CreateSeries();
        series.ChannelId = Guid.NewGuid();
        var season = CreateSeason(1);
        var episode = CreateEpisode("E1", 1, 1);
        var channels = new Mock<Tesserafin.Controller.Channels.IChannelManager>(MockBehavior.Strict);
        channels
            .Setup(c => c.GetChannelItemsInternal(It.Is<InternalItemsQuery>(q => q.ChannelIds.Contains(series.ChannelId) && q.ParentId.Equals(season.Id)), It.IsAny<IProgress<double>>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new Tesserafin.Model.Querying.QueryResult<BaseItem>(new BaseItem[] { episode }));
        var listing = Mock.Of<IItemListingServices>(l => l.Channels == channels.Object && l.Sort == new RecordingItemSortService());

        var result = series.GetSeasonEpisodes(season, null, new DtoOptions(true), true, listing);

        Assert.Equal(new BaseItem[] { episode }, result);
        channels.VerifyAll();
    }

    [Fact]
    public void ChannelSeries_WithoutListingServices_ReturnsNothing()
    {
        // The channel lookup failure is swallowed (as channel errors always were): no static fallback exists.
        SetDisplaySpecialsWithinSeasons(false);
        var series = CreateSeries();
        series.ChannelId = Guid.NewGuid();

        Assert.Empty(series.GetSeasonEpisodes(CreateSeason(1), null, new DtoOptions(true), true, null));
    }

    private sealed class RecordingItemSortService : IItemSortService
    {
        public List<(IReadOnlyList<BaseItem> Items, User? User, IReadOnlyList<ItemSortBy> SortBy, SortOrder SortOrder)> Calls { get; } = [];

        public void AddParts(IEnumerable<IBaseItemComparer> itemComparers)
        {
        }

        public IEnumerable<BaseItem> Sort(IEnumerable<BaseItem> items, User? user, IEnumerable<ItemSortBy> sortBy, SortOrder sortOrder)
        {
            var itemList = items.ToList();
            var sortByList = sortBy.ToList();
            Calls.Add((itemList, user, sortByList, sortOrder));

            var useAiredOrder = sortByList.Contains(ItemSortBy.AiredEpisodeOrder);

            IOrderedEnumerable<BaseItem> ordered = sortOrder == SortOrder.Descending
                ? itemList.OrderByDescending(i => Key(i, useAiredOrder))
                : itemList.OrderBy(i => Key(i, useAiredOrder));

            return ordered.ToList();
        }

        public IEnumerable<BaseItem> Sort(IEnumerable<BaseItem> items, User? user, IEnumerable<(ItemSortBy OrderBy, SortOrder SortOrder)> orderBy)
            => throw new NotSupportedException("Series.GetSeasonEpisodes only uses the 4-arg Sort overload.");

        private static string Key(BaseItem item, bool useAiredOrder)
            => useAiredOrder
                ? (((item.ParentIndexNumber ?? -1) * 1000) + (item.IndexNumber ?? -1)).ToString("D10", System.Globalization.CultureInfo.InvariantCulture)
                : item.SortName;
    }
}
