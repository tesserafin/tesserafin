using System;
using System.Linq;
using Moq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Playlists;
using Tesserafin.Controller.Sorting;
using Tesserafin.Data.Entities;
using Tesserafin.Model.Library;
using Tesserafin.Model.Querying;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

/// <summary>
/// Behavioral parity tests for <see cref="ItemQueryScopeService"/>, mirroring the characterization
/// tests in <c>LibraryManagerGlobalQueryTests</c> (PR85) that pinned down the equivalent
/// <c>LibraryManager</c> private helpers this service copies from
/// (<c>SetTopParentIdsOrAncestors</c>/<c>AddUserToQuery</c>/<c>GetTopParentIdsForQuery</c>). These
/// exercise the extracted service directly, with <see cref="IItemLookupService"/>,
/// <see cref="IUserViewCatalog"/>, <see cref="IItemSortService"/> and
/// <see cref="IUserRootFolderProvider"/> mocked. <b>PR110</b>: this service now depends on
/// <see cref="IUserViewCatalog"/> instead of <c>IUserViewManager</c> - it holds no direct or
/// transitive reference to <c>ILibraryManager</c>, <c>IUserViewManager</c>, <c>IChannelManager</c> or
/// <c>ILiveTvManager</c>, so it is a fully cycle-free leaf (see
/// <c>docs/pr85b-item-query-scope-service.md</c>).
/// </summary>
public class ItemQueryScopeServiceTests
{
    private readonly Mock<IItemLookupService> _itemLookupServiceMock = new();
    private readonly Mock<IItemAccessService> _itemAccessServiceMock = new();
    private readonly Mock<IUserViewCatalog> _userViewCatalogMock = new();
    private readonly Mock<IItemSortService> _itemSortServiceMock = new();
    private readonly Mock<IUserRootFolderProvider> _rootFolderProviderMock = new();
    private readonly ItemQueryScopeService _scopeService;

    public ItemQueryScopeServiceTests()
    {
        _scopeService = new ItemQueryScopeService(
            _itemLookupServiceMock.Object,
            _itemAccessServiceMock.Object,
            _userViewCatalogMock.Object,
            _itemSortServiceMock.Object,
            _rootFolderProviderMock.Object,
            Mock.Of<IStandaloneVisibilityService>());
    }

