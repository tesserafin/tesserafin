using System;
using System.Collections.Generic;
using System.Threading;
using AutoFixture;
using AutoFixture.AutoMoq;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Resolvers;
using Tesserafin.Controller.Sorting;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Globalization;
using Tesserafin.Naming.Common;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library.LibraryManager;

/// <summary>
/// Cross-checks between <see cref="Tesserafin.Server.Core.Library.ItemStore"/> (PR106a) and the real
/// <see cref="Tesserafin.Server.Core.Library.LibraryManager"/> it is injected into: parity of
/// <c>GetNewItemId</c> and the save-then-register subset of <c>CreateItem</c>, and the
/// exactly-once <c>ItemAdded</c> contract (RFC <c>docs/rfc-di-query-user-views-v2.md</c> §2,
/// decided in PR105b) between the new <c>ItemStore.ItemSaved</c> relay and the historical
/// <c>LibraryManager.CreateItems</c> path. Fixture setup mirrors
/// <see cref="LibraryManagerItemLookupTests"/>: a single, shared, real
/// <see cref="Tesserafin.Server.Core.Library.ItemLookupService"/> instance backs both
/// <see cref="IItemLookupService"/> and the internal <see cref="Tesserafin.Server.Core.Library.IItemCacheStore"/>
/// port, and a single, shared, real <see cref="Tesserafin.Server.Core.Library.ItemStore"/> instance
/// (built from that same cache plus the frozen persistence/configuration mocks) is registered for
/// both the concrete type and <see cref="IItemStore"/>, exactly like the double/triple-singleton
/// wiring in <c>ApplicationHost</c>.
/// </summary>
[Collection(LibraryManagerStaticStateFixture.Name)]
public class LibraryManagerItemStoreTests
{
    private readonly Tesserafin.Server.Core.Library.LibraryManager _libraryManager;
    private readonly Tesserafin.Server.Core.Library.ItemLookupService _itemLookupService;
    private readonly Tesserafin.Server.Core.Library.ItemStore _itemStore;
    private readonly Mock<IItemRepository> _itemRepositoryMock;
    private readonly Mock<IItemPersistenceService> _persistenceServiceMock;
    private readonly Mock<IServerConfigurationManager> _configurationManagerMock;

    public LibraryManagerItemStoreTests()
    {
        var fixture = new Fixture().Customize(new AutoMoqCustomization());
        fixture.Register(() => new NamingOptions());

        _configurationManagerMock = fixture.Freeze<Mock<IServerConfigurationManager>>();
        _configurationManagerMock.Setup(c => c.ApplicationPaths.ProgramDataPath).Returns("/data");
        _configurationManagerMock.Setup(c => c.ApplicationPaths.InternalMetadataPath).Returns("/data/metadata");
        _configurationManagerMock.Setup(c => c.Configuration).Returns(new ServerConfiguration());

        _itemRepositoryMock = fixture.Freeze<Mock<IItemRepository>>();
        _itemRepositoryMock.Setup(i => i.RetrieveItem(It.IsAny<Guid>())).Returns<BaseItem>(null);

        _persistenceServiceMock = fixture.Freeze<Mock<IItemPersistenceService>>();

        var externalDataManagerMock = fixture.Freeze<Mock<IExternalDataManager>>();
        fixture.Register(() => new Lazy<IExternalDataManager>(() => externalDataManagerMock.Object));

        // Same single-instance double-singleton wiring as LibraryManagerItemLookupTests (PR75/PR76):
        // one real ItemLookupService backs both IItemLookupService and IItemCacheStore.
        _itemLookupService = new Tesserafin.Server.Core.Library.ItemLookupService(_itemRepositoryMock.Object, _configurationManagerMock.Object);
        fixture.Register(() => _itemLookupService);
        fixture.Register<IItemLookupService>(() => _itemLookupService);
        fixture.Register<Tesserafin.Server.Core.Library.IItemCacheStore>(() => _itemLookupService);

        var itemAccessService = new Tesserafin.Server.Core.Library.ItemAccessService(
            _itemLookupService,
            new Tesserafin.Server.Core.Library.TagVisibilityService(_itemLookupService, fixture.Freeze<ICollectionFolderResolver>(), fixture.Freeze<IUserRootFolderProvider>(), _itemRepositoryMock.Object, fixture.Freeze<ILocalizationManager>(), fixture.Freeze<IServerConfigurationManager>(), new Tesserafin.Controller.LiveTv.ActiveRecordings()));
        fixture.Register<IItemAccessService>(() => itemAccessService);

        // PR106a: same pattern, extended to ItemStore - one real instance backs both the concrete
        // type and IItemStore, built from the frozen persistence mock and the shared cache above,
        // exactly like ApplicationHost's AddSingleton<IItemStore, ItemStore>() registration.
        _itemStore = new Tesserafin.Server.Core.Library.ItemStore(
            _persistenceServiceMock.Object,
            _itemLookupService,
            _configurationManagerMock.Object,
            NullLogger<Tesserafin.Server.Core.Library.ItemStore>.Instance);
        fixture.Register(() => _itemStore);
        fixture.Register<IItemStore>(() => _itemStore);

        _libraryManager = fixture.Build<Tesserafin.Server.Core.Library.LibraryManager>().Do(s => s.AddParts(
                fixture.Create<IEnumerable<IResolverIgnoreRule>>(),
                fixture.Create<IEnumerable<IItemResolver>>(),
                fixture.Create<IEnumerable<IIntroProvider>>(),
                fixture.Create<IEnumerable<IBaseItemComparer>>()))
            .Create();
    }

