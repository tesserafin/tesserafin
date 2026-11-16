using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System;
using AutoFixture.AutoMoq;
using AutoFixture;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Resolvers;
using Tesserafin.Controller.Sorting;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.IO;
using Tesserafin.Naming.Common;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library.LibraryManager;

/// <summary>
/// Parity tests between the delegated <see cref="Tesserafin.Server.Core.Library.LibraryManager"/>
/// <c>GetNamedView</c>/<c>GetShadowView</c> facade and the historical, now-ported-off behavior it
/// used to implement inline (RFC <c>docs/rfc-di-query-user-views-v2.md</c> §2/§9, PR106b). Fixture
/// setup mirrors <see cref="LibraryManagerItemStoreTests"/> (PR106a): a single, shared, real
/// <see cref="Tesserafin.Server.Core.Library.ItemLookupService"/> instance backs both
/// <see cref="IItemLookupService"/> and the internal <see cref="Tesserafin.Server.Core.Library.IItemCacheStore"/>
/// port, a single, shared, real <see cref="Tesserafin.Server.Core.Library.ItemStore"/> instance is
/// registered for both the concrete type and <see cref="IItemStore"/>, and - new in PR106b - a
/// single, shared, real <see cref="Tesserafin.Server.Core.Library.UserViewFactory"/> instance (built
/// from that same lookup/store pair, plus an explicitly-controlled <see cref="Lazy{IProviderManager}"/>
/// so I2 evaluation timing is directly observable) is registered for both the concrete type and
/// <see cref="IUserViewFactory"/>, exactly like the multi-singleton wiring in <c>ApplicationHost</c>.
/// This is deliberate, not incidental: with plain AutoMoq wiring, <c>LibraryManager</c>'s new
/// <see cref="IUserViewFactory"/> constructor dependency would resolve to an auto-mocked stub whose
/// <c>GetNamedView</c>/<c>GetShadowView</c> return <c>null</c> - these tests would then exercise the
/// mock, not the real delegation, and pass vacuously.
/// </summary>
[Collection(LibraryManagerStaticStateFixture.Name)]
public class LibraryManagerUserViewFactoryTests
{
    private readonly Tesserafin.Server.Core.Library.LibraryManager _libraryManager;
    private readonly Tesserafin.Server.Core.Library.ItemLookupService _itemLookupService;
    private readonly Tesserafin.Server.Core.Library.ItemStore _itemStore;
    private readonly Tesserafin.Server.Core.Library.UserViewFactory _userViewFactory;
    private readonly IItemWriteCommands _writes;
    private readonly Mock<IItemRepository> _itemRepositoryMock;
    private readonly Mock<IItemPersistenceService> _persistenceServiceMock;
    private readonly Mock<IServerConfigurationManager> _configurationManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<IProviderManager> _providerManagerMock;

