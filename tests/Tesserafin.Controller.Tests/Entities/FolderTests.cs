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
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Tests.TestSupport;
using Tesserafin.Controller.TV;
using Tesserafin.Data.Entities;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Querying;
using Xunit;

namespace Tesserafin.Controller.Tests.Entities;

// This class deliberately tests the obsolete 5-parameter Folder.GetItems/GetItemList overloads
// (documented legitimate caller) — see docs/major-rewrite-plan-v13.md § PR28/N.
#pragma warning disable CS0618
[Collection(BaseItemStaticStateFixture.Name)]
public class FolderTests
{
    private static (Mock<IChannelManager> ChannelManager, Mock<ICollectionManager> CollectionManager, Mock<IUserViewManager> UserViewManager, Mock<ITVSeriesManager> TvSeriesManager) StrictManagers()
        => (new Mock<IChannelManager>(MockBehavior.Strict),
            new Mock<ICollectionManager>(MockBehavior.Strict),
            new Mock<IUserViewManager>(MockBehavior.Strict),
            new Mock<ITVSeriesManager>(MockBehavior.Strict));

    [Theory]
    [InlineData(20d, 80d, 50d)]
    [InlineData(null, 80d, 80d)]
    [InlineData(null, null, null)]
    public void GetRefreshProgress_CollectionFolder_AveragesActivePhysicalFolders(double? firstProgress, double? secondProgress, double? expected)
    {
        var first = new Folder { Id = Guid.NewGuid() };
        var second = new Folder { Id = Guid.NewGuid() };
        var collection = new CollectionFolder { PhysicalFolderIds = [first.Id, second.Id] };
        var library = new Mock<ILibraryManager>(MockBehavior.Strict);
        library.Setup(manager => manager.GetItemById(first.Id)).Returns(first);
        library.Setup(manager => manager.GetItemById(second.Id)).Returns(second);
        BaseItem.LibraryManager = library.Object;

        var providers = new Mock<IProviderManager>(MockBehavior.Strict);
        providers.Setup(manager => manager.GetRefreshProgress(first.Id)).Returns(firstProgress);
        providers.Setup(manager => manager.GetRefreshProgress(second.Id)).Returns(secondProgress);

        Assert.Equal(expected, collection.GetRefreshProgress(providers.Object));
    }

    [Fact]
    public void GetItems_NonChannelFolder_NoUser_ReturnsChildrenWithoutTouchingOtherManagers()
    {
        var folder = new Folder { Id = Guid.NewGuid() };
        var child1 = new Movie { Id = Guid.NewGuid() };
        var child2 = new Movie { Id = Guid.NewGuid() };
        folder.Children = new BaseItem[] { child1, child2 };

        BaseItem.LibraryManager = new Mock<ILibraryManager>().Object;

        var (channelManager, collectionManager, userViewManager, tvSeriesManager) = StrictManagers();

        var result = folder.GetItems(new InternalItemsQuery { Visibility = PermissiveVisibility.Scope }, channelManager.Object, collectionManager.Object, userViewManager.Object, tvSeriesManager.Object);

        Assert.Equal(2, result.Items.Count);
        Assert.Contains(child1, result.Items);
        Assert.Contains(child2, result.Items);

        // Plain library folder, no user, no BoxSet collapsing: none of the 4 threaded
        // dependencies (replacing the removed BaseItem/Folder statics) should be touched.
        channelManager.VerifyNoOtherCalls();
        collectionManager.VerifyNoOtherCalls();
        userViewManager.VerifyNoOtherCalls();
        tvSeriesManager.VerifyNoOtherCalls();
    }

