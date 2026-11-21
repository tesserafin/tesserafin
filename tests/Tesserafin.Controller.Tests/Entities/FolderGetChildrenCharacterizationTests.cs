using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Collections;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Playlists;
using Tesserafin.Controller.Sorting;
using Tesserafin.Controller.Tests.TestSupport;
using Tesserafin.Controller.TV;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Querying;
using Xunit;

namespace Tesserafin.Controller.Tests.Entities;

#pragma warning disable CS0618
[Collection(BaseItemStaticStateFixture.Name)]
public class FolderGetChildrenCharacterizationTests
{
    private static User CreateUser() => new("test-user", "provider", "provider");

    private static Movie CreateMovie(string name)
        => new() { Id = Guid.NewGuid(), Name = name };

    private static void SetStatics(ILibraryManager libraryManager)
    {
        BaseItem.LibraryManager = libraryManager;
    }

    private static (Mock<IChannelManager> ChannelManager, Mock<ICollectionManager> CollectionManager, Mock<IUserViewManager> UserViewManager, Mock<ITVSeriesManager> TvSeriesManager) StrictManagers()
        => (new Mock<IChannelManager>(MockBehavior.Strict),
            new Mock<ICollectionManager>(MockBehavior.Strict),
            new Mock<IUserViewManager>(MockBehavior.Strict),
            new Mock<ITVSeriesManager>(MockBehavior.Strict));

    [Fact]
    public void FolderGetChildren_ThreeArgumentOverload_FiltersWithQuery()
    {
        var user = CreateUser();
        var movie = CreateMovie("Movie");
        var childFolder = new Folder { Id = Guid.NewGuid(), Name = "Folder" };
        var folder = new Folder { Id = Guid.NewGuid(), Children = new BaseItem[] { movie, childFolder } };
        SetStatics(new Mock<ILibraryManager>().Object);

        var result = folder.GetChildren(user, true, new InternalItemsQuery { Visibility = PermissiveVisibility.Scope, IsFolder = false });

        Assert.Equal(new BaseItem[] { movie }, result);
    }

    [Fact]
    public void FolderGetChildren_FourArgumentOverload_ReturnsTotalBeforePaging()
    {
        var user = CreateUser();
        var first = CreateMovie("A");
        var second = CreateMovie("B");
        var folder = new Folder { Id = Guid.NewGuid(), Children = new BaseItem[] { first, second } };
        SetStatics(new Mock<ILibraryManager>().Object);

        var result = folder.GetChildren(user, true, out var totalItemCount, new InternalItemsQuery { Visibility = PermissiveVisibility.Scope, Limit = 1 });

        Assert.Equal(2, totalItemCount);
        Assert.Equal(new BaseItem[] { first }, result);
    }

    [Fact]
    public void FolderGetRecursiveChildren_UserQuery_IncludesNestedVisibleChildren()
    {
        var user = CreateUser();
        var grandchild = CreateMovie("Grandchild");
        var childFolder = new Folder { Id = Guid.NewGuid(), Name = "Child", Children = new BaseItem[] { grandchild } };
        var root = new Folder { Id = Guid.NewGuid(), Children = new BaseItem[] { childFolder } };
        SetStatics(new Mock<ILibraryManager>().Object);

        var result = root.GetRecursiveChildren(user, new InternalItemsQuery(user) { Visibility = PermissiveVisibility.Scope }, out var totalCount);

        Assert.Equal(2, totalCount);
        Assert.Equal(new BaseItem[] { childFolder, grandchild }, result);
    }

    [Fact]
    public void FolderGetRecursiveChildren_FilterOverload_IncludesNestedMatchingChildren()
    {
        var grandchild = CreateMovie("Grandchild");
        var childFolder = new Folder { Id = Guid.NewGuid(), Name = "Child", Children = new BaseItem[] { grandchild } };
        var root = new Folder { Id = Guid.NewGuid(), Children = new BaseItem[] { childFolder } };

        var result = root.GetRecursiveChildren(i => !i.IsFolder);

        Assert.Equal(new BaseItem[] { grandchild }, result);
    }

