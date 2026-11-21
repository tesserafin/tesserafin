using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using AutoFixture.AutoMoq;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LibraryTaskScheduler;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Resolvers;
using Tesserafin.Controller.Sorting;
using Tesserafin.Data;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Globalization;
using Tesserafin.Naming.Common;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library.LibraryManager;

/// <summary>
/// Characterization tests for <see cref="Tesserafin.Server.Core.Library.LibraryManager"/> item lookup
/// (<c>GetItemById</c> family) and its cache (<c>_cache</c>, a <c>FastConcurrentLru</c>). These tests
/// lock down the current behavior, including the cache coherence invariant introduced by PR70:
/// every cache write goes through the <c>ShouldCacheItem</c>/<c>RegisterItemInCache</c>/
/// <c>RemoveItemFromCache</c>/<c>RemoveItemsFromCache</c> primitives.
/// </summary>
[Collection(LibraryManagerStaticStateFixture.Name)]
public class LibraryManagerItemLookupTests
{
    private readonly IItemWriteCommands _writes;
    private readonly Tesserafin.Server.Core.Library.LibraryManager _libraryManager;
    private readonly Tesserafin.Server.Core.Library.ItemDeletionCommands _deletions;
    private readonly Tesserafin.Server.Core.Library.ItemChangeEvents _changes = new(Microsoft.Extensions.Logging.Abstractions.NullLogger<Tesserafin.Server.Core.Library.ItemChangeEvents>.Instance);
    private readonly Tesserafin.Server.Core.Library.ItemLookupService _itemLookupService;
    private readonly Tesserafin.Server.Core.Library.ItemAccessService _itemAccessService;
    private readonly Mock<IItemRepository> _itemRepositoryMock;
    private readonly Mock<IItemPersistenceService> _persistenceServiceMock;
    private readonly Mock<IServerConfigurationManager> _configurationManagerMock;
    private readonly Mock<IExternalDataManager> _externalDataManagerMock;
    private readonly Mock<IUserRootFolderProvider> _userRootFolderProviderMock;