    [Fact]
    public void GetItems_ChannelFolder_DelegatesToChannelManagerOnly()
    {
        var folder = new Folder { Id = Guid.NewGuid(), ChannelId = Guid.NewGuid() };
        var channelItem = new Movie { Id = Guid.NewGuid() };

        var (channelManager, collectionManager, userViewManager, tvSeriesManager) = StrictManagers();
        channelManager
            .Setup(x => x.GetChannelItemsInternal(It.IsAny<InternalItemsQuery>(), It.IsAny<IProgress<double>>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new Tesserafin.Model.Querying.QueryResult<BaseItem>(0, 1, new BaseItem[] { channelItem }));

        var result = folder.GetItems(new InternalItemsQuery { Visibility = PermissiveVisibility.Scope }, channelManager.Object, collectionManager.Object, userViewManager.Object, tvSeriesManager.Object);

        Assert.Single(result.Items);
        Assert.Equal(channelItem, result.Items[0]);
        collectionManager.VerifyNoOtherCalls();
        userViewManager.VerifyNoOtherCalls();
        tvSeriesManager.VerifyNoOtherCalls();
    }

    [Fact]
    public void GetItemList_NonChannelFolder_NoUser_ReturnsChildrenAsFlatList()
    {
        var folder = new Folder { Id = Guid.NewGuid() };
        var child = new Movie { Id = Guid.NewGuid() };
        folder.Children = new BaseItem[] { child };

        BaseItem.LibraryManager = new Mock<ILibraryManager>().Object;

        var (channelManager, collectionManager, userViewManager, tvSeriesManager) = StrictManagers();

        var result = folder.GetItemList(new InternalItemsQuery { Visibility = PermissiveVisibility.Scope }, channelManager.Object, collectionManager.Object, userViewManager.Object, tvSeriesManager.Object);

        Assert.Single(result);
        Assert.Equal(child, result[0]);
        channelManager.VerifyNoOtherCalls();
        collectionManager.VerifyNoOtherCalls();
        userViewManager.VerifyNoOtherCalls();
        tvSeriesManager.VerifyNoOtherCalls();
    }

    [Fact]
    public void GetItems_NonChannelFolder_WithUser_CollapseBoxSetItemsFalse_SkipsCollectionManager()
    {
        var folder = new Folder { Id = Guid.NewGuid() };
        var child = new Movie { Id = Guid.NewGuid() };
        folder.Children = new BaseItem[] { child };

        var user = new User("test-user", "provider", "provider");

        BaseItem.LibraryManager = new Mock<ILibraryManager>().Object;

        var (channelManager, collectionManager, userViewManager, tvSeriesManager) = StrictManagers();

        // CollapseBoxSetItems explicitly false: PostFilterAndSort still runs (user is not null)
        // but CollapseBoxSetItemsIfNeeded's own gate short-circuits before touching collectionManager.
        var query = new InternalItemsQuery { Visibility = PermissiveVisibility.Scope, User = user, CollapseBoxSetItems = false };

        var result = folder.GetItems(query, channelManager.Object, collectionManager.Object, userViewManager.Object, tvSeriesManager.Object);

        Assert.Single(result.Items);
        Assert.Equal(child, result.Items[0]);
        channelManager.VerifyNoOtherCalls();
        collectionManager.VerifyNoOtherCalls();
        userViewManager.VerifyNoOtherCalls();
        tvSeriesManager.VerifyNoOtherCalls();
    }

    [Fact]
    public void GetItems_FiltersPlayedFavoritesUsingQueryUserData()
    {
        var playedFavorite = new Movie { Id = Guid.NewGuid() };
        var unplayedFavorite = new Movie { Id = Guid.NewGuid() };
        var folder = new Folder { Id = Guid.NewGuid(), Children = [playedFavorite, unplayedFavorite] };
        var user = new User("test-user", "provider", "provider");
        var userData = new Mock<IUserDataManager>(MockBehavior.Strict);
        userData.Setup(manager => manager.GetUserData(user, playedFavorite))
            .Returns(new UserItemData { Key = "played", IsFavorite = true, Played = true });
        userData.Setup(manager => manager.GetUserData(user, unplayedFavorite))
            .Returns(new UserItemData { Key = "unplayed", IsFavorite = true });
        BaseItem.LibraryManager = Mock.Of<ILibraryManager>();

        var query = new InternalItemsQuery
        {
            User = user,
            Visibility = PermissiveVisibility.Scope,
            Listing = Mock.Of<IItemListingServices>(listing => listing.UserData == userData.Object),
            CollapseBoxSetItems = false,
            IsFavorite = true,
            IsPlayed = true
        };

        var (channels, collections, views, series) = StrictManagers();
        var result = folder.GetItems(query, channels.Object, collections.Object, views.Object, series.Object);

        Assert.Same(playedFavorite, Assert.Single(result.Items));
        userData.Verify(manager => manager.GetUserData(user, playedFavorite), Times.Once);
        userData.Verify(manager => manager.GetUserData(user, unplayedFavorite), Times.Once);
    }

    [Fact]
    public void GetItems_NonChannelFolder_WithUser_CollapseBoxSetItemsTrue_InvokesCollectionManager()
    {
        var folder = new Folder { Id = Guid.NewGuid() };
        var child = new Movie { Id = Guid.NewGuid() };
        folder.Children = new BaseItem[] { child };

        var user = new User("test-user", "provider", "provider");

        BaseItem.LibraryManager = new Mock<ILibraryManager>().Object;
        BaseItem.ConfigurationManager = Mock.Of<IServerConfigurationManager>(x => x.Configuration == new ServerConfiguration
        {
            EnableGroupingMoviesIntoCollections = true,
            EnableGroupingShowsIntoCollections = true,
        });

        var (channelManager, collectionManager, userViewManager, tvSeriesManager) = StrictManagers();
        collectionManager
            .Setup(x => x.CollapseItemsWithinBoxSets(It.IsAny<IEnumerable<BaseItem>>(), user))
            .Returns((IEnumerable<BaseItem> items, User _) => items);

        // Explicit CollapseBoxSetItems = true, both grouping settings enabled: PostFilterAndSort
        // must route through CollapseBoxSetItemsIfNeeded -> collectionManager.CollapseItemsWithinBoxSets,
        // the branch left uncovered by PR15 (cf. plan doc, point 1 "reste du point 1").
        var query = new InternalItemsQuery { Visibility = PermissiveVisibility.Scope, User = user, CollapseBoxSetItems = true };

        var result = folder.GetItems(query, channelManager.Object, collectionManager.Object, userViewManager.Object, tvSeriesManager.Object);

        Assert.Single(result.Items);
        Assert.Equal(child, result.Items[0]);
        collectionManager.Verify(x => x.CollapseItemsWithinBoxSets(It.IsAny<IEnumerable<BaseItem>>(), user), Times.Once);
        channelManager.VerifyNoOtherCalls();
        userViewManager.VerifyNoOtherCalls();
        tvSeriesManager.VerifyNoOtherCalls();
    }

    [Fact]
    public void UserView_GetItems_NonCollectionTypeParent_ThreadsAllFourManagersToResolvedParent()
    {
        // UserView.GetItemsInternal resolves DisplayParentId/ParentId to a "real" Folder and
        // delegates via UserViewBuilder.GetUserItems -> queryParent.GetItems(query, channelManager,
        // collectionManager, userViewManager, tvSeriesManager) once no CollectionType switch case
        // matches (cf. UserViewBuilder.GetUserItems default branch, queryParent is not a UserView).
        // Using a channel-sourced resolved parent proves the exact channelManager instance passed
        // into UserView reaches that Folder unmodified (identity-checked via the strict mock).
        var displayParent = new Folder { Id = Guid.NewGuid(), ChannelId = Guid.NewGuid() };
        var userView = new UserView { Id = Guid.NewGuid(), DisplayParentId = displayParent.Id };
        var channelItem = new Movie { Id = Guid.NewGuid() };

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(x => x.GetItemById(displayParent.Id)).Returns(displayParent);
        BaseItem.LibraryManager = libraryManager.Object;
        BaseItem.Logger = NullLogger<BaseItem>.Instance;

        var (channelManager, collectionManager, userViewManager, tvSeriesManager) = StrictManagers();
        channelManager
            .Setup(x => x.GetChannelItemsInternal(It.IsAny<InternalItemsQuery>(), It.IsAny<IProgress<double>>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new QueryResult<BaseItem>(0, 1, new BaseItem[] { channelItem }));

        var result = userView.GetItems(new InternalItemsQuery { Visibility = PermissiveVisibility.Scope }, channelManager.Object, collectionManager.Object, userViewManager.Object, tvSeriesManager.Object);

        Assert.Single(result.Items);
        Assert.Equal(channelItem, result.Items[0]);
        collectionManager.VerifyNoOtherCalls();
        userViewManager.VerifyNoOtherCalls();
        tvSeriesManager.VerifyNoOtherCalls();
    }
}
#pragma warning restore CS0618