    [Fact]
    public void NamedAncestors_VisibleLibrariesAndLinkedViews_ResolvePhysicalFolders()
    {
        var physical = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var library = new CollectionFolder { Id = Guid.NewGuid(), PhysicalFolderIds = physical };
        var view = new UserView { Id = Guid.NewGuid(), DisplayParentId = library.Id };
        var user = new User("test-user", "provider", "provider");
        _itemLookupServiceMock.Setup(l => l.GetItemById(library.Id)).Returns(library);
        _itemLookupServiceMock.Setup(l => l.GetItemById(view.Id)).Returns(view);
        _itemAccessServiceMock.Setup(a => a.GetVisibleItemById<BaseItem>(view.Id, user)).Returns(view);
        var query = new InternalItemsQuery(user) { AncestorIds = [view.Id] };

        _scopeService.SetTopParentOrAncestorIds(query);

        Assert.Equal(physical, query.TopParentIds);
        Assert.Empty(query.AncestorIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NamedAncestors_HiddenLibraryOrAggregateRoot_CannotBroadenAUserQuery(bool aggregate)
    {
        BaseItem parent = aggregate ? new AggregateFolder { Id = Guid.NewGuid() } : new CollectionFolder { Id = Guid.NewGuid(), PhysicalFolderIds = [Guid.NewGuid()] };
        _itemLookupServiceMock.Setup(l => l.GetItemById(parent.Id)).Returns(parent);
        var query = new InternalItemsQuery(new User("test-user", "provider", "provider")) { AncestorIds = [parent.Id] };

        _scopeService.SetTopParentOrAncestorIds(query);

        Assert.Empty(query.AncestorIds);
        var sentinel = Assert.Single(query.TopParentIds);
        Assert.NotEqual(Guid.Empty, sentinel);
        Assert.NotEqual(parent.Id, sentinel);
    }

    [Fact]
    public void NamedAncestors_MixedLibraryAndOrdinaryAncestor_PreservesTheOriginalFilter()
    {
        var library = new CollectionFolder { Id = Guid.NewGuid(), PhysicalFolderIds = [Guid.NewGuid()] };
        var folder = new Folder { Id = Guid.NewGuid() };
        _itemLookupServiceMock.Setup(l => l.GetItemById(library.Id)).Returns(library);
        _itemLookupServiceMock.Setup(l => l.GetItemById(folder.Id)).Returns(folder);
        var original = new[] { library.Id, folder.Id };
        var query = new InternalItemsQuery { AncestorIds = original };

        _scopeService.SetTopParentOrAncestorIds(query);

        Assert.Same(original, query.AncestorIds);
        Assert.Empty(query.TopParentIds);
    }

    [Fact]
    public void NamedAncestors_UnknownId_IsRejectedRatherThanIgnored()
    {
        var query = new InternalItemsQuery { AncestorIds = [Guid.NewGuid()] };

        Assert.Throws<ArgumentException>(() => _scopeService.SetTopParentOrAncestorIds(query));

        Assert.Empty(query.TopParentIds);
        Assert.Single(query.AncestorIds);
    }

    [Fact]
    public void NamedAncestors_EmptyLibrary_UsesANonemptySentinel()
    {
        var library = new CollectionFolder { Id = Guid.NewGuid() };
        _itemLookupServiceMock.Setup(l => l.GetItemById(library.Id)).Returns(library);
        var query = new InternalItemsQuery { AncestorIds = [library.Id] };

        _scopeService.SetTopParentOrAncestorIds(query);

        Assert.Empty(query.AncestorIds);
        Assert.NotEqual(Guid.Empty, Assert.Single(query.TopParentIds));
    }

    [Fact]
    public void Ownership_ManagerUsesTheSharedScope_AndHasNoPrivateParentScopeCopies()
    {
        var manager = typeof(Tesserafin.Server.Core.Library.LibraryManager);
        var dependencies = manager.GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.Contains(typeof(IItemQueryScopeService), dependencies);
        Assert.DoesNotContain(typeof(IUserViewCatalog), dependencies);
        var removed = new[] { "SetTopParentIdsOrAncestors", "SetTopParentOrAncestorIds", "SetTopParentIdsToUserViews", "GetTopParentIdsForQuery" };
        Assert.DoesNotContain(manager.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic), m => removed.Contains(m.Name));
        Assert.DoesNotContain(typeof(ItemQueryScopeService).GetConstructors().Single().GetParameters(), p => p.ParameterType == typeof(ILibraryManager));
    }

    // ---------------------------------------------------------------
    // 1. A plain Folder parent (not a collection/user view, not a Playlist/BoxSet with linked
    // children) routes through AncestorIds (LibraryManager.cs "else" branch, L1972-1979).
    // ---------------------------------------------------------------

    [Fact]
    public void SetTopParentIdsOrAncestors_PlainFolderParent_SetsAncestorIds()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "Series" };
        var query = new InternalItemsQuery { Recursive = true };

        _scopeService.SetTopParentIdsOrAncestors(query, [series]);