    [Fact]
    public void UserViewGetChildren_DelegatesThroughResolvedParentLegacyPath()
    {
        var user = CreateUser();
        var child = CreateMovie("Child");
        var parent = new Folder { Id = Guid.NewGuid(), Children = new BaseItem[] { child } };
        var userView = new UserView { Id = Guid.NewGuid(), DisplayParentId = parent.Id };

        var libraryManager = new Mock<ILibraryManager>();
        var reads = new Mock<IItemReadServices>();
        reads.Setup(x => x.GetItemById(parent.Id)).Returns(parent);
        userView.ReadServices = reads.Object;
        SetStatics(libraryManager.Object);

        var (channelManager, collectionManager, userViewManager, tvSeriesManager) = StrictManagers();
        var listing = Mock.Of<IItemListingServices>(l =>
            l.Channels == channelManager.Object
            && l.Collections == collectionManager.Object
            && l.UserViews == userViewManager.Object
            && l.TVSeries == tvSeriesManager.Object);

        var result = userView.GetChildren(user, true, new InternalItemsQuery(user) { Visibility = PermissiveVisibility.Scope, Listing = listing });

        Assert.Equal(new BaseItem[] { child }, result);
        channelManager.VerifyNoOtherCalls();
        collectionManager.VerifyNoOtherCalls();
        userViewManager.VerifyNoOtherCalls();
        tvSeriesManager.VerifyNoOtherCalls();
    }

    [Fact]
    public void SeasonGetChildren_ReturnsSeriesEpisodesForSeason()
    {
        var user = CreateUser();
        var series = new Series
        {
            Id = Guid.NewGuid(),
            Name = "Series",
            PresentationUniqueKey = "series-key",
            ReadServices = Mock.Of<IItemReadServices>(reads => reads.DisplaySpecialsWithinSeasons)
        };
        var season = new Season { Id = Guid.NewGuid(), Name = "Season 1", IndexNumber = 1, SeriesId = series.Id };
        var episode = new Episode { Id = Guid.NewGuid(), Name = "Episode", ParentIndexNumber = 1, IndexNumber = 1 };

        var queries = new Mock<IItemQueryService>();
        var libraryManager = new Mock<ILibraryManager>();
        season.ReadServices = Mock.Of<IItemReadServices>(reads => reads.GetItemById(series.Id) == series);
        queries.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem> { episode });
        libraryManager
            .Setup(x => x.Sort(It.IsAny<IEnumerable<BaseItem>>(), user, It.IsAny<IEnumerable<ItemSortBy>>(), SortOrder.Ascending))
            .Returns((IEnumerable<BaseItem> items, User _, IEnumerable<ItemSortBy> _, SortOrder _) => items);
        SetStatics(libraryManager.Object);

        var result = season.GetChildren(user, true, new InternalItemsQuery(user) { Visibility = PermissiveVisibility.Scope, Listing = Mock.Of<IItemListingServices>(l => l.Queries == queries.Object) });