    public LibraryManagerUserViewFactoryTests()
    {
        var fixture = new Fixture().Customize(new AutoMoqCustomization());
        fixture.Register(() => new NamingOptions());

        var internalMetadataPath = Path.Combine(Path.GetTempPath(), "reefin-uvf-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(internalMetadataPath);

        _configurationManagerMock = fixture.Freeze<Mock<IServerConfigurationManager>>();
        _configurationManagerMock.Setup(c => c.ApplicationPaths.ProgramDataPath).Returns("/data");
        _configurationManagerMock.Setup(c => c.ApplicationPaths.InternalMetadataPath).Returns(internalMetadataPath);
        _configurationManagerMock.Setup(c => c.Configuration).Returns(new ServerConfiguration());

        _itemRepositoryMock = fixture.Freeze<Mock<IItemRepository>>();
        _itemRepositoryMock.Setup(i => i.RetrieveItem(It.IsAny<Guid>())).Returns<BaseItem>(null);

        _persistenceServiceMock = fixture.Freeze<Mock<IItemPersistenceService>>();

        var externalDataManagerMock = fixture.Freeze<Mock<IExternalDataManager>>();
        fixture.Register(() => new Lazy<IExternalDataManager>(() => externalDataManagerMock.Object));

        // Same single-instance double-singleton wiring as LibraryManagerItemStoreTests (PR75/PR76):
        // one real ItemLookupService backs both IItemLookupService and IItemCacheStore.
        _itemLookupService = new Tesserafin.Server.Core.Library.ItemLookupService(_itemRepositoryMock.Object, _configurationManagerMock.Object);
        fixture.Register(() => _itemLookupService);
        fixture.Register<IItemLookupService>(() => _itemLookupService);
        fixture.Register<Tesserafin.Server.Core.Library.IItemCacheStore>(() => _itemLookupService);

        var itemAccessService = new Tesserafin.Server.Core.Library.ItemAccessService(
            _itemLookupService,
            new Tesserafin.Server.Core.Library.TagVisibilityService(_itemLookupService, fixture.Freeze<ICollectionFolderResolver>(), fixture.Freeze<IUserRootFolderProvider>(), _itemRepositoryMock.Object, fixture.Freeze<ILocalizationManager>(), fixture.Freeze<IServerConfigurationManager>(), new Tesserafin.Controller.LiveTv.ActiveRecordings()));
        fixture.Register<IItemAccessService>(() => itemAccessService);

        // PR106a: one real ItemStore instance backs both the concrete type and IItemStore.
        _itemStore = new Tesserafin.Server.Core.Library.ItemStore(
            _persistenceServiceMock.Object,
            _itemLookupService,
            _configurationManagerMock.Object,
            Mock.Of<IItemChangePublisher>());
        fixture.Register(() => _itemStore);
        fixture.Register<IItemStore>(() => _itemStore);

        _fileSystemMock = new Mock<IFileSystem>();
        _fileSystemMock.Setup(f => f.GetValidFilename(It.IsAny<string>())).Returns<string>(s => s);

        _providerManagerMock = new Mock<IProviderManager>();

        // PR106b: one real UserViewFactory instance backs both the concrete type and
        // IUserViewFactory - built on the same ItemLookupService/ItemStore pair as LibraryManager
        // itself, with its own explicitly-controlled Lazy<IProviderManager> (distinct from
        // LibraryManager's own, AutoFixture-provided one) so tests can assert on I2 evaluation
        // timing directly against a known mock instance.
        _writes = Tesserafin.Server.Implementations.Tests.Library.LibraryWriteFixture.Register(fixture);
        _userViewFactory = new Tesserafin.Server.Core.Library.UserViewFactory(
            _writes,
            _itemLookupService,
            _itemStore,
            _configurationManagerMock.Object,
            _fileSystemMock.Object,
            new Lazy<IProviderManager>(() => _providerManagerMock.Object));
        fixture.Register(() => _userViewFactory);
        fixture.Register<IUserViewFactory>(() => _userViewFactory);

        _libraryManager = fixture.Build<Tesserafin.Server.Core.Library.LibraryManager>().Do(s => s.AddParts(
                fixture.Create<IEnumerable<IResolverIgnoreRule>>(),
                fixture.Create<IEnumerable<IItemResolver>>(),
                fixture.Create<IEnumerable<IIntroProvider>>(),
                fixture.Create<IEnumerable<IBaseItemComparer>>()))
            .Create();
    }

    // ---------------------------------------------------------------
    // GetNamedView(User, string, CollectionType?, string) - delegates to the 5-arg overload with
    // parentId = Guid.Empty (ILibraryManager.cs:448/LibraryManager.cs:2792 historically, now
    // UserViewFactory.cs).
    // ---------------------------------------------------------------

    [Fact]
    public void GetNamedView_UserOverload_NewView_PersistedRegisteredWithExpectedProperties()
    {
        var user = new User("alice", "provider", "provider") { Id = Guid.NewGuid() };

        var view = _libraryManager.GetNamedView(user, "Movies", CollectionType.movies, "SortName");

        Assert.NotNull(view);
        Assert.Equal("Movies", view.Name);
        Assert.Equal(CollectionType.movies, view.ViewType);
        Assert.Equal("SortName", view.ForcedSortName);
        Assert.Equal(user.Id, view.UserId);
        Assert.True(view.DisplayParentId.IsEmpty());

        // Deterministic id: same idValues formula as the historical LibraryManager.cs:2855
        // ("38_namedview_" + name + user.Id + parentIdString + viewType), parentId empty here.
        var expectedIdValues = "38_namedview_" + "Movies" + user.Id.ToString("N", CultureInfo.InvariantCulture) + CollectionType.movies;
        var expectedId = _itemStore.GetNewItemId(expectedIdValues, typeof(UserView));
        Assert.Equal(expectedId, view.Id);

        Assert.Same(view, _itemLookupService.GetItemById(view.Id));
        _persistenceServiceMock.Verify(p => p.SaveItems(It.Is<IReadOnlyList<BaseItem>>(items => items.Count == 1 && items[0] == view), It.IsAny<CancellationToken>()), Times.Once);
        _providerManagerMock.Verify(p => p.QueueRefresh(view.Id, It.IsAny<MetadataRefreshOptions>(), RefreshPriority.Normal), Times.Once);
    }

    [Fact]
    public void GetNamedView_UserOverload_CalledTwice_SecondCallReturnsSameInstance_NoReCreation()
    {
        var user = new User("bob", "provider", "provider") { Id = Guid.NewGuid() };

        var first = _libraryManager.GetNamedView(user, "TV Shows", CollectionType.tvshows, "SortName");
        var second = _libraryManager.GetNamedView(user, "TV Shows", CollectionType.tvshows, "SortName");

        Assert.Same(first, second);
        _persistenceServiceMock.Verify(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---------------------------------------------------------------
    // GetShadowView(BaseItem, CollectionType?, string) - ILibraryManager.cs:489/LibraryManager.cs:2910.
    // ---------------------------------------------------------------

    [Fact]
    public void GetShadowView_NewView_PersistedRegisteredWithExpectedProperties()
    {
        var parentPath = Path.Combine(Path.GetTempPath(), "reefin-uvf-parent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parentPath);
        var parent = new Folder { Id = Guid.NewGuid(), Name = "Parent Folder", Path = parentPath };

        var view = _libraryManager.GetShadowView(parent, CollectionType.homevideos, "SortName");

        Assert.Equal(parent.Name, view.Name);
        Assert.Equal(parent.Id, view.DisplayParentId);
        Assert.Equal(parentPath, view.Path);
        Assert.Same(view, _itemLookupService.GetItemById(view.Id));
        _persistenceServiceMock.Verify(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()), Times.Once);
        _providerManagerMock.Verify(p => p.QueueRefresh(view.Id, It.IsAny<MetadataRefreshOptions>(), RefreshPriority.Normal), Times.Once);
    }

    // ---------------------------------------------------------------
    // GetNamedView(User, string, Guid, CollectionType?, string) - sub-view with an explicit
    // parentId (ILibraryManager.cs:433/LibraryManager.cs:2845).
    // ---------------------------------------------------------------

    [Fact]
    public void GetNamedView_UserAndParentIdOverload_NewSubView_DisplayParentIdSet()
    {
        var user = new User("carol", "provider", "provider") { Id = Guid.NewGuid() };
        var parentId = Guid.NewGuid();

        var view = _libraryManager.GetNamedView(user, "Recently Added", parentId, CollectionType.movies, "SortName");

        Assert.Equal(parentId, view.DisplayParentId);
        Assert.Equal(user.Id, view.UserId);
        Assert.Same(view, _itemLookupService.GetItemById(view.Id));
        _persistenceServiceMock.Verify(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---------------------------------------------------------------
    // GetNamedView(string, Guid, CollectionType?, string, string) - the uniqueId overload
    // (ILibraryManager.cs:475/LibraryManager.cs:2974).
    // ---------------------------------------------------------------

    [Fact]
    public void GetNamedView_UniqueIdOverload_NewView_DisplayParentIdSetAndPersisted()
    {
        var parentId = Guid.NewGuid();

        var view = _libraryManager.GetNamedView("Folders", parentId, CollectionType.boxsets, "SortName", "unique-1");

        Assert.Equal(parentId, view.DisplayParentId);
        Assert.Equal(CollectionType.boxsets, view.ViewType);
        Assert.Same(view, _itemLookupService.GetItemById(view.Id));
        _persistenceServiceMock.Verify(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---------------------------------------------------------------
    // GetNamedView(string, CollectionType, string) - the top-level, no-parentId overload
    // (ILibraryManager.cs:461/LibraryManager.cs:2801). The only one of the 5 that calls
    // item.UpdateToRepositoryAsync(...) on its new-item path, which reaches the static
    // BaseItem.LibraryManager fallback (BaseItem.cs:2370) - pre-existing behavior, reproduced
    // identically (see the UserViewFactory type-level remarks and this PR's final report).
    // ---------------------------------------------------------------

    [Fact]
    public void GetNamedView_NameViewTypeSortNameOverload_NewView_PersistedAndRefreshed()
    {
        Tesserafin.Controller.Entities.BaseItem.ConfigurationManager = _configurationManagerMock.Object;
        Tesserafin.Controller.Entities.BaseItem.LibraryManager = _libraryManager;
        Tesserafin.Controller.Entities.BaseItem.MediaSourceManager = Mock.Of<IMediaSourceManager>();

        var view = _libraryManager.GetNamedView("Movies", CollectionType.movies, "SortName");

        Assert.Equal("Movies", view.Name);
        Assert.Equal(CollectionType.movies, view.ViewType);
        Assert.Same(view, _itemLookupService.GetItemById(view.Id));

        // Two SaveItems calls, exactly like the historical implementation: one from
        // IItemStore.CreateItem (PR106a), one from the item.UpdateToRepositoryAsync(...) call
        // this overload makes on its new-item path (LibraryManager.cs:2838 historically,
        // UserViewFactory.cs today) - reproduced identically, not "fixed" into a single save.
        _persistenceServiceMock.Verify(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _providerManagerMock.Verify(p => p.QueueRefresh(view.Id, It.IsAny<MetadataRefreshOptions>(), RefreshPriority.Normal), Times.Once);
    }

    // ---------------------------------------------------------------
    // I2 (RFC §8): Lazy<IProviderManager>.Value evaluation timing, observed through the delegated
    // LibraryManager facade (proves the delegation itself does not change the timing).
    // ---------------------------------------------------------------

    [Fact]
    public void GetNamedView_ExistingViewAlreadyRefreshed_LazyProviderManagerValueNeverEvaluated()
    {
        var user = new User("dave", "provider", "provider") { Id = Guid.NewGuid() };

        // Phase 1: create the view normally (the shared _userViewFactory, working ProviderManager
        // mock), then mark it as already recently refreshed - "existing view, refresh not due" is
        // the common case at the real GetUserViews call sites.
        var created = _libraryManager.GetNamedView(user, "Music", CollectionType.music, "SortName");
        created.DateLastRefreshed = DateTime.UtcNow;

        // Phase 2: re-fetch through a second UserViewFactory sharing the same backing
        // ItemLookupService/ItemStore, wired to a Lazy<IProviderManager> that throws if its factory
        // is ever invoked - proves .Value is never touched when no refresh is due.
        var throwingLazy = new Lazy<IProviderManager>(() => throw new InvalidOperationException(
            "Lazy<IProviderManager>.Value evaluated when no refresh was due (RFC I2 violation)."));
        var factoryWithThrowingProvider = new Tesserafin.Server.Core.Library.UserViewFactory(
            _writes,
            _itemLookupService,
            _itemStore,
            _configurationManagerMock.Object,
            _fileSystemMock.Object,
            throwingLazy);

        var refetched = factoryWithThrowingProvider.GetNamedView(user, "Music", CollectionType.music, "SortName");

        Assert.Same(created, refetched);
    }

    [Fact]
    public void GetShadowView_NewView_LazyProviderManagerValueEvaluatedOnlyAfterSaveAndRegister()
    {
        var callOrder = new List<string>();

        var persistenceMock = new Mock<IItemPersistenceService>();
        persistenceMock
            .Setup(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("save"));

        var cacheStore = new OrderTrackingItemCacheStore(callOrder);

        var itemStore = new Tesserafin.Server.Core.Library.ItemStore(
            persistenceMock.Object,
            cacheStore,
            _configurationManagerMock.Object,
            Mock.Of<IItemChangePublisher>());

        var lookupServiceMock = new Mock<IItemLookupService>();
        lookupServiceMock.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((BaseItem?)null);

        var providerManagerMock = new Mock<IProviderManager>();
        var lazyProviderManager = new Lazy<IProviderManager>(() =>
        {
            callOrder.Add("providerManager.Value");
            return providerManagerMock.Object;
        });

        var factory = new Tesserafin.Server.Core.Library.UserViewFactory(
            _writes,
            lookupServiceMock.Object,
            itemStore,
            _configurationManagerMock.Object,
            _fileSystemMock.Object,
            lazyProviderManager);

        var parentPath = Path.Combine(Path.GetTempPath(), "reefin-uvf-i2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parentPath);
        var parent = new Folder { Id = Guid.NewGuid(), Name = "I2 parent", Path = parentPath };

        factory.GetShadowView(parent, CollectionType.homevideos, "SortName");

        Assert.Equal(new[] { "save", "register", "providerManager.Value" }, callOrder);
    }

    /// <summary>
    /// Hand-written internal <see cref="Tesserafin.Server.Core.Library.IItemCacheStore"/> double, purely
    /// to record when <c>Register</c> runs relative to <c>SaveItems</c> and
    /// <c>Lazy&lt;IProviderManager&gt;.Value</c> - see <see cref="Tesserafin.Server.Implementations.Tests.Library.ItemStoreTests"/>'s
    /// own <c>FakeItemCacheStore</c> remarks for why a hand-written double is used here instead of Moq.
    /// </summary>
    private sealed class OrderTrackingItemCacheStore : Tesserafin.Server.Core.Library.IItemCacheStore
    {
        private readonly List<string> _callOrder;

        public OrderTrackingItemCacheStore(List<string> callOrder)
        {
            _callOrder = callOrder;
        }

        public void Register(BaseItem item) => _callOrder.Add("register");

        public void Remove(Guid id)
        {
        }

        public void RemoveRange(IEnumerable<Guid> ids)
        {
        }
    }
}