    public LibraryManagerItemLookupTests()
    {
        var fixture = new Fixture().Customize(new AutoMoqCustomization());
        fixture.Register<IItemChangePublisher>(() => _changes);
        fixture.Register(() => new NamingOptions());

        _configurationManagerMock = fixture.Freeze<Mock<IServerConfigurationManager>>();
        _configurationManagerMock.Setup(c => c.ApplicationPaths.ProgramDataPath).Returns("/data");
        _configurationManagerMock.Setup(c => c.ApplicationPaths.InternalMetadataPath).Returns("/data/metadata");
        _configurationManagerMock.Setup(c => c.Configuration).Returns(new ServerConfiguration());

        _itemRepositoryMock = fixture.Freeze<Mock<IItemRepository>>();
        _itemRepositoryMock.Setup(i => i.RetrieveItem(It.IsAny<Guid>())).Returns<BaseItem>(null);

        _persistenceServiceMock = fixture.Freeze<Mock<IItemPersistenceService>>();

        // No user is known to this manager: any user id names nobody.
        fixture.Freeze<Mock<IUserManager>>().Setup(u => u.GetUserById(It.IsAny<Guid>())).Returns((Tesserafin.Data.Entities.User?)null);

        _externalDataManagerMock = fixture.Freeze<Mock<IExternalDataManager>>();
        fixture.Register(() => new Lazy<IExternalDataManager>(() => _externalDataManagerMock.Object));

        // PR107: LibraryManager no longer owns UserRootFolder construction/caching itself - it
        // delegates to IUserRootFolderProvider. Freeze the auto-mock so individual tests can control
        // what GetUserRootFolder() returns (see ValidateTopLibraryFolders_... below, which used to
        // reach into LibraryManager's own private _userRootFolder field via reflection).
        _userRootFolderProviderMock = fixture.Freeze<Mock<IUserRootFolderProvider>>();

        // PR75: LibraryManager no longer owns the item lookup cache directly - it delegates to a
        // single ItemLookupService instance. Build a *real* ItemLookupService (not a mock) from the
        // frozen repository/configuration mocks above, and register that one instance for every type
        // that can request it (the concrete class, the public IItemLookupService, and the internal
        // IItemCacheStore port) so LibraryManager and the tests below share the same live cache -
        // exactly like the single-instance double-singleton wiring in ApplicationHost.
        _itemLookupService = new Tesserafin.Server.Core.Library.ItemLookupService(_itemRepositoryMock.Object, _configurationManagerMock.Object);
        fixture.Register(() => _itemLookupService);
        fixture.Register<IItemLookupService>(() => _itemLookupService);
        fixture.Register<Tesserafin.Server.Core.Library.IItemCacheStore>(() => _itemLookupService);

        // PR77: LibraryManager.GetItemById<T>(id, user) (non-null user) now delegates to
        // IItemAccessService instead of the (removed) user-aware overload on IItemLookupService.
        // Register a *real* ItemAccessService wrapping the same ItemLookupService instance above -
        // an auto-mocked IItemAccessService would silently return null for every visibility check
        // instead of exercising the real ItemIsVisible/IsVisibleStandalone logic.
        _itemAccessService = new Tesserafin.Server.Core.Library.ItemAccessService(
            _itemLookupService,
            new Tesserafin.Server.Core.Library.TagVisibilityService(_itemLookupService, fixture.Freeze<ICollectionFolderResolver>(), fixture.Freeze<IUserRootFolderProvider>(), _itemRepositoryMock.Object, fixture.Freeze<ILocalizationManager>(), fixture.Freeze<IServerConfigurationManager>(), new Tesserafin.Controller.LiveTv.ActiveRecordings(), Mock.Of<ILibraryItemOptions>()));
        fixture.Register<IItemAccessService>(() => _itemAccessService);

        _writes = Tesserafin.Server.Implementations.Tests.Library.LibraryWriteFixture.Register(fixture);

        _libraryManager = fixture.Build<Tesserafin.Server.Core.Library.LibraryManager>().Do(s => s.AddParts(
                fixture.Create<IEnumerable<IResolverIgnoreRule>>(),
                fixture.Create<IEnumerable<IItemResolver>>(),
                fixture.Create<IEnumerable<IBaseItemComparer>>()))
            .Create();
        fixture.Register<ILibraryManager>(() => _libraryManager);
        fixture.Inject<Microsoft.Extensions.Logging.ILogger<Tesserafin.Server.Core.Library.ItemDeletionCommands>>(Microsoft.Extensions.Logging.Abstractions.NullLogger<Tesserafin.Server.Core.Library.ItemDeletionCommands>.Instance);
        _deletions = fixture.Create<Tesserafin.Server.Core.Library.ItemDeletionCommands>();
    }

    private static void SetConfigurationManagerStatic(IServerConfigurationManager configurationManager)
    {
        BaseItem.ConfigurationManager = configurationManager;
    }

    private static void SetLibraryManagerStatic(ILibraryManager libraryManager)
    {
        BaseItem.LibraryManager = libraryManager;
    }

    // ---------------------------------------------------------------
    // 1. Empty guid
    // ---------------------------------------------------------------

