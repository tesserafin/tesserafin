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
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Resolvers;
using Tesserafin.Controller.Sorting;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Globalization;
using Tesserafin.Naming.Common;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library.LibraryManager;

/// <summary>
/// Shared cache, identity and notification contracts for user-view and general item creation.
/// </summary>
[Collection(LibraryManagerStaticStateFixture.Name)]
public class LibraryManagerItemStoreTests
{
    private readonly IItemWriteCommands _writes;
    private readonly Tesserafin.Server.Core.Library.LibraryManager _libraryManager;
    private readonly Tesserafin.Server.Core.Library.ItemLookupService _itemLookupService;
    private readonly Tesserafin.Server.Core.Library.ItemStore _itemStore;
    private readonly Tesserafin.Server.Core.Library.ItemChangeEvents _changes = new(NullLogger<Tesserafin.Server.Core.Library.ItemChangeEvents>.Instance);
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
            _changes,
            new ActiveRecordings());
        fixture.Register<IItemChangePublisher>(() => _changes);
        fixture.Register(() => _itemStore);
        fixture.Register<IItemStore>(() => _itemStore);

        _writes = Tesserafin.Server.Implementations.Tests.Library.LibraryWriteFixture.Register(fixture);

        _libraryManager = fixture.Build<Tesserafin.Server.Core.Library.LibraryManager>().Do(s => s.AddParts(
                fixture.Create<IEnumerable<IResolverIgnoreRule>>(),
                fixture.Create<IEnumerable<IItemResolver>>(),
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
    public void CreateItem_ViaItemStore_PublishesExactlyOneItemAdded()
    {
        var item = new UserView { Id = Guid.NewGuid(), Name = "Movies" };
        var raisedCount = 0;
        ItemChangeEventArgs? raisedArgs = null;
        _changes.ItemAdded += (_, args) =>
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
    public void CreateItems_ViaLibraryManager_RaisesExactlyOneItemAdded_NotTwo()
    {
        // The two creation owners publish to one feed without a relay between them.
        var item = new UserView { Id = Guid.NewGuid(), Name = "Movies" };
        var raisedCount = 0;
        _changes.ItemAdded += (_, _) => raisedCount++;

        _writes.CreateItems(new List<BaseItem> { item }, null, CancellationToken.None);

        Assert.Equal(1, raisedCount);
    }

    [Fact]
    public void CreateItems_ParentLookupFailure_DoesNotFailAnAlreadyPublishedItem()
    {
        var item = new UserView { Id = Guid.NewGuid(), ParentId = Guid.NewGuid(), Name = "Movies" };
        _itemRepositoryMock.Setup(r => r.RetrieveItem(item.ParentId)).Throws(new InvalidOperationException("parent lookup unavailable"));
        var notifications = 0;
        _changes.ItemAdded += (_, _) => notifications++;

        _writes.CreateItems([item], null, CancellationToken.None);

        Assert.Same(item, _libraryManager.GetItemById(item.Id));
        _persistenceServiceMock.Verify(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public void IndependentItemStore_PublishesWithoutLibraryManagerRelay()
    {
        var otherItemStore = new Tesserafin.Server.Core.Library.ItemStore(
            _persistenceServiceMock.Object,
            _itemLookupService,
            _configurationManagerMock.Object,
            _changes,
            new ActiveRecordings());
        var count = 0;
        _changes.ItemAdded += (_, _) => count++;

        otherItemStore.CreateItem(new UserView { Id = Guid.NewGuid(), Name = "Movies" }, null);
        _itemStore.CreateItem(new UserView { Id = Guid.NewGuid(), Name = "TV Shows" }, null);

        Assert.Equal(2, count);
    }
}
