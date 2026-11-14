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
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Sorting;
using Tesserafin.Controller.TV;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Querying;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

public class ItemQueryServiceTests
{
    private static ItemQueryService CreateService(
        out Mock<IChannelManager> channelManager,
        out Mock<ICollectionManager> collectionManager,
        out Mock<IUserViewManager> userViewManager,
        out Mock<ITVSeriesManager> tvSeriesManager,
        out Mock<IItemLookupService> itemLookupService,
        out Mock<IItemRepository> itemRepository,
        out Mock<IItemQueryScopeService> scopeService,
        ILibraryManager? libraryManager = null,
        IServerConfigurationManager? configurationManager = null,
        IItemSortService? itemSortService = null)
    {
        channelManager = new Mock<IChannelManager>();
        collectionManager = new Mock<ICollectionManager>();
        userViewManager = new Mock<IUserViewManager>();
        tvSeriesManager = new Mock<ITVSeriesManager>();
        itemLookupService = new Mock<IItemLookupService>();
        itemRepository = new Mock<IItemRepository>();
        scopeService = new Mock<IItemQueryScopeService>();

        libraryManager ??= new Mock<ILibraryManager>().Object;
        configurationManager ??= Mock.Of<IServerConfigurationManager>(x => x.Configuration == new ServerConfiguration());

        BaseItem.LibraryManager = libraryManager;
        BaseItem.ConfigurationManager = configurationManager;
        BaseItem.UserDataManager = new Mock<IUserDataManager>().Object;

        return new ItemQueryService(channelManager.Object, collectionManager.Object, userViewManager.Object, tvSeriesManager.Object, configurationManager, itemSortService ?? new PassthroughItemSortService(), itemLookupService.Object, itemRepository.Object, scopeService.Object);
    }

    [Fact]
    public void GetItems_DelegatesToFolderWithTheServiceOwnedManagers()
    {
        var service = CreateService(out var channelManager, out var collectionManager, out var userViewManager, out var tvSeriesManager, out _, out _, out _);

        var folder = new Folder { Id = Guid.NewGuid() };
        var child = new Movie { Id = Guid.NewGuid() };
        folder.Children = new BaseItem[] { child };

        var result = service.GetItems(folder, new InternalItemsQuery());

        Assert.Single(result.Items);
        Assert.Equal(child, result.Items[0]);

        // Not a real assertion on channelManager/collectionManager/userViewManager/tvSeriesManager
        // being called (this plain library folder doesn't touch them, same as FolderTests) - the
        // point of this test is that the service compiles/wires the 4 dependencies through to
        // Folder.GetItems without the caller having to hold them itself.
        channelManager.VerifyNoOtherCalls();
        collectionManager.VerifyNoOtherCalls();
        userViewManager.VerifyNoOtherCalls();
        tvSeriesManager.VerifyNoOtherCalls();
    }

    [Fact]
    public void GetItemList_DelegatesToFolderWithTheServiceOwnedManagers()
    {
        var service = CreateService(out var channelManager, out var collectionManager, out var userViewManager, out var tvSeriesManager, out _, out _, out _);

        var folder = new Folder { Id = Guid.NewGuid() };
        var child = new Movie { Id = Guid.NewGuid() };
        folder.Children = new BaseItem[] { child };

        var result = service.GetItemList(folder, new InternalItemsQuery());

        Assert.Single(result);
        Assert.Equal(child, result[0]);
        channelManager.VerifyNoOtherCalls();
        collectionManager.VerifyNoOtherCalls();
        userViewManager.VerifyNoOtherCalls();
        tvSeriesManager.VerifyNoOtherCalls();
    }

    // Parity tests (major rewrite plan, PR23/N): ItemQueryService.PostFilterAndSort is a relocation
    // of Folder.PostFilterAndSort - not wired into GetItems/GetItemList yet (that's PR24, once a
    // raw-children primitive exists on Folder). These compare the new standalone method against the
    // still-untouched Folder.GetItems (which still runs its own copy of the same logic) given the
    // same raw items, to prove behavior was reproduced exactly, not just relocated by inspection.

