using System;
using System.Collections.Generic;
using System.Linq;
using AutoFixture;
using AutoFixture.AutoMoq;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Playlists;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Resolvers;
using Tesserafin.Controller.Sorting;
using Tesserafin.Data.Entities;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.Library;
using Tesserafin.Model.Querying;
using Tesserafin.Naming.Common;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library.LibraryManager;

/// <summary>
/// Characterization (golden-master) tests for <see cref="Tesserafin.Server.Core.Library.LibraryManager"/>'s
/// GLOBAL-query orchestration: <c>GetItemList(InternalItemsQuery, bool)</c> and
/// <c>GetItemsResult(InternalItemsQuery)</c>, plus the private helpers they share
/// (<c>SetTopParentIdsOrAncestors</c>, <c>AddUserToQuery</c>). These tests pin down the CURRENT
/// behavior, prior to a later PR that extracts this orchestration into a dedicated service
/// (PR85). They mock <see cref="IItemRepository"/> and <see cref="IUserViewCatalog"/> (PR110:
/// replaces <c>IUserViewManager</c>) and assert on the <see cref="InternalItemsQuery"/> mutated
/// in place before it reaches the repository, plus which repository method is invoked and how
/// its result is wrapped.
/// </summary>
[Collection(LibraryManagerStaticStateFixture.Name)]
public class LibraryManagerGlobalQueryTests
{
    private readonly Tesserafin.Server.Core.Library.LibraryManager _libraryManager;
    private readonly Mock<IItemRepository> _itemRepositoryMock;
    private readonly Mock<IUserViewCatalog> _userViewCatalogMock;

    public LibraryManagerGlobalQueryTests()
    {
        var fixture = new Fixture().Customize(new AutoMoqCustomization());
        fixture.Register(() => new NamingOptions());

        var configurationManagerMock = fixture.Freeze<Mock<IServerConfigurationManager>>();
        configurationManagerMock.Setup(c => c.ApplicationPaths.ProgramDataPath).Returns("/data");
        configurationManagerMock.Setup(c => c.ApplicationPaths.InternalMetadataPath).Returns("/data/metadata");
        configurationManagerMock.Setup(c => c.Configuration).Returns(new ServerConfiguration());

        _itemRepositoryMock = fixture.Freeze<Mock<IItemRepository>>();
        _itemRepositoryMock.Setup(i => i.RetrieveItem(It.IsAny<Guid>())).Returns<BaseItem>(null);

        fixture.Freeze<Mock<IItemPersistenceService>>();

        var externalDataManagerMock = fixture.Freeze<Mock<IExternalDataManager>>();
        fixture.Register(() => new Lazy<IExternalDataManager>(() => externalDataManagerMock.Object));

        // GetItemsResult/GetItemList resolve unscoped user queries via IUserViewCatalog (PR110:
        // replaces IUserViewManager - LibraryManager.cs AddUserToQuery, ~L2001). Freeze the mock
        // so tests can both control the returned views and assert on the UserViewQuery
        // LibraryManager builds for it.
        _userViewCatalogMock = fixture.Freeze<Mock<IUserViewCatalog>>();

        // Same wiring as LibraryManagerItemLookupTests: build a *real* ItemLookupService from the
        // frozen repository/configuration mocks so LibraryManager's GetItemById(ParentId) parent
        // resolution (used by SetTopParentIdsOrAncestors) reads through the mocked repository
        // instead of a second, independently-behaving auto-mock.
        var itemLookupService = new Tesserafin.Server.Core.Library.ItemLookupService(_itemRepositoryMock.Object, configurationManagerMock.Object);
        fixture.Register(() => itemLookupService);
        fixture.Register<IItemLookupService>(() => itemLookupService);
        fixture.Register<Tesserafin.Server.Core.Library.IItemCacheStore>(() => itemLookupService);

        var itemAccessService = new Tesserafin.Server.Core.Library.ItemAccessService(
            itemLookupService,
            new Tesserafin.Server.Core.Library.TagVisibilityService(itemLookupService, fixture.Freeze<ICollectionFolderResolver>(), fixture.Freeze<IUserRootFolderProvider>(), _itemRepositoryMock.Object, fixture.Freeze<ILocalizationManager>(), fixture.Freeze<IServerConfigurationManager>(), new Tesserafin.Controller.LiveTv.ActiveRecordings()));
        fixture.Register<IItemAccessService>(() => itemAccessService);

        Tesserafin.Server.Implementations.Tests.Library.LibraryWriteFixture.Register(fixture);

        _libraryManager = fixture.Build<Tesserafin.Server.Core.Library.LibraryManager>().Do(s => s.AddParts(
                fixture.Create<IEnumerable<IResolverIgnoreRule>>(),
                fixture.Create<IEnumerable<IItemResolver>>(),
                fixture.Create<IEnumerable<IBaseItemComparer>>()))
            .Create();
    }