    // ---------------------------------------------------------------
    // GetNewItemId parity (RFC §2, §9/PR106a: "tests de parité contre le chemin historique").
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("/data/views/movies_namedview_Movies")]
    [InlineData("relative/path/without/prefix")]
    [InlineData("Key-With-MIXED-Case")]
    [InlineData("38_namedview_Moviesabc123")]
    public void GetNewItemId_ItemStoreAndLibraryManager_SameInputs_ReturnSameGuid(string key)
    {
        var viaItemStore = _itemStore.GetNewItemId(key, typeof(UserView));
        var viaLibraryManager = _libraryManager.GetNewItemId(key, typeof(UserView));

        Assert.Equal(viaLibraryManager, viaItemStore);
    }

    // ---------------------------------------------------------------
    // CreateItem parity: UserView/parent=null subset actually used by GetNamedView/GetShadowView.
    // ---------------------------------------------------------------

    [Fact]
    public void CreateItem_ViaItemStore_UserViewParentNull_PersistedAndRegisteredInSharedCache()
    {
        var item = new UserView { Id = Guid.NewGuid(), Name = "Movies" };

        _itemStore.CreateItem(item, null);

        // Registered through IItemCacheStore -> visible from LibraryManager.GetItemById via the
        // exact same shared ItemLookupService instance, with no repository round-trip.
        var result = _libraryManager.GetItemById(item.Id);

        Assert.Same(item, result);
        _persistenceServiceMock.Verify(p => p.SaveItems(It.Is<IReadOnlyList<BaseItem>>(items => items.Count == 1 && items[0] == item), It.IsAny<CancellationToken>()), Times.Once);
        _itemRepositoryMock.Verify(r => r.RetrieveItem(It.IsAny<Guid>()), Times.Never);
    }

    // ---------------------------------------------------------------
    // Exactly-once ItemAdded (RFC §2, §9/PR106a: "test exactly-once ItemAdded").
    // ---------------------------------------------------------------

    [Fact]
    public void CreateItem_ViaItemStore_RaisesExactlyOneItemAddedOnLibraryManager()
    {
        var item = new UserView { Id = Guid.NewGuid(), Name = "Movies" };
        var raisedCount = 0;
        ItemChangeEventArgs? raisedArgs = null;
        _libraryManager.ItemAdded += (_, args) =>
        {
            raisedCount++;
            raisedArgs = args;
        };

        _itemStore.CreateItem(item, null);

        Assert.Equal(1, raisedCount);
        Assert.NotNull(raisedArgs);
        Assert.Same(item, raisedArgs!.Item);
    }

    [Fact]
    public void CreateItems_ViaLibraryManagerHistoricalPath_RaisesExactlyOneItemAdded_NotTwo()
    {
        // LibraryManager.CreateItems does not go through IItemStore at all - it must keep raising
        // ItemAdded directly, exactly once, with no doubling via the new ItemStore.ItemSaved relay
        // (which is only ever triggered by ItemStore.CreateItem, never by LibraryManager.CreateItems).
        var item = new UserView { Id = Guid.NewGuid(), Name = "Movies" };
        var raisedCount = 0;
        _libraryManager.ItemAdded += (_, _) => raisedCount++;

        _libraryManager.CreateItems(new List<BaseItem> { item }, null, CancellationToken.None);

        Assert.Equal(1, raisedCount);
    }

    // ---------------------------------------------------------------
    // DI wiring identity (RFC I1, §9/PR106a): the singleton LibraryManager is subscribed to must be
    // the exact instance the container hands out under IItemStore, not merely "an" IItemStore.
    // ---------------------------------------------------------------

    [Fact]
    public void DiWiring_LibraryManagerOnlyRelaysFromInjectedItemStoreSingleton_NotFromADifferentInstance()
    {
        // A second, independently-constructed ItemStore (same dependencies, different object
        // identity) must NOT be able to trigger LibraryManager.ItemAdded - proves the subscription
        // set up in LibraryManager's constructor is bound to the exact singleton instance injected
        // (fixture-registered above as both the concrete type and IItemStore), matching the
        // single-instance ApplicationHost wiring rather than "any IItemStore".
        var otherItemStore = new Tesserafin.Server.Core.Library.ItemStore(
            _persistenceServiceMock.Object,
            _itemLookupService,
            _configurationManagerMock.Object,
            NullLogger<Tesserafin.Server.Core.Library.ItemStore>.Instance);

        var raised = false;
        _libraryManager.ItemAdded += (_, _) => raised = true;

        var item = new UserView { Id = Guid.NewGuid(), Name = "Movies" };
        otherItemStore.CreateItem(item, null);

        Assert.False(raised);

        // Sanity check: the injected singleton still works as expected.
        var itemViaInjectedSingleton = new UserView { Id = Guid.NewGuid(), Name = "TV Shows" };
        _itemStore.CreateItem(itemViaInjectedSingleton, null);

        Assert.True(raised);
    }
}