    [Fact]
    public void PostFilterAndSort_UserNull_MatchesFolderGetItems()
    {
        var service = CreateService(out var channelManager, out var collectionManager, out var userViewManager, out var tvSeriesManager, out _, out _, out _);

        var folder = new Folder { Id = Guid.NewGuid() };
        var child1 = new Movie { Id = Guid.NewGuid(), Name = "Alpha" };
        var child2 = new Movie { Id = Guid.NewGuid(), Name = "Beta" };
        folder.Children = new BaseItem[] { child1, child2 };

        // Parity test deliberately compares against the obsolete 5-parameter overload — see
        // docs/major-rewrite-plan-v13.md § PR28/N.
#pragma warning disable CS0618
        var expected = folder.GetItems(new InternalItemsQuery(), channelManager.Object, collectionManager.Object, userViewManager.Object, tvSeriesManager.Object);
#pragma warning restore CS0618
        var actual = service.PostFilterAndSort(folder, folder.Children, new InternalItemsQuery());

        Assert.Equal(expected.Items, actual.Items);
        Assert.Equal(expected.TotalRecordCount, actual.TotalRecordCount);
    }

    [Fact]
    public void PostFilterAndSort_UserNonNull_CollapseFalse_MatchesFolderGetItems()
    {
        var service = CreateService(out var channelManager, out var collectionManager, out var userViewManager, out var tvSeriesManager, out _, out _, out _);

        var folder = new Folder { Id = Guid.NewGuid() };
        var child = new Movie { Id = Guid.NewGuid(), Name = "Alpha" };
        folder.Children = new BaseItem[] { child };
        var user = new User("test-user", "provider", "provider");

        InternalItemsQuery MakeQuery() => new() { User = user, CollapseBoxSetItems = false };

        // Parity test deliberately compares against the obsolete 5-parameter overload — see
        // docs/major-rewrite-plan-v13.md § PR28/N.
#pragma warning disable CS0618
        var expected = folder.GetItems(MakeQuery(), channelManager.Object, collectionManager.Object, userViewManager.Object, tvSeriesManager.Object);
#pragma warning restore CS0618
        var actual = service.PostFilterAndSort(folder, folder.Children, MakeQuery());

        Assert.Equal(expected.Items, actual.Items);
        collectionManager.Verify(x => x.CollapseItemsWithinBoxSets(It.IsAny<IEnumerable<BaseItem>>(), It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public void PostFilterAndSort_UserNonNull_CollapseTrue_MatchesFolderGetItems()
    {
        var configurationManager = Mock.Of<IServerConfigurationManager>(x => x.Configuration == new ServerConfiguration
        {
            EnableGroupingMoviesIntoCollections = true,
            EnableGroupingShowsIntoCollections = true,
        });
        var service = CreateService(out var channelManager, out var collectionManager, out var userViewManager, out var tvSeriesManager, out _, out _, out _, configurationManager: configurationManager);

        var folder = new Folder { Id = Guid.NewGuid() };
        var child = new Movie { Id = Guid.NewGuid(), Name = "Alpha" };
        folder.Children = new BaseItem[] { child };
        var user = new User("test-user", "provider", "provider");

        collectionManager
            .Setup(x => x.CollapseItemsWithinBoxSets(It.IsAny<IEnumerable<BaseItem>>(), user))
            .Returns((IEnumerable<BaseItem> items, User _) => items);

        InternalItemsQuery MakeQuery() => new() { User = user, CollapseBoxSetItems = true };

        // Parity test deliberately compares against the obsolete 5-parameter overload — see
        // docs/major-rewrite-plan-v13.md § PR28/N.
#pragma warning disable CS0618
        var expected = folder.GetItems(MakeQuery(), channelManager.Object, collectionManager.Object, userViewManager.Object, tvSeriesManager.Object);
#pragma warning restore CS0618
        var actual = service.PostFilterAndSort(folder, folder.Children, MakeQuery());

        Assert.Equal(expected.Items, actual.Items);
        // Both the untouched Folder path and the new relocated path call the same collectionManager
        // instance once each with the exact same arguments - not just "similar output".
        collectionManager.Verify(x => x.CollapseItemsWithinBoxSets(It.IsAny<IEnumerable<BaseItem>>(), user), Times.Exactly(2));
    }

    [Theory]
    [InlineData("B", "", "", "Beta")]
    [InlineData("", "B", "", "Beta")]
    [InlineData("", "", "B", "Alpha")]
    public void PostFilterAndSort_NameFiltersAfterCollapse_MatchesFolderGetItems(string nameStartsWith, string nameStartsWithOrGreater, string nameLessThan, string expectedName)
    {
        var configurationManager = Mock.Of<IServerConfigurationManager>(x => x.Configuration == new ServerConfiguration
        {
            EnableGroupingMoviesIntoCollections = true,
            EnableGroupingShowsIntoCollections = true,
        });
        var service = CreateService(out var channelManager, out var collectionManager, out var userViewManager, out var tvSeriesManager, out _, out _, out _, configurationManager: configurationManager);

        var folder = new Folder { Id = Guid.NewGuid() };
        var alpha = new Movie { Id = Guid.NewGuid(), Name = "Alpha" };
        var beta = new Movie { Id = Guid.NewGuid(), Name = "Beta" };
        folder.Children = new BaseItem[] { alpha, beta };
        var user = new User("test-user", "provider", "provider");

        collectionManager
            .Setup(x => x.CollapseItemsWithinBoxSets(It.IsAny<IEnumerable<BaseItem>>(), user))
            .Returns((IEnumerable<BaseItem> items, User _) => items);

        InternalItemsQuery MakeQuery() => new()
        {
            User = user,
            CollapseBoxSetItems = true,
            NameStartsWith = nameStartsWith,
            NameStartsWithOrGreater = nameStartsWithOrGreater,
            NameLessThan = nameLessThan,
        };

        // Parity test deliberately compares against the obsolete 5-parameter overload — see
        // docs/major-rewrite-plan-v13.md § PR28/N.
#pragma warning disable CS0618
        var expected = folder.GetItems(MakeQuery(), channelManager.Object, collectionManager.Object, userViewManager.Object, tvSeriesManager.Object);
#pragma warning restore CS0618
        var actual = service.PostFilterAndSort(folder, folder.Children, MakeQuery());

        Assert.Equal(expected.Items.Select(i => i.Name), actual.Items.Select(i => i.Name));
        Assert.Single(actual.Items);
        Assert.Equal(expectedName, actual.Items[0].Name);
    }

    // Wiring tests (major rewrite plan, PR25/N): GetItems/GetItemList now take a fast path
    // (GetRawQueryItems + PostFilterAndSort) for folders where Folder.SupportsRawQueryItems is
    // true. For the safe case the fast path is behaviorally equivalent to the old folder.GetItems
    // call by construction (Folder.GetItemsInternal routes through the same GetRawQueryItems,
    // PR24) - not worth re-proving here. What IS worth proving: the 4 cases where taking the fast
    // path would be a silent correctness bug (SupportsRawQueryItems false, Recursive, ItemIds,
    // channel source) actually fall back to the old, still-correct path instead.

    [Fact]
    public void GetItems_RecursiveQuery_FallsBackInsteadOfRawQueryItems()
    {
        var sentinel = new Movie { Id = Guid.NewGuid() };
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(x => x.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem>(0, 1, new BaseItem[] { sentinel }));
        var service = CreateService(out var channelManager, out var collectionManager, out var userViewManager, out var tvSeriesManager, out _, out _, out _, libraryManager: libraryManager.Object);

        var folder = new Folder { Id = Guid.NewGuid() };
        // Present in Children so a wrongly-taken fast path would return this instead of the sentinel below.
        folder.Children = new BaseItem[] { new Movie { Id = Guid.NewGuid() } };

        var result = service.GetItems(folder, new InternalItemsQuery { Recursive = true });

        Assert.Single(result.Items);
        Assert.Equal(sentinel, result.Items[0]);
    }

    [Fact]
    public void GetItems_ItemIdsQuery_FallsBackInsteadOfRawQueryItems()
    {
        var sentinel = new Movie { Id = Guid.NewGuid() };
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(x => x.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem>(0, 1, new BaseItem[] { sentinel }));
        var service = CreateService(out var channelManager, out var collectionManager, out var userViewManager, out var tvSeriesManager, out _, out _, out _, libraryManager: libraryManager.Object);

        var folder = new Folder { Id = Guid.NewGuid() };
        folder.Children = new BaseItem[] { new Movie { Id = Guid.NewGuid() } };

        var result = service.GetItems(folder, new InternalItemsQuery { ItemIds = new[] { sentinel.Id } });

        Assert.Single(result.Items);
        Assert.Equal(sentinel, result.Items[0]);
    }

    [Fact]
    public void GetItems_ChannelFolder_FallsBackInsteadOfRawQueryItems()
    {
        var service = CreateService(out var channelManager, out var collectionManager, out var userViewManager, out var tvSeriesManager, out _, out _, out _);

        var folder = new Folder { Id = Guid.NewGuid(), ChannelId = Guid.NewGuid() };
        folder.Children = new BaseItem[] { new Movie { Id = Guid.NewGuid() } };

        var channelItem = new Movie { Id = Guid.NewGuid() };
        channelManager
            .Setup(x => x.GetChannelItemsInternal(It.IsAny<InternalItemsQuery>(), It.IsAny<IProgress<double>>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new QueryResult<BaseItem>(0, 1, new BaseItem[] { channelItem }));

        var result = service.GetItems(folder, new InternalItemsQuery());

        Assert.Single(result.Items);
        Assert.Equal(channelItem, result.Items[0]);
    }

    [Fact]
    public void GetItems_UserView_FallsBackInsteadOfRawQueryItems()
    {
        // UserView.SupportsRawQueryItems is false - its own GetItemsInternal (resolving
        // DisplayParentId to a channel-sourced Folder, cf. FolderTests' equivalent test) must be
        // used, not the base GetRawQueryItems on the UserView itself (which has no Children set here
        // and would silently return an empty result if the fast path were wrongly taken).
        var displayParent = new Folder { Id = Guid.NewGuid(), ChannelId = Guid.NewGuid() };
        var userView = new UserView { Id = Guid.NewGuid(), DisplayParentId = displayParent.Id };
        var channelItem = new Movie { Id = Guid.NewGuid() };

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(x => x.GetItemById(displayParent.Id)).Returns(displayParent);
        var service = CreateService(out var channelManager, out var collectionManager, out var userViewManager, out var tvSeriesManager, out _, out _, out _, libraryManager: libraryManager.Object);
        BaseItem.Logger = NullLogger<BaseItem>.Instance;

        channelManager
            .Setup(x => x.GetChannelItemsInternal(It.IsAny<InternalItemsQuery>(), It.IsAny<IProgress<double>>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new QueryResult<BaseItem>(0, 1, new BaseItem[] { channelItem }));

        var result = service.GetItems(userView, new InternalItemsQuery());

        Assert.Single(result.Items);
        Assert.Equal(channelItem, result.Items[0]);
    }

    [Fact]
    public void GetItemList_FastPath_ForcesEnableTotalRecordCountFalse()
    {
        var service = CreateService(out var channelManager, out var collectionManager, out var userViewManager, out var tvSeriesManager, out _, out _, out _);

        var folder = new Folder { Id = Guid.NewGuid() };
        var child = new Movie { Id = Guid.NewGuid() };
        folder.Children = new BaseItem[] { child };

        var query = new InternalItemsQuery { EnableTotalRecordCount = true };

        var result = service.GetItemList(folder, query);

        Assert.Single(result);
        Assert.Equal(child, result[0]);
        Assert.False(query.EnableTotalRecordCount);
    }

    [Fact]
    public void GetItems_BoxSetNonRecursive_UsesItemSortServiceInsteadOfStaticSort()
    {
        var user = new User("test-user", "provider", "provider");
        var alpha = new Movie { Id = Guid.NewGuid(), Name = "Alpha" };
        var zulu = new Movie { Id = Guid.NewGuid(), Name = "Zulu" };
        var boxSet = new BoxSet { Id = Guid.NewGuid(), DisplayOrder = nameof(ItemSortBy.SortName), Children = new BaseItem[] { zulu, alpha } };
        var libraryManager = new Mock<ILibraryManager>(MockBehavior.Strict);
        var sortService = new RecordingItemSortService();
        var service = CreateService(
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            libraryManager: libraryManager.Object,
            itemSortService: sortService);

        var result = service.GetItems(boxSet, new InternalItemsQuery(user));

        Assert.Equal(new BaseItem[] { alpha, zulu }, result.Items);
        Assert.Single(sortService.Calls);
        libraryManager.VerifyNoOtherCalls();
    }

    [Fact]
    public void GetItemList_BoxSetNonRecursive_UsesItemSortServiceInsteadOfStaticSort()
    {
        var user = new User("test-user", "provider", "provider");
        var alpha = new Movie { Id = Guid.NewGuid(), Name = "Alpha" };
        var zulu = new Movie { Id = Guid.NewGuid(), Name = "Zulu" };
        var boxSet = new BoxSet { Id = Guid.NewGuid(), DisplayOrder = nameof(ItemSortBy.SortName), Children = new BaseItem[] { zulu, alpha } };
        var libraryManager = new Mock<ILibraryManager>(MockBehavior.Strict);
        var sortService = new RecordingItemSortService();
        var service = CreateService(
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            libraryManager: libraryManager.Object,
            itemSortService: sortService);

        var result = service.GetItemList(boxSet, new InternalItemsQuery(user));

        Assert.Equal(new BaseItem[] { alpha, zulu }, result);
        Assert.Single(sortService.Calls);
        libraryManager.VerifyNoOtherCalls();
    }

    // Global-query surface tests (PR86): GetItemList(InternalItemsQuery)/GetItems(InternalItemsQuery)
    // reproduce LibraryManager.GetItemList(query, allowExternalContent)/GetItemsResult(query)
    // (Tesserafin.Server.Core/Library/LibraryManager.cs L1648/L1912), but via the cycle-free
    // IItemLookupService/IItemQueryScopeService/IItemRepository leaves instead of ILibraryManager.

    [Fact]
    public void GetItemList_RecursiveWithParentId_ResolvesParentAndScopesTopParents()
    {
        var service = CreateService(out _, out _, out _, out _, out var itemLookupService, out var itemRepository, out var scopeService);

        var parent = new Folder { Id = Guid.NewGuid() };
        var query = new InternalItemsQuery { Recursive = true, ParentId = parent.Id };
        var expected = new BaseItem[] { new Movie { Id = Guid.NewGuid() } };

        itemLookupService.Setup(x => x.GetItemById(parent.Id)).Returns(parent);
        itemRepository.Setup(x => x.GetItemList(query)).Returns(expected);

        var result = service.GetItemList(query);

        scopeService.Verify(x => x.SetTopParentIdsOrAncestors(query, It.Is<IReadOnlyCollection<BaseItem>>(p => p.Count == 1 && p.Single() == parent)), Times.Once);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetItemList_RecursiveWithParentIdNotFound_DoesNotScopeTopParents()
    {
        var service = CreateService(out _, out _, out _, out _, out var itemLookupService, out var itemRepository, out var scopeService);

        var missingParentId = Guid.NewGuid();
        var query = new InternalItemsQuery { Recursive = true, ParentId = missingParentId };
        var expected = new BaseItem[] { new Movie { Id = Guid.NewGuid() } };

        itemLookupService.Setup(x => x.GetItemById(missingParentId)).Returns((BaseItem?)null);
        itemRepository.Setup(x => x.GetItemList(query)).Returns(expected);

        var result = service.GetItemList(query);

        scopeService.Verify(x => x.SetTopParentIdsOrAncestors(It.IsAny<InternalItemsQuery>(), It.IsAny<IReadOnlyCollection<BaseItem>>()), Times.Never);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetItemList_UserNonNull_ScopesToUserAndReturnsRepositoryGetItemList()
    {
        var service = CreateService(out _, out _, out _, out _, out var itemLookupService, out var itemRepository, out var scopeService);

        var user = new User("test-user", "provider", "provider");
        var query = new InternalItemsQuery { User = user };
        var expected = new BaseItem[] { new Movie { Id = Guid.NewGuid() } };

        itemRepository.Setup(x => x.GetItemList(query)).Returns(expected);

        var result = service.GetItemList(query);

        scopeService.Verify(x => x.AddUserToQuery(query, user, true), Times.Once);
        itemLookupService.Verify(x => x.GetItemById(It.IsAny<Guid>()), Times.Never);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetItems_EnableTotalRecordCountTrue_ReturnsRepositoryGetItems()
    {
        var service = CreateService(out _, out _, out _, out _, out _, out var itemRepository, out _);

        var query = new InternalItemsQuery { EnableTotalRecordCount = true };
        var expected = new QueryResult<BaseItem>(0, 1, new BaseItem[] { new Movie { Id = Guid.NewGuid() } });

        itemRepository.Setup(x => x.GetItems(query)).Returns(expected);

        var result = service.GetItems(query);

        Assert.Same(expected, result);
        itemRepository.Verify(x => x.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public void GetItems_EnableTotalRecordCountFalse_ReturnsQueryResultWrappingRepositoryGetItemList()
    {
        var service = CreateService(out _, out _, out _, out _, out _, out var itemRepository, out _);

        var query = new InternalItemsQuery { EnableTotalRecordCount = false, StartIndex = 5 };
        var items = new BaseItem[] { new Movie { Id = Guid.NewGuid() } };

        itemRepository.Setup(x => x.GetItemList(query)).Returns(items);

        var result = service.GetItems(query);

        Assert.Equal(5, result.StartIndex);
        // Mirrors LibraryManager.GetItemsResult: new QueryResult<BaseItem>(query.StartIndex, null, items)
        // passes a null totalRecordCount, which QueryResult's constructor falls back to items.Count for.
        Assert.Equal(items.Length, result.TotalRecordCount);
        Assert.Equal(items, result.Items);
        itemRepository.Verify(x => x.GetItems(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public void GetItems_RecursiveWithParentId_ResolvesParentAndScopesTopParents()
    {
        var service = CreateService(out _, out _, out _, out _, out var itemLookupService, out var itemRepository, out var scopeService);

        var parent = new Folder { Id = Guid.NewGuid() };
        var query = new InternalItemsQuery { Recursive = true, ParentId = parent.Id, EnableTotalRecordCount = false };
        var items = new BaseItem[] { new Movie { Id = Guid.NewGuid() } };

        itemLookupService.Setup(x => x.GetItemById(parent.Id)).Returns(parent);
        itemRepository.Setup(x => x.GetItemList(query)).Returns(items);

        var result = service.GetItems(query);

        scopeService.Verify(x => x.SetTopParentIdsOrAncestors(query, It.Is<IReadOnlyCollection<BaseItem>>(p => p.Count == 1 && p.Single() == parent)), Times.Once);
        Assert.Equal(items, result.Items);
    }

    [Fact]
    public void GetItems_UserNonNull_ScopesToUser()
    {
        var service = CreateService(out _, out _, out _, out _, out _, out var itemRepository, out var scopeService);

        var user = new User("test-user", "provider", "provider");
        var query = new InternalItemsQuery { User = user, EnableTotalRecordCount = true };
        var expected = new QueryResult<BaseItem>(0, 0, Array.Empty<BaseItem>());

        itemRepository.Setup(x => x.GetItems(query)).Returns(expected);

        var result = service.GetItems(query);

        scopeService.Verify(x => x.AddUserToQuery(query, user, true), Times.Once);
        Assert.Same(expected, result);
    }

    private sealed class PassthroughItemSortService : IItemSortService
    {
        public void AddParts(IEnumerable<IBaseItemComparer> itemComparers)
        {
        }

        public IEnumerable<BaseItem> Sort(IEnumerable<BaseItem> items, User? user, IEnumerable<ItemSortBy> sortBy, SortOrder sortOrder)
            => items;

        public IEnumerable<BaseItem> Sort(IEnumerable<BaseItem> items, User? user, IEnumerable<(ItemSortBy OrderBy, SortOrder SortOrder)> orderBy)
            => items;
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

            return sortOrder == SortOrder.Descending
                ? itemList.OrderByDescending(i => i.SortName, StringComparer.OrdinalIgnoreCase)
                : itemList.OrderBy(i => i.SortName, StringComparer.OrdinalIgnoreCase);
        }

        public IEnumerable<BaseItem> Sort(IEnumerable<BaseItem> items, User? user, IEnumerable<(ItemSortBy OrderBy, SortOrder SortOrder)> orderBy)
            => throw new NotSupportedException("BoxSet raw-query tests use the 4-argument Sort overload.");
    }
}