    // ---------------------------------------------------------------
    // 1. Recursive query with a non-empty ParentId resolves the parent via GetItemById and sets
    // ancestor/top-parent scoping from it before the repository call (LibraryManager.cs L1649-1656,
    // L1938-1983 SetTopParentIdsOrAncestors "else" branch: a plain Folder parent -> AncestorIds).
    // ---------------------------------------------------------------

    [Fact]
    public void GetItemList_RecursiveWithParentId_SetsAncestorIdsFromResolvedParent()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "Series" };
        _itemRepositoryMock.Setup(r => r.RetrieveItem(series.Id)).Returns(series);
        _itemRepositoryMock.Setup(r => r.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(Array.Empty<BaseItem>());

        var query = new InternalItemsQuery { Recursive = true, ParentId = series.Id };

        _libraryManager.GetItemList(query);

        Assert.Equal(new[] { series.Id }, query.AncestorIds);
        Assert.Empty(query.TopParentIds);
        _itemRepositoryMock.Verify(r => r.GetItemList(query), Times.Once);
    }

    // ---------------------------------------------------------------
    // 2. GetItemsResult with a non-null User and an otherwise-unscoped query resolves the user's
    // views into query.TopParentIds via IUserViewCatalog before hitting the repository
    // (AddUserToQuery, LibraryManager.cs L1985-2016).
    // ---------------------------------------------------------------

    [Fact]
    public void GetItemsResult_UserQueryWithoutScope_ResolvesUserViewsIntoTopParentIds()
    {
        var user = new User("test-user", "provider", "provider");
        var physicalFolderId = Guid.NewGuid();
        var userView = new CollectionFolder { Id = Guid.NewGuid(), Name = "Movies", PhysicalFolderIds = [physicalFolderId] };

        _userViewCatalogMock.Setup(m => m.GetUserViews(It.IsAny<UserViewQuery>())).Returns([userView]);
        _itemRepositoryMock.Setup(r => r.GetItems(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem>(0, 0, Array.Empty<BaseItem>()));

        var query = new InternalItemsQuery(user);

        _libraryManager.GetItemsResult(query);

        Assert.Equal(new[] { physicalFolderId }, query.TopParentIds);
        _userViewCatalogMock.Verify(
            m => m.GetUserViews(It.Is<UserViewQuery>(q => q.User == user && q.IncludeHidden)),
            Times.Once);
    }

    // ---------------------------------------------------------------
    // 3. Empty-scope guard: when top-parent scoping resolves to an empty set, a fresh sentinel GUID
    // is injected so the repository is never asked to scan all libraries on an empty filter
    // (SetTopParentIdsOrAncestors, LibraryManager.cs L1943-1949).
    // ---------------------------------------------------------------

    [Fact]
    public void GetItemList_RecursiveParentResolvesToEmptyScope_InjectsSentinelTopParentId()
    {
        // A CollectionFolder (ICollectionFolder) with no PhysicalFolderIds hits the "optimize by
        // querying against top level views" branch and resolves to zero top-parent ids.
        var emptyView = new CollectionFolder { Id = Guid.NewGuid(), Name = "Empty" };
        _itemRepositoryMock.Setup(r => r.RetrieveItem(emptyView.Id)).Returns(emptyView);
        _itemRepositoryMock.Setup(r => r.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(Array.Empty<BaseItem>());

        var query = new InternalItemsQuery { Recursive = true, ParentId = emptyView.Id };

        _libraryManager.GetItemList(query);

        Assert.Single(query.TopParentIds);
        Assert.NotEqual(Guid.Empty, query.TopParentIds[0]);
        Assert.Empty(query.AncestorIds);
    }

    // ---------------------------------------------------------------
    // 4. A single Playlist (or BoxSet) parent whose LinkedChildren is non-empty routes via
    // query.ItemIds (the linked child ids), NOT AncestorIds (LinkedChildren branch,
    // LibraryManager.cs L1951-1969).
    // ---------------------------------------------------------------

    [Fact]
    public void GetItemList_RecursiveParentIsPlaylistWithLinkedChildren_RoutesViaItemIds()
    {
        var child1Id = Guid.NewGuid();
        var child2Id = Guid.NewGuid();
        var playlist = new Playlist
        {
            Id = Guid.NewGuid(),
            Name = "Playlist",
            LinkedChildren =
            [
                new LinkedChild { ItemId = child1Id },
                new LinkedChild { ItemId = child2Id }
            ]
        };
        _itemRepositoryMock.Setup(r => r.RetrieveItem(playlist.Id)).Returns(playlist);
        _itemRepositoryMock.Setup(r => r.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(Array.Empty<BaseItem>());

        var query = new InternalItemsQuery { Recursive = true, ParentId = playlist.Id };

        _libraryManager.GetItemList(query);

        Assert.Equal(new[] { child1Id, child2Id }, query.ItemIds);
        Assert.Empty(query.AncestorIds);
    }

    // ---------------------------------------------------------------
    // 5. EnableTotalRecordCount routing: true -> GetItemsResult returns
    // _itemRepository.GetItems(query) directly; false -> returns a QueryResult wrapping
    // _itemRepository.GetItemList(query) (LibraryManager.cs L1927-1936).
    // ---------------------------------------------------------------

    [Fact]
    public void GetItemsResult_EnableTotalRecordCountTrue_ReturnsRepositoryGetItemsResultDirectly()
    {
        var expected = new QueryResult<BaseItem>(0, 42, Array.Empty<BaseItem>());
        _itemRepositoryMock.Setup(r => r.GetItems(It.IsAny<InternalItemsQuery>())).Returns(expected);

        var query = new InternalItemsQuery { EnableTotalRecordCount = true };

        var result = _libraryManager.GetItemsResult(query);

        Assert.Same(expected, result);
        _itemRepositoryMock.Verify(r => r.GetItems(query), Times.Once);
        _itemRepositoryMock.Verify(r => r.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public void GetItemsResult_EnableTotalRecordCountFalse_WrapsRepositoryGetItemList()
    {
        var items = new BaseItem[]
        {
            new Movie { Id = Guid.NewGuid(), Name = "Movie 1" },
            new Movie { Id = Guid.NewGuid(), Name = "Movie 2" }
        };
        _itemRepositoryMock.Setup(r => r.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(items);

        var query = new InternalItemsQuery { EnableTotalRecordCount = false, StartIndex = 5 };

        var result = _libraryManager.GetItemsResult(query);

        Assert.Same(items, result.Items);
        Assert.Equal(5, result.StartIndex);
        // The (int?, int?, IReadOnlyList<T>) QueryResult ctor falls back to items.Count when the
        // totalRecordCount argument is null, so this is never actually a null/absent count on the
        // wire - it is simply not independently sourced from the repository's own count query.
        Assert.Equal(items.Length, result.TotalRecordCount);
        _itemRepositoryMock.Verify(r => r.GetItemList(query), Times.Once);
        _itemRepositoryMock.Verify(r => r.GetItems(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    // ---------------------------------------------------------------
    // 6. GetItemList(query, allowExternalContent: false) propagates IncludeExternalContent == false
    // into the UserViewQuery used to resolve user views (AddUserToQuery's allowExternalContent
    // parameter, LibraryManager.cs L1985 / L2005).
    // ---------------------------------------------------------------

    [Fact]
    public void GetItemList_AllowExternalContentFalse_PropagatesIncludeExternalContentFalseToUserViewQuery()
    {
        var user = new User("test-user", "provider", "provider");
        UserViewQuery? capturedQuery = null;
        _userViewCatalogMock.Setup(m => m.GetUserViews(It.IsAny<UserViewQuery>()))
            .Callback<UserViewQuery>(q => capturedQuery = q)
            .Returns([]);
        _itemRepositoryMock.Setup(r => r.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(Array.Empty<BaseItem>());

        var query = new InternalItemsQuery(user);

        _libraryManager.GetItemList(query, allowExternalContent: false);

        Assert.NotNull(capturedQuery);
        Assert.False(capturedQuery!.IncludeExternalContent);
    }
}