        Assert.Equal(new[] { series.Id }, query.AncestorIds);
        Assert.Empty(query.TopParentIds);
    }

    // ---------------------------------------------------------------
    // 2. AddUserToQuery resolves the user's views into TopParentIds via IUserViewCatalog when the
    // query does not already carry any scoping (LibraryManager.cs L1985-2016).
    // ---------------------------------------------------------------

    [Fact]
    public void AddUserToQuery_UnscopedQuery_ResolvesUserViewsIntoTopParentIds()
    {
        var user = new User("test-user", "provider", "provider");
        var physicalFolderId = Guid.NewGuid();
        var userView = new CollectionFolder { Id = Guid.NewGuid(), Name = "Movies", PhysicalFolderIds = [physicalFolderId] };

        _userViewCatalogMock.Setup(m => m.GetUserViews(It.IsAny<UserViewQuery>())).Returns([userView]);

        var query = new InternalItemsQuery(user);

        _scopeService.AddUserToQuery(query, user);

        Assert.Equal(new[] { physicalFolderId }, query.TopParentIds);
        _userViewCatalogMock.Verify(
            m => m.GetUserViews(It.Is<UserViewQuery>(q => q.User == user && q.IncludeHidden)),
            Times.Once);
    }

    // ---------------------------------------------------------------
    // 3. Empty-scope guard: when top-parent scoping resolves to an empty set, a fresh sentinel GUID
    // is injected so the query never falls back to scanning all libraries
    // (SetTopParentIdsOrAncestors, LibraryManager.cs L1943-1949).
    // ---------------------------------------------------------------

    [Fact]
    public void SetTopParentIdsOrAncestors_ParentResolvesToEmptyScope_InjectsSentinelTopParentId()
    {
        // A CollectionFolder (ICollectionFolder) with no PhysicalFolderIds hits the "optimize by
        // querying against top level views" branch and resolves to zero top-parent ids.
        var emptyView = new CollectionFolder { Id = Guid.NewGuid(), Name = "Empty" };
        var query = new InternalItemsQuery { Recursive = true };

        _scopeService.SetTopParentIdsOrAncestors(query, [emptyView]);

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
    public void SetTopParentIdsOrAncestors_PlaylistWithLinkedChildren_RoutesViaItemIds()
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
        var query = new InternalItemsQuery { Recursive = true };

        _scopeService.SetTopParentIdsOrAncestors(query, [playlist]);

        Assert.Equal(new[] { child1Id, child2Id }, query.ItemIds);
        Assert.Empty(query.AncestorIds);
    }

    [Fact]
    public void SetTopParentIdsOrAncestors_BoxSetWithLinkedChildren_RoutesViaItemIds()
    {
        var childId = Guid.NewGuid();
        var boxSet = new BoxSet
        {
            Id = Guid.NewGuid(),
            Name = "BoxSet",
            LinkedChildren = [new LinkedChild { ItemId = childId }]
        };
        var query = new InternalItemsQuery { Recursive = true };

        _scopeService.SetTopParentIdsOrAncestors(query, [boxSet]);

        Assert.Equal(new[] { childId }, query.ItemIds);
        Assert.Empty(query.AncestorIds);
    }

    // ---------------------------------------------------------------
    // 5. AddUserToQuery(query, user, allowExternalContent: false) propagates
    // IncludeExternalContent == false into the UserViewQuery used to resolve user views
    // (LibraryManager.cs L1985 / L2005).
    // ---------------------------------------------------------------

    [Fact]
    public void AddUserToQuery_AllowExternalContentFalse_PropagatesIncludeExternalContentFalseToUserViewQuery()
    {
        var user = new User("test-user", "provider", "provider");
        UserViewQuery? capturedQuery = null;
        _userViewCatalogMock.Setup(m => m.GetUserViews(It.IsAny<UserViewQuery>()))
            .Callback<UserViewQuery>(q => capturedQuery = q)
            .Returns([]);

        var query = new InternalItemsQuery(user);

        _scopeService.AddUserToQuery(query, user, allowExternalContent: false);

        Assert.NotNull(capturedQuery);
        Assert.False(capturedQuery!.IncludeExternalContent);
    }

    // ---------------------------------------------------------------
    // 6. AddUserToQuery is a no-op when the query is already scoped (e.g. non-empty AncestorIds) -
    // IUserViewCatalog is never consulted (LibraryManager.cs L1992-1999 guard condition).
    // ---------------------------------------------------------------

    [Fact]
    public void AddUserToQuery_AlreadyScopedQuery_DoesNotResolveUserViews()
    {
        var user = new User("test-user", "provider", "provider");
        var query = new InternalItemsQuery(user) { AncestorIds = [Guid.NewGuid()] };

        _scopeService.AddUserToQuery(query, user);

        _userViewCatalogMock.Verify(m => m.GetUserViews(It.IsAny<UserViewQuery>()), Times.Never);
        Assert.Empty(query.TopParentIds);
    }

    // ---------------------------------------------------------------
    // 7. AddUserToQuery sets query.User when the query does not already have one
    // (LibraryManager.cs L1987-1990).
    // ---------------------------------------------------------------

    [Fact]
    public void AddUserToQuery_QueryWithoutUser_SetsUserOnQuery()
    {
        var user = new User("test-user", "provider", "provider");
        _userViewCatalogMock.Setup(m => m.GetUserViews(It.IsAny<UserViewQuery>())).Returns([]);

        var query = new InternalItemsQuery();

        _scopeService.AddUserToQuery(query, user);

        Assert.Equal(user, query.User);
    }

    // ---------------------------------------------------------------
    // 8. AddUserToQuery resolves a plain (non-view, non-collection) user view into its top parent
    // WITHOUT touching the static BaseItem.LibraryManager. The item's parent chain is resolved purely
    // through IItemLookupService; a strict BaseItem.LibraryManager mock proves no static hop occurs.
    // This pins PR90's static-free query-scoping fallback.
    // ---------------------------------------------------------------

    [Fact]
    public void AddUserToQuery_PlainViewResolvesTopParent_StaysOffStaticLibraryManager()
    {
        // Strict: any call to the static BaseItem.LibraryManager throws, failing the test.
        var previous = BaseItem.LibraryManager;
        BaseItem.LibraryManager = new Mock<ILibraryManager>(MockBehavior.Strict).Object;
        try
        {
            var user = new User("test-user", "provider", "provider");

            // Aggregate root: makes its direct child a top parent (IsTopParentVia -> GetParent(lookup) is AggregateFolder).
            var aggregate = new AggregateFolder { Id = Guid.NewGuid(), Name = "Root" };
            var topFolder = new Folder { Id = Guid.NewGuid(), Name = "Top", ParentId = aggregate.Id };
            // Leaf returned by GetUserViews: plain Folder, not top, parent is topFolder.
            var leaf = new Folder { Id = Guid.NewGuid(), Name = "Leaf", ParentId = topFolder.Id };

            _itemLookupServiceMock.Setup(m => m.GetItemById(topFolder.Id)).Returns(topFolder);
            _itemLookupServiceMock.Setup(m => m.GetItemById(aggregate.Id)).Returns(aggregate);
            _userViewCatalogMock.Setup(m => m.GetUserViews(It.IsAny<UserViewQuery>())).Returns(new[] { leaf });

            var query = new InternalItemsQuery(user);

            _scopeService.AddUserToQuery(query, user);

            // Resolved purely through the lookup mock: leaf -> topFolder (top parent).
            Assert.Equal(new[] { topFolder.Id }, query.TopParentIds);
        }
        finally
        {
            BaseItem.LibraryManager = previous;
        }
    }

    // ---------------------------------------------------------------
    // SECURITY-MEDIA-R3. A parent, or a playlist's linked children, stand in for scoping a query
    // to its user's libraries: with a user on the query each is asked for through
    // IItemAccessService, and an ancestor is scoped to the user's views as well.
    // ---------------------------------------------------------------

    [Fact]
    public void SetTopParentIdsOrAncestors_ParentTheUserMayNotSee_ScopesToNothing()
    {
        var user = new User("test-user", "provider", "provider");
        var folder = new Folder { Id = Guid.NewGuid(), Name = "Withheld" };
        var query = new InternalItemsQuery(user) { Recursive = true };

        _scopeService.SetTopParentIdsOrAncestors(query, [folder]);

        Assert.Single(query.TopParentIds);
        Assert.NotEqual(folder.Id, query.TopParentIds[0]);
        Assert.Empty(query.AncestorIds);
        Assert.Empty(query.ItemIds);
    }

    [Fact]
    public void SetTopParentIdsOrAncestors_AncestorTheUserMaySee_IsScopedToTheUsersViewsToo()
    {
        var user = new User("test-user", "provider", "provider");
        var physicalFolderId = Guid.NewGuid();
        var view = new CollectionFolder { Id = Guid.NewGuid(), Name = "Movies", PhysicalFolderIds = [physicalFolderId] };
        var root = new AggregateFolder { Id = Guid.NewGuid(), Name = "Root" };
        _itemAccessServiceMock.Setup(m => m.GetVisibleItemById<BaseItem>(root.Id, user)).Returns(root);
        _userViewCatalogMock.Setup(m => m.GetUserViews(It.IsAny<UserViewQuery>())).Returns([view]);
        var query = new InternalItemsQuery(user) { Recursive = true };

        _scopeService.SetTopParentIdsOrAncestors(query, [root]);

        Assert.Equal(new[] { root.Id }, query.AncestorIds);
        Assert.Equal(new[] { physicalFolderId }, query.TopParentIds);
    }

    [Fact]
    public void SetTopParentIdsOrAncestors_PlaylistWithAUser_KeepsTheLinkedChildrenThatUserMaySee()
    {
        var user = new User("test-user", "provider", "provider");
        var seen = new Folder { Id = Guid.NewGuid(), Name = "Seen" };
        var withheldId = Guid.NewGuid();
        var playlist = new Playlist
        {
            Id = Guid.NewGuid(),
            Name = "Playlist",
            LinkedChildren = [new LinkedChild { ItemId = withheldId }, new LinkedChild { ItemId = seen.Id }]
        };
        _itemAccessServiceMock.Setup(m => m.GetVisibleItemById<BaseItem>(playlist.Id, user)).Returns(playlist);
        _itemAccessServiceMock.Setup(m => m.GetVisibleItemById<BaseItem>(seen.Id, user)).Returns(seen);
        var query = new InternalItemsQuery(user) { Recursive = true };

        _scopeService.SetTopParentIdsOrAncestors(query, [playlist]);

        Assert.Equal(new[] { seen.Id }, query.ItemIds);
    }

    [Fact]
    public void SetTopParentIdsOrAncestors_PlaylistWhoseEveryChildIsWithheld_ScopesToNothing()
    {
        var user = new User("test-user", "provider", "provider");
        var withheldId = Guid.NewGuid();
        var playlist = new Playlist { Id = Guid.NewGuid(), Name = "Playlist", LinkedChildren = [new LinkedChild { ItemId = withheldId }] };
        _itemAccessServiceMock.Setup(m => m.GetVisibleItemById<BaseItem>(playlist.Id, user)).Returns(playlist);
        var query = new InternalItemsQuery(user) { Recursive = true };

        _scopeService.SetTopParentIdsOrAncestors(query, [playlist]);

        Assert.Single(query.ItemIds);
        Assert.NotEqual(withheldId, query.ItemIds[0]);
    }
}