    [Fact]
    public void GetItemById_EmptyGuid_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => _libraryManager.GetItemById(Guid.Empty));
    }

    // ---------------------------------------------------------------
    // 2-4. Cache hit / miss / cacheable types
    // ---------------------------------------------------------------

    [Fact]
    public void GetItemById_ItemPreRegisteredInCache_DoesNotCallRepository()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Name = "Folder" };
        _itemLookupService.Register(folder);

        var result = _libraryManager.GetItemById(folder.Id);

        Assert.Same(folder, result);
        _itemRepositoryMock.Verify(r => r.RetrieveItem(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public void GetItemById_CacheMissCacheableItem_CallsRepositoryOnce()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Name = "Folder" };
        _itemRepositoryMock.Setup(r => r.RetrieveItem(folder.Id)).Returns(folder);

        var result = _libraryManager.GetItemById(folder.Id);

        Assert.Same(folder, result);
        _itemRepositoryMock.Verify(r => r.RetrieveItem(folder.Id), Times.Once);
    }

    public static TheoryData<BaseItem> CacheableItems()
    {
        return new TheoryData<BaseItem>
        {
            new Folder { Id = Guid.NewGuid(), Name = "Folder" },
            new Movie { Id = Guid.NewGuid(), Name = "Movie" },
            new LiveTvChannel { Id = Guid.NewGuid(), Name = "Channel" },
            new MusicArtist { Id = Guid.NewGuid(), Name = "Artist" },
        };
    }

    [Theory]
    [MemberData(nameof(CacheableItems))]
    public void GetItemById_CacheableTypeSecondCall_RepositoryCalledOnlyOnce(BaseItem item)
    {
        _itemRepositoryMock.Setup(r => r.RetrieveItem(item.Id)).Returns(item);

        var first = _libraryManager.GetItemById(item.Id);
        var second = _libraryManager.GetItemById(item.Id);

        Assert.Same(item, first);
        Assert.Same(item, second);
        _itemRepositoryMock.Verify(r => r.RetrieveItem(item.Id), Times.Once);
    }

    // ---------------------------------------------------------------
    // 5. Non-cacheable types
    // ---------------------------------------------------------------

    public static TheoryData<BaseItem> NonCacheableItems()
    {
        return new TheoryData<BaseItem>
        {
            new Genre { Id = Guid.NewGuid(), Name = "Genre" }, // IItemByName, not MusicArtist
            new Person { Id = Guid.NewGuid(), Name = "Person" }, // IItemByName, not MusicArtist
            new Audio { Id = Guid.NewGuid(), Name = "Audio" }, // leaf, not Video/LiveTvChannel
            new Book { Id = Guid.NewGuid(), Name = "Book" }, // leaf, not Video/LiveTvChannel
        };
    }

    [Theory]
    [MemberData(nameof(NonCacheableItems))]
    public void GetItemById_NonCacheableType_RepositoryCalledOnEveryLookup(BaseItem item)
    {
        _itemRepositoryMock.Setup(r => r.RetrieveItem(item.Id)).Returns(item);

        _libraryManager.GetItemById(item.Id);
        _libraryManager.GetItemById(item.Id);

        _itemRepositoryMock.Verify(r => r.RetrieveItem(item.Id), Times.Exactly(2));
    }

    // ---------------------------------------------------------------
    // 6. Incompatible generic cast
    // ---------------------------------------------------------------

    [Fact]
    public void GetItemByIdGeneric_IncompatibleType_ReturnsNull()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Movie" };
        _itemRepositoryMock.Setup(r => r.RetrieveItem(movie.Id)).Returns(movie);

        var result = _libraryManager.GetItemById<MusicArtist>(movie.Id);

        Assert.Null(result);
    }

    // ---------------------------------------------------------------
    // 7. User-aware variant - null-user routing only. PR77 moved the actual visibility checks
    // (item-not-found, tag restrictions, UserRootFolder exception) to ItemAccessServiceTests,
    // since they now exercise IItemAccessService rather than LibraryManager/IItemLookupService.
    // This test stays here because it locks down LibraryManager's own routing decision: a null
    // user bypasses IItemAccessService entirely and falls back to a plain, unfiltered lookup
    // (see LibraryManager.GetItemById<T>(Guid, User?) remarks).
    // ---------------------------------------------------------------

    [Fact]
    public void GetItemByIdGeneric_UserNull_AlwaysReturnsItem()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Movie", Tags = new[] { "blocked" } };
        _itemRepositoryMock.Setup(r => r.RetrieveItem(movie.Id)).Returns(movie);

        var result = _libraryManager.GetItemById<Movie>(movie.Id, (User)null!);

        Assert.Same(movie, result);
    }

    // ---------------------------------------------------------------
    // 9. RegisterItem replaces the cached instance
    // ---------------------------------------------------------------

    [Fact]
    public void GetItemByIdGeneric_UserId_EmptyValidatesNothing_AndAnIdThatNamesNoUserSeesNothing()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Name = "Folder" };
        _itemLookupService.Register(folder);

        // An API key and a system caller carry no user id.
        Assert.Same(folder, _libraryManager.GetItemById<Folder>(folder.Id, Guid.Empty));

        // SECURITY-MEDIA-R1: a deleted user's id used to fall through to "no user", and see everything.
        Assert.Null(_libraryManager.GetItemById<Folder>(folder.Id, Guid.NewGuid()));
    }

    [Fact]
    public void Register_NewInstanceSameId_ReplacesCachedInstance()
    {
        var id = Guid.NewGuid();
        var original = new Folder { Id = id, Name = "Original" };
        var replacement = new Folder { Id = id, Name = "Replacement" };

        _itemLookupService.Register(original);
        Assert.Same(original, _libraryManager.GetItemById(id));

        _itemLookupService.Register(replacement);

        var result = _libraryManager.GetItemById(id);
        Assert.Same(replacement, result);
        Assert.NotSame(original, result);
        _itemRepositoryMock.Verify(r => r.RetrieveItem(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public void CreateItems_CacheableItem_RegistersInCache()
    {
        var artist = new MusicArtist { Id = Guid.NewGuid(), Name = "Artist" };

        _writes.CreateItems(new List<BaseItem> { artist }, null, System.Threading.CancellationToken.None);

        var result = _libraryManager.GetItemById(artist.Id);

        Assert.Same(artist, result);
        _persistenceServiceMock.Verify(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<System.Threading.CancellationToken>()), Times.Once);
        _itemRepositoryMock.Verify(r => r.RetrieveItem(It.IsAny<Guid>()), Times.Never);
    }

    // ---------------------------------------------------------------
    // 10. DeleteItem invalidates the cache
    // ---------------------------------------------------------------

    [Fact]
    public async Task DeleteItem_CacheableItem_InvalidatesCache()
    {
        SetConfigurationManagerStatic(_configurationManagerMock.Object);

        // LiveTvChannel keeps DeleteItem's side paths inert: SourceType is LiveTV (not Channel, so
        // no ChannelManager static needed), IsFolder is false (no recursive children), and with
        // Path left null, IsFileProtocol/IsInternalItem are both false so no filesystem deletion is
        // attempted. This lets us exercise the cache-invalidation tail of DeleteItem without pulling
        // in the Video alternate-version machinery or real file I/O.
        var channel = new LiveTvChannel { Id = Guid.NewGuid(), Name = "Channel" };
        _itemLookupService.Register(channel);
        Assert.Same(channel, _libraryManager.GetItemById(channel.Id));

        await _deletions.DeleteAsync(channel, new DeleteOptions { DeleteFileLocation = false }, null, TestContext.Current.CancellationToken);

        _itemRepositoryMock.Setup(r => r.RetrieveItem(channel.Id)).Returns((BaseItem)null!);
        var result = _libraryManager.GetItemById(channel.Id);

        Assert.Null(result);
        _itemRepositoryMock.Verify(r => r.RetrieveItem(channel.Id), Times.Once);
        _persistenceServiceMock.Verify(p => p.DeleteItem(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(channel.Id))), Times.Once);
    }

    // ---------------------------------------------------------------
    // 11. DeleteItemsUnsafeFast invalidates the cache
    // ---------------------------------------------------------------

    [Fact]
    public async Task DeleteUnreferenced_CacheableItem_InvalidatesCache()
    {
        SetConfigurationManagerStatic(_configurationManagerMock.Object);

        var channel = new LiveTvChannel { Id = Guid.NewGuid(), Name = "Channel" };
        _itemLookupService.Register(channel);
        Assert.Same(channel, _libraryManager.GetItemById(channel.Id));

        await _deletions.DeleteUnreferencedAsync(new List<BaseItem> { channel }, deleteSourceFiles: false, TestContext.Current.CancellationToken);

        _itemRepositoryMock.Setup(r => r.RetrieveItem(channel.Id)).Returns((BaseItem)null!);
        var result = _libraryManager.GetItemById(channel.Id);

        Assert.Null(result);
        _itemRepositoryMock.Verify(r => r.RetrieveItem(channel.Id), Times.Once);
        _persistenceServiceMock.Verify(p => p.DeleteItem(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(channel.Id))), Times.Once);
    }

    // ---------------------------------------------------------------
    // 12. IItemLookupService (PR71)
    // ---------------------------------------------------------------

    [Fact]
    public void LibraryManager_IsAssignableToIItemLookupService()
    {
        Assert.IsAssignableFrom<IItemLookupService>(_libraryManager);
    }

    [Fact]
    public void GetItemById_ViaIItemLookupServiceReference_SameCacheBehaviorAndInstanceAsILibraryManager()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Name = "Folder" };
        _itemLookupService.Register(folder);

        IItemLookupService lookupService = _libraryManager;

        var viaInterface = lookupService.GetItemById(folder.Id);
        var viaConcrete = _libraryManager.GetItemById(folder.Id);

        Assert.Same(folder, viaInterface);
        Assert.Same(viaConcrete, viaInterface);
        _itemRepositoryMock.Verify(r => r.RetrieveItem(It.IsAny<Guid>()), Times.Never);
    }

    // Note (PR77): GetItemByIdGeneric_ViaIItemLookupServiceReference_InvisibleViaBlockedTag_ReturnsNull
    // used to live here, exercising the user-aware IItemLookupService.GetItemById<T>(Guid, User)
    // overload across the interface boundary. That overload was removed from IItemLookupService in
    // PR77 (moved to IItemAccessService) - the equivalent coverage now lives in
    // ItemAccessServiceTests.GetVisibleItemById_InvisibleViaBlockedTag_ReturnsNull.

    // ---------------------------------------------------------------
    // 13. DeleteItem parent resolution uses the injected lookup (PR73)
    // ---------------------------------------------------------------

    [Fact]
    public async Task DeleteItem_ParentResolution_UsesInstanceLookupNotStaticLibraryManager()
    {
        SetConfigurationManagerStatic(_configurationManagerMock.Object);

        // Strict mock with zero setups: DeleteItem's parent resolution (item.GetOwner(this)
        // ?? item.GetParent(this), PR73) must resolve entirely through the LibraryManager
        // instance passed as "this". If it fell back to the static BaseItem.LibraryManager
        // (pre-PR73 behavior), any call on this mock throws and the test fails.
        SetLibraryManagerStatic(new Mock<ILibraryManager>(MockBehavior.Strict).Object);

        var parentFolder = new Folder { Id = Guid.NewGuid(), Name = "Parent" };
        _itemLookupService.Register(parentFolder);

        // LiveTvChannel keeps DeleteItem's side paths inert (see DeleteItem_CacheableItem_
        // InvalidatesCache above): SourceType is LiveTV, IsFolder is false, and Path is null,
        // so only the cache-invalidation / event-notification tail runs.
        var channel = new LiveTvChannel { Id = Guid.NewGuid(), Name = "Channel", ParentId = parentFolder.Id };
        _itemLookupService.Register(channel);

        ItemChangeEventArgs? raisedArgs = null;
        _changes.ItemRemoved += (_, args) => raisedArgs = args;

        await _deletions.DeleteAsync(channel, new DeleteOptions { DeleteFileLocation = false }, TestContext.Current.CancellationToken);

        Assert.NotNull(raisedArgs);
        Assert.Same(channel, raisedArgs.Item);
        Assert.Same(parentFolder, raisedArgs.Parent);
    }

    // ---------------------------------------------------------------
    // 14. ItemLookupService extraction (PR75)
    // ---------------------------------------------------------------

    [Fact]
    public void ItemLookupService_Standalone_CacheMissThenHit_ReadsThroughOnceThenServesFromCache()
    {
        // Exercised directly against ItemLookupService, with no LibraryManager involved, to prove
        // the extracted service is independently read-through/cache-capable rather than relying on
        // LibraryManager to drive it.
        var folder = new Folder { Id = Guid.NewGuid(), Name = "Folder" };
        _itemRepositoryMock.Setup(r => r.RetrieveItem(folder.Id)).Returns(folder);

        var first = _itemLookupService.GetItemById(folder.Id);
        var second = _itemLookupService.GetItemById(folder.Id);

        Assert.Same(folder, first);
        Assert.Same(folder, second);
        _itemRepositoryMock.Verify(r => r.RetrieveItem(folder.Id), Times.Once);
    }

    [Fact]
    public void GetItemById_LibraryManagerAndItemLookupService_ReturnSameInstanceFromSameCache()
    {
        // LibraryManager.GetItemById must delegate to the exact same ItemLookupService instance
        // used elsewhere (DI wires a single ItemLookupService as both the concrete service and the
        // IItemLookupService/IItemCacheStore ports) - not a second, independently-caching instance.
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Movie" };
        _itemRepositoryMock.Setup(r => r.RetrieveItem(movie.Id)).Returns(movie);

        var viaLibraryManager = _libraryManager.GetItemById(movie.Id);
        var viaItemLookupService = _itemLookupService.GetItemById(movie.Id);

        Assert.Same(movie, viaLibraryManager);
        Assert.Same(viaLibraryManager, viaItemLookupService);
        _itemRepositoryMock.Verify(r => r.RetrieveItem(movie.Id), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void FindByPath_UsesTheNewestMatchAndTheRequestedFolderFilter(bool? isFolder)
    {
        BaseItem newest = isFolder == true ? new Folder { Path = "/library/folder" } : new Movie { Path = "/library/movie.mkv" };
        _itemRepositoryMock.Setup(r => r.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) =>
        {
            Assert.Equal(newest.Path, query.Path);
            Assert.Equal(isFolder, query.IsFolder);
            Assert.Equal(1, query.Limit);
            Assert.Equal((ItemSortBy.DateCreated, SortOrder.Descending), Assert.Single(query.OrderBy));
            return new BaseItem[] { newest };
        });

        IItemPathLookupService lookup = _itemLookupService;
        Assert.Same(newest, lookup.FindByPath(newest.Path, isFolder));
    }

    // ---------------------------------------------------------------
    // 15. PR76: BaseItem.GetParent()/GetOwner() (static LibraryManager) and their
    // GetParent(lookup)/GetOwner(lookup) counterparts (PR72) resolve through the same cache.
    // ---------------------------------------------------------------

    [Fact]
    public void GetParent_StaticAndLookupOverload_ResolveSameInstanceFromSameCache()
    {
        // Closing-audit check (PR76 point 5): the historical BaseItem.GetParent() (static
        // BaseItem.LibraryManager, itself delegating to _itemLookupService per LibraryManager.cs:1599)
        // and the PR72 BaseItem.GetParent(IItemLookupService) overload must resolve the exact same
        // cached instance, not two independently-caching lookups.
        SetLibraryManagerStatic(_libraryManager);

        var parent = new Folder { Id = Guid.NewGuid(), Name = "Parent" };
        _itemLookupService.Register(parent);
        var child = new Movie { Id = Guid.NewGuid(), Name = "Child", ParentId = parent.Id };

        var viaStatic = child.GetParent();
        var viaLookup = child.GetParent(_itemLookupService);

        Assert.Same(parent, viaStatic);
        Assert.Same(viaStatic, viaLookup);
        _itemRepositoryMock.Verify(r => r.RetrieveItem(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public void GetOwner_StaticAndLookupOverload_ResolveSameInstanceFromSameCache()
    {
        // Closing-audit check (PR76 point 6): same as above, for GetOwner()/GetOwner(lookup).
        SetLibraryManagerStatic(_libraryManager);

        var owner = new Movie { Id = Guid.NewGuid(), Name = "Owner" };
        _itemLookupService.Register(owner);
        var item = new Movie { Id = Guid.NewGuid(), Name = "Item", OwnerId = owner.Id };

        var viaStatic = item.GetOwner();
        var viaLookup = item.GetOwner(_itemLookupService);

        Assert.Same(owner, viaStatic);
        Assert.Same(viaStatic, viaLookup);
        _itemRepositoryMock.Verify(r => r.RetrieveItem(It.IsAny<Guid>()), Times.Never);
    }

    // ---------------------------------------------------------------
    // 16. PR76: hardening guards - DI wiring and concrete-type visibility.
    // ---------------------------------------------------------------

    [Fact]
    public void DiWiring_ApplicationHostStyleRegistration_BothPortsResolveSameSingleton()
    {
        // Reproduces the exact ApplicationHost wiring (ApplicationHost.cs, PR75/PR76): one
        // concrete ItemLookupService singleton exposed under both the public read port
        // (IItemLookupService) and the internal lifecycle port (IItemCacheStore). If someone
        // ever splits these mappings into two instances, reads and invalidation would silently
        // operate on two different caches.
        var services = new ServiceCollection();
        services.AddSingleton(_itemRepositoryMock.Object);
        services.AddSingleton(_configurationManagerMock.Object);
        services.AddSingleton<Tesserafin.Server.Core.Library.ItemLookupService>();
        services.AddSingleton<IItemLookupService>(sp => sp.GetRequiredService<Tesserafin.Server.Core.Library.ItemLookupService>());
        services.AddSingleton<Tesserafin.Server.Core.Library.IItemCacheStore>(sp => sp.GetRequiredService<Tesserafin.Server.Core.Library.ItemLookupService>());

        using var provider = services.BuildServiceProvider();

        var lookup = provider.GetRequiredService<IItemLookupService>();
        var cacheStore = provider.GetRequiredService<Tesserafin.Server.Core.Library.IItemCacheStore>();

        Assert.Same(lookup, cacheStore);
    }

    [Fact]
    public void ItemLookupService_ConcreteType_StaysInternalSealed()
    {
        // PR76 hardening guard: the concrete cache owner must never become public again -
        // consumers outside Tesserafin.Server.Core may only see IItemLookupService (reads) or,
        // within the assembly, IItemCacheStore (lifecycle).
        var concreteType = typeof(Tesserafin.Server.Core.Library.ItemLookupService);

        Assert.False(concreteType.IsVisible);
        Assert.False(concreteType.IsPublic);
        Assert.True(concreteType.IsSealed);
    }

    // ---------------------------------------------------------------
    // 17. PR76: ValidateTopLibraryFolders - a CollectionFolder whose directory disappeared is
    // deleted from the database AND invalidated from the lookup cache (fix discovered in PR75).
    // ---------------------------------------------------------------

    [Fact]
    public async Task ScanTraversal_MissingCollectionFolderDirectory_InvalidatesCacheEntry()
    {
        // The heavy machinery (root construction, metadata refresh, children validation) is
        // neutralized: stub root folders are supplied through their dedicated provider ports,
        // their ValidateChildrenInternal/Children members are overridden to no-ops, and the
        // metadata refresh uses a provider mock supplied through the validation port.
        // What remains under test is exactly the PR75 fix: the missing-directory cleanup loop in
        // ValidateTopLibraryFolders must invalidate the cache entry after deleting from the DB.
        SetLibraryManagerStatic(_libraryManager);
        SetConfigurationManagerStatic(_configurationManagerMock.Object);
        BaseItem.Logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<BaseItem>();

        var missing = new CollectionFolder
        {
            Id = Guid.NewGuid(),
            Name = "Vanished library",
            Path = "/reefin-test-nonexistent/" + Guid.NewGuid()
        };

        _itemLookupService.Register(missing);
        Assert.Same(missing, _libraryManager.GetItemById(missing.Id));
        _itemRepositoryMock.Verify(r => r.RetrieveItem(missing.Id), Times.Never);

        // PR107: UserRootFolder construction/caching moved off LibraryManager to
        // IUserRootFolderProvider - stub the injected mock's GetUserRootFolder() instead of poking a
        // (now-removed) private LibraryManager field.
        _userRootFolderProviderMock
            .Setup(p => p.GetUserRootFolder())
            .Returns(new StubUserRootFolder { StubChildren = [missing] });

        var roots = Mock.Of<ILibraryRootFolderProvider>(p => p.GetRootFolder() == new StubAggregateFolder());
        var providers = Mock.Of<IProviderManager>();
        var validation = Mock.Of<IFolderValidationServices>(services => services.Providers == providers);
        var traversal = new Tesserafin.Server.Core.Library.LibraryScanTraversal(_writes, Mock.Of<IItemImageService>(), _deletions, validation, roots, _userRootFolderProviderMock.Object, Mock.Of<Tesserafin.Model.IO.IFileSystem>(), _persistenceServiceMock.Object, _itemLookupService, new Tesserafin.Server.Core.Library.DotIgnoreIgnoreRule());
        await traversal.RefreshTopFoldersAsync(TestContext.Current.CancellationToken, false);

        // Deleted from the database...
        _persistenceServiceMock.Verify(
            p => p.DeleteItem(It.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0].Equals(missing.Id))),
            Times.Once);

        // ...and invalidated from the cache: the next lookup is a read-through miss.
        Assert.Null(_libraryManager.GetItemById(missing.Id));
        _itemRepositoryMock.Verify(r => r.RetrieveItem(missing.Id), Times.Once);
    }

    private sealed class StubAggregateFolder : AggregateFolder
    {
        public override IEnumerable<BaseItem> Children
        {
            get => [];
            set { }
        }

        protected override System.Threading.Tasks.Task ValidateChildrenInternal(IFolderValidationServices services, IProgress<double> progress, bool recursive, bool refreshChildMetadata, bool allowRemoveRoot, MetadataRefreshOptions refreshOptions, IDirectoryService directoryService, CancellationToken cancellationToken)
            => System.Threading.Tasks.Task.CompletedTask;
    }

    private sealed class StubUserRootFolder : UserRootFolder
    {
        public IReadOnlyList<BaseItem> StubChildren { get; set; } = [];

        public override IEnumerable<BaseItem> GetCatalogueChildren(IItemRepository repository, IItemLookupService lookup)
            => StubChildren;

        protected override System.Threading.Tasks.Task ValidateChildrenInternal(IFolderValidationServices services, IProgress<double> progress, bool recursive, bool refreshChildMetadata, bool allowRemoveRoot, MetadataRefreshOptions refreshOptions, IDirectoryService directoryService, CancellationToken cancellationToken)
            => System.Threading.Tasks.Task.CompletedTask;
    }
}