        Assert.Equal(new BaseItem[] { episode }, result);
    }

    [Fact]
    public void SeriesGetChildren_ReturnsSeasonsFromLibraryQuery()
    {
        var user = CreateUser();
        var series = new Series
        {
            Id = Guid.NewGuid(),
            Name = "Series",
            PresentationUniqueKey = "series-key",
            ReadServices = Mock.Of<IItemReadServices>(reads => reads.DisplaySpecialsWithinSeasons)
        };
        var season = new Season { Id = Guid.NewGuid(), Name = "Season 1", IndexNumber = 1 };

        var queries = new Mock<IItemQueryService>();
        var libraryManager = new Mock<ILibraryManager>();
        queries.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem> { season });
        SetStatics(libraryManager.Object);

        var result = series.GetChildren(user, true, new InternalItemsQuery(user) { Visibility = PermissiveVisibility.Scope, Listing = Mock.Of<IItemListingServices>(l => l.Queries == queries.Object) });

        Assert.Equal(new BaseItem[] { season }, result);
    }

    [Fact]
    public void PlaylistGetChildren_ReturnsOnlyPlayableItems()
    {
        var user = CreateUser();
        var movie = CreateMovie("Movie");
        var childFolder = new Folder { Id = Guid.NewGuid(), Name = "Folder" };
        var playlist = new Playlist { Id = Guid.NewGuid(), Children = new BaseItem[] { movie, childFolder } };
        SetStatics(new Mock<ILibraryManager>().Object);

        var result = playlist.GetChildren(user, true, PermissiveVisibility.Query(user));

        Assert.Equal(new BaseItem[] { movie }, result);
    }

    [Fact]
    public void BoxSetGetChildren_ResultMatchesFourArgumentPath()
    {
        var user = CreateUser();
        var alpha = CreateMovie("Alpha");
        var zulu = CreateMovie("Zulu");
        var boxSet = new BoxSet { Id = Guid.NewGuid(), DisplayOrder = nameof(ItemSortBy.SortName), Children = new BaseItem[] { zulu, alpha } };
        SetStatics(CreateSortingLibraryManager().Object);

        var threeArgResult = boxSet.GetChildren(user, true, PermissiveVisibility.Query(user));
        var fourArgResult = boxSet.GetChildren(user, true, out _, PermissiveVisibility.Query(user));

        Assert.Equal(new BaseItem[] { alpha, zulu }, threeArgResult);
        Assert.Equal(threeArgResult, fourArgResult);
    }

    [Fact]
    public void BoxSetGetChildren_ThreeArgumentLegacyPathCurrentlySortsTwice()
    {
        var user = CreateUser();
        var alpha = CreateMovie("Alpha");
        var zulu = CreateMovie("Zulu");
        var boxSet = new BoxSet { Id = Guid.NewGuid(), DisplayOrder = nameof(ItemSortBy.SortName), Children = new BaseItem[] { zulu, alpha } };
        var libraryManager = CreateSortingLibraryManager();
        SetStatics(libraryManager.Object);

        _ = boxSet.GetChildren(user, true, PermissiveVisibility.Query(user));

        libraryManager.Verify(
            x => x.Sort(It.IsAny<IEnumerable<BaseItem>>(), user, It.Is<IEnumerable<ItemSortBy>>(s => s.SequenceEqual(new[] { ItemSortBy.SortName })), SortOrder.Ascending),
            Times.Exactly(2));
    }

    [Fact]
    public void BoxSetGetRecursiveChildren_SortsRecursiveResultOnce()
    {
        var user = CreateUser();
        var alpha = CreateMovie("Alpha");
        var zulu = CreateMovie("Zulu");
        var boxSet = new BoxSet { Id = Guid.NewGuid(), DisplayOrder = nameof(ItemSortBy.SortName), Children = new BaseItem[] { zulu, alpha } };
        var libraryManager = CreateSortingLibraryManager();
        SetStatics(libraryManager.Object);

        var result = boxSet.GetRecursiveChildren(user, new InternalItemsQuery(user) { Visibility = PermissiveVisibility.Scope }, out var totalCount);

        Assert.Equal(2, totalCount);
        Assert.Equal(new BaseItem[] { alpha, zulu }, result);
        libraryManager.Verify(
            x => x.Sort(It.IsAny<IEnumerable<BaseItem>>(), user, It.Is<IEnumerable<ItemSortBy>>(s => s.SequenceEqual(new[] { ItemSortBy.SortName })), SortOrder.Ascending),
            Times.Once);
    }

    [Fact]
    public void BoxSetGetChildren_ServiceAwarePathSortsOnceWithoutStaticSort()
    {
        var user = CreateUser();
        var alpha = CreateMovie("Alpha");
        var zulu = CreateMovie("Zulu");
        var boxSet = new BoxSet { Id = Guid.NewGuid(), DisplayOrder = nameof(ItemSortBy.SortName), Children = new BaseItem[] { zulu, alpha } };
        var libraryManager = new Mock<ILibraryManager>(MockBehavior.Strict);
        SetStatics(libraryManager.Object);
        var sortService = new RecordingItemSortService();

        var result = boxSet.GetChildren(user, true, PermissiveVisibility.Query(user), sortService);

        Assert.Equal(new BaseItem[] { alpha, zulu }, result);
        Assert.Single(sortService.Calls);
        libraryManager.VerifyNoOtherCalls();
    }

    [Fact]
    public void SeasonGetChildren_ServiceAwarePathPassesSortServiceToGetEpisodes()
    {
        var user = CreateUser();
        var series = new Series
        {
            Id = Guid.NewGuid(),
            Name = "Series",
            PresentationUniqueKey = "series-key",
            ReadServices = Mock.Of<IItemReadServices>(reads => reads.DisplaySpecialsWithinSeasons)
        };
        var season = new Season { Id = Guid.NewGuid(), Name = "Season 1", IndexNumber = 1, SeriesId = series.Id };
        var episode = new Episode { Id = Guid.NewGuid(), Name = "Episode", ParentIndexNumber = 1, IndexNumber = 1 };

        var queries = new Mock<IItemQueryService>();
        var libraryManager = new Mock<ILibraryManager>();
        season.ReadServices = Mock.Of<IItemReadServices>(reads => reads.GetItemById(series.Id) == series);
        queries.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem> { episode });
        SetStatics(libraryManager.Object);
        var sortService = new RecordingItemSortService();

        var query = new InternalItemsQuery(user)
        {
            Visibility = PermissiveVisibility.Scope,
            Listing = Mock.Of<IItemListingServices>(l => l.Sort == sortService && l.Queries == queries.Object)
        };
        var result = season.GetChildren(user, true, query, sortService);

        Assert.Equal(new BaseItem[] { episode }, result);
        Assert.Single(sortService.Calls);
        libraryManager.Verify(
            x => x.Sort(It.IsAny<IEnumerable<BaseItem>>(), It.IsAny<User>(), It.IsAny<IEnumerable<ItemSortBy>>(), It.IsAny<SortOrder>()),
            Times.Never);
    }

    [Fact]
    public void UserViewGetChildren_ServiceAwarePathDoesNotUseStaticSort()
    {
        var user = CreateUser();
        var child = CreateMovie("Child");
        var parent = new Folder { Id = Guid.NewGuid(), Children = new BaseItem[] { child } };
        var userView = new UserView { Id = Guid.NewGuid(), DisplayParentId = parent.Id };

        var libraryManager = new Mock<ILibraryManager>(MockBehavior.Strict);
        var reads = new Mock<IItemReadServices>();
        reads.Setup(x => x.GetItemById(parent.Id)).Returns(parent);
        userView.ReadServices = reads.Object;
        SetStatics(libraryManager.Object);

        var (channelManager, collectionManager, userViewManager, tvSeriesManager) = StrictManagers();
        var listing = Mock.Of<IItemListingServices>(l =>
            l.Channels == channelManager.Object
            && l.Collections == collectionManager.Object
            && l.UserViews == userViewManager.Object
            && l.TVSeries == tvSeriesManager.Object);
        var sortService = new RecordingItemSortService();

        var result = userView.GetChildren(
            user,
            true,
            new InternalItemsQuery(user) { Visibility = PermissiveVisibility.Scope, Listing = listing, OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)] },
            sortService);

        Assert.Equal(new BaseItem[] { child }, result);
        Assert.Single(sortService.Calls);
        reads.Verify(x => x.GetItemById(parent.Id), Times.Once);
        libraryManager.VerifyNoOtherCalls();
    }

    [Fact]
    public void UserViewGetRecursiveChildren_ServiceAwarePathDoesNotUseStaticSort()
    {
        var user = CreateUser();
        var child = CreateMovie("Child");
        var root = new Folder { Id = Guid.NewGuid(), Children = new BaseItem[] { child } };
        var userView = new UserView { Id = Guid.NewGuid(), ViewType = CollectionType.folders };

        var libraryManager = new Mock<ILibraryManager>(MockBehavior.Strict);
        libraryManager.Setup(x => x.GetUserRootFolder()).Returns(root);
        SetStatics(libraryManager.Object);

        var (channelManager, collectionManager, userViewManager, tvSeriesManager) = StrictManagers();
        var listing = Mock.Of<IItemListingServices>(l =>
            l.Channels == channelManager.Object
            && l.Collections == collectionManager.Object
            && l.UserViews == userViewManager.Object
            && l.TVSeries == tvSeriesManager.Object);
        var sortService = new RecordingItemSortService();

        var result = userView.GetRecursiveChildren(
            user,
            new InternalItemsQuery(user) { Visibility = PermissiveVisibility.Scope, Listing = listing, OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)] },
            out var totalCount,
            sortService);

        Assert.Equal(1, totalCount);
        Assert.Equal(new BaseItem[] { child }, result);
        Assert.Single(sortService.Calls);
        libraryManager.Verify(x => x.GetUserRootFolder(), Times.Once);
        libraryManager.VerifyNoOtherCalls();
    }

    private static Mock<ILibraryManager> CreateSortingLibraryManager()
    {
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(x => x.Sort(It.IsAny<IEnumerable<BaseItem>>(), It.IsAny<User>(), It.IsAny<IEnumerable<ItemSortBy>>(), It.IsAny<SortOrder>()))
            .Returns((IEnumerable<BaseItem> items, User _, IEnumerable<ItemSortBy> sortBy, SortOrder sortOrder) =>
                sortOrder == SortOrder.Descending
                    ? items.OrderByDescending(i => i.SortName, StringComparer.OrdinalIgnoreCase)
                    : items.OrderBy(i => i.SortName, StringComparer.OrdinalIgnoreCase));

        return libraryManager;
    }

    [Fact]
    public void UserRootFolderChildren_AfterTheFirstLoad_AreTheItemCacheInstancesInTheLoadedOrder()
    {
        // MEDIA-ACCESS-R2: the repository's copies of the libraries are not the instances an edit
        // through the item cache changes, so only the first read may return them.
        var first = new CollectionFolder { Id = Guid.NewGuid(), Name = "First" };
        var missing = new CollectionFolder { Id = Guid.NewGuid(), Name = "Missing" };
        var last = new CollectionFolder { Id = Guid.NewGuid(), Name = "Last" };
        var cachedFirst = new CollectionFolder { Id = first.Id, Name = "First" };
        var cachedLast = new CollectionFolder { Id = last.Id, Name = "Last" };
        var root = new UserRootFolder { Id = Guid.NewGuid() };

        var reads = new Mock<IItemReadServices>();
        reads.Setup(x => x.GetChildren(root)).Returns(new BaseItem[] { first, missing, last });
        root.ReadServices = reads.Object;
        reads.Setup(x => x.GetItemById(first.Id)).Returns(cachedFirst);
        reads.Setup(x => x.GetItemById(last.Id)).Returns(cachedLast);
        Assert.Equal(new BaseItem[] { first, missing, last }, root.Children);
        Assert.Equal(new BaseItem[] { cachedFirst, cachedLast }, root.Children);

        var replaced = new CollectionFolder { Id = last.Id, Name = "Last" };
        reads.Setup(x => x.GetItemById(last.Id)).Returns(replaced);
        Assert.Equal(new BaseItem[] { cachedFirst, replaced }, root.Children);

        reads.Verify(x => x.GetChildren(root), Times.Once);
    }

    [Fact]
    public void AggregateFolderChildren_RehydrateCachedIdentitiesThroughTheAttachedCatalogue()
    {
        var saved = new Folder { Id = Guid.NewGuid() };
        var cached = new Folder { Id = saved.Id };
        var root = new AggregateFolder();
        var reads = new Mock<IItemReadServices>();
        reads.Setup(x => x.GetChildren(root)).Returns([saved]);
        reads.Setup(x => x.GetItemById(saved.Id)).Returns(cached);
        root.ReadServices = reads.Object;

        Assert.Same(saved, Assert.Single(root.Children));
        root.Children = null;
        Assert.Same(cached, Assert.Single(root.Children!));
        reads.Verify(x => x.GetChildren(root), Times.Once);
    }

    private sealed class RecordingItemSortService : IItemSortService
    {
        public List<(IReadOnlyList<BaseItem> Items, User User, IReadOnlyList<ItemSortBy> SortBy, SortOrder SortOrder)> Calls { get; } = [];

        public void AddParts(IEnumerable<IBaseItemComparer> itemComparers)
        {
        }

        public IEnumerable<BaseItem> Sort(IEnumerable<BaseItem> items, User? user, IEnumerable<ItemSortBy> sortBy, SortOrder sortOrder)
        {
            var itemList = items.ToList();
            var sortByList = sortBy.ToList();
            Calls.Add((itemList, user!, sortByList, sortOrder));

            return sortOrder == SortOrder.Descending
                ? itemList.OrderByDescending(i => i.SortName, StringComparer.OrdinalIgnoreCase)
                : itemList.OrderBy(i => i.SortName, StringComparer.OrdinalIgnoreCase);
        }

        public IEnumerable<BaseItem> Sort(IEnumerable<BaseItem> items, User? user, IEnumerable<(ItemSortBy OrderBy, SortOrder SortOrder)> orderBy)
        {
            var orderByList = orderBy.ToList();
            var firstOrder = orderByList.Count == 0 ? SortOrder.Ascending : orderByList[0].SortOrder;
            var sortByList = orderByList.Select(i => i.OrderBy).ToList();
            var itemList = items.ToList();
            Calls.Add((itemList, user!, sortByList, firstOrder));

            return firstOrder == SortOrder.Descending
                ? itemList.OrderByDescending(i => i.SortName, StringComparer.OrdinalIgnoreCase)
                : itemList.OrderBy(i => i.SortName, StringComparer.OrdinalIgnoreCase);
        }
    }
}
#pragma warning restore CS0618
