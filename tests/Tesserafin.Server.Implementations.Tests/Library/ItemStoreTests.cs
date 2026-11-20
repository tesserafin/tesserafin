using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Model.Configuration;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

/// <summary>
/// Standalone tests for <see cref="ItemStore"/> (PR106a), exercised in isolation from
/// <see cref="Tesserafin.Server.Core.Library.LibraryManager"/> against a mocked
/// <see cref="IItemPersistenceService"/>, a hand-written <see cref="FakeItemCacheStore"/> test
/// double (Moq cannot proxy the internal <see cref="IItemCacheStore"/> port across the assembly
/// boundary without also granting <c>InternalsVisibleTo</c> to Castle's dynamic-proxy assembly,
/// which this repo does not do - a plain implementing class works fine instead, since
/// <c>InternalsVisibleTo("Tesserafin.Server.Implementations.Tests")</c> already lets this project
/// implement the interface directly), and a mocked <see cref="IServerConfigurationManager"/>.
/// Cross-checks against the real <c>LibraryManager</c> (parity, shared <c>ItemAdded</c> publication)
/// live in <c>LibraryManager/LibraryManagerItemStoreTests.cs</c>, since those need both producers using the same notification owner.
/// </summary>
public class ItemStoreTests
{
    private readonly Mock<IItemPersistenceService> _persistenceServiceMock;
    private readonly FakeItemCacheStore _itemCacheStore;
    private readonly Mock<IServerConfigurationManager> _configurationManagerMock;
    private readonly ItemStore _itemStore;
    private readonly ItemChangeEvents _changes = new(NullLogger<ItemChangeEvents>.Instance);

    public ItemStoreTests()
    {
        _configurationManagerMock = new Mock<IServerConfigurationManager>();
        _configurationManagerMock.Setup(c => c.ApplicationPaths.ProgramDataPath).Returns("/data");
        _configurationManagerMock.Setup(c => c.Configuration).Returns(new ServerConfiguration());

        _persistenceServiceMock = new Mock<IItemPersistenceService>();
        _itemCacheStore = new FakeItemCacheStore();

        _itemStore = new ItemStore(
            _persistenceServiceMock.Object,
            _itemCacheStore,
            _configurationManagerMock.Object,
            _changes,
            new ActiveRecordings());
    }

    // ---------------------------------------------------------------
    // GetNewItemId - basic determinism (cross-checked against LibraryManager for parity in
    // LibraryManagerItemStoreTests, since that comparison needs a real LibraryManager instance
    // sharing the same IServerConfigurationManager).
    // ---------------------------------------------------------------

    [Fact]
    public void GetNewItemId_SameKeyAndType_IsDeterministic()
    {
        var first = _itemStore.GetNewItemId("/data/views/movies_namedview_Movies", typeof(UserView));
        var second = _itemStore.GetNewItemId("/data/views/movies_namedview_Movies", typeof(UserView));

        Assert.Equal(first, second);
    }

    [Fact]
    public void GetNewItemId_DifferentType_ProducesDifferentId()
    {
        var asUserView = _itemStore.GetNewItemId("same-key", typeof(UserView));
        var asFolder = _itemStore.GetNewItemId("same-key", typeof(Folder));

        Assert.NotEqual(asUserView, asFolder);
    }

    [Fact]
    public void GetNewItemId_EmptyKey_Throws()
    {
        Assert.Throws<ArgumentException>(() => _itemStore.GetNewItemId(string.Empty, typeof(UserView)));
    }

    [Fact]
    public void GetNewItemId_NullType_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _itemStore.GetNewItemId("key", null!));
    }

    // ---------------------------------------------------------------
    // CreateItem - happy path: save then register, in order.
    // ---------------------------------------------------------------

    [Fact]
    public void CreateItem_SaveAndRegisterSucceed_CallsSaveThenRegister()
    {
        var item = new UserView { Id = Guid.NewGuid(), Name = "Movies" };
        var callOrder = new List<string>();
        _persistenceServiceMock
            .Setup(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("save"));
        _itemCacheStore.OnRegister = _ => callOrder.Add("register");

        _itemStore.CreateItem(item, null);

        Assert.Equal(new[] { "save", "register" }, callOrder);
        _persistenceServiceMock.Verify(p => p.SaveItems(It.Is<IReadOnlyList<BaseItem>>(items => items.Count == 1 && items[0] == item), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single(_itemCacheStore.Registered);
        Assert.Same(item, _itemCacheStore.Registered[0]);
    }

    [Fact]
    public void CreateItem_NullItem_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _itemStore.CreateItem(null!, null));
    }

    [Fact]
    public void CreateItem_FolderParent_ResetsChildrenAndUserDataOnParent()
    {
        // Mirrors LibraryManager.CreateItems' "if (parent is Folder folder) { folder.Children = null;
        // folder.UserData = null; }" tail. Never exercised at the real GetNamedView/GetShadowView
        // call sites (parent is always null there), but characterized here since CreateItem's public
        // signature accepts a non-null parent.
        var parentFolder = new Folder { Id = Guid.NewGuid(), Name = "Parent", Children = new List<BaseItem> { new Folder() } };
        var item = new UserView { Id = Guid.NewGuid(), Name = "Movies" };

        _itemStore.CreateItem(item, parentFolder);

        // The public getter would reload the just-invalidated cache from the catalogue.
        var childrenField = typeof(Folder).GetField("_children", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.Null(childrenField.GetValue(parentFolder));
        Assert.Null(parentFolder.UserData);
    }

    // ---------------------------------------------------------------
    // CreateItem - characterized partial-failure semantics (no rollback), matching the current,
    // non-transactional behavior of LibraryManager.CreateItems (LibraryManager.cs:2290-2295).
    // ---------------------------------------------------------------

    [Fact]
    public void CreateItem_SaveItemsThrows_NothingRegisteredAndExceptionPropagates()
    {
        var item = new UserView { Id = Guid.NewGuid(), Name = "Movies" };
        var notifications = 0;
        _changes.ItemAdded += (_, _) => notifications++;
        var saveException = new InvalidOperationException("save failed");
        _persistenceServiceMock
            .Setup(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()))
            .Throws(saveException);

        var thrown = Assert.Throws<InvalidOperationException>(() => _itemStore.CreateItem(item, null));

        Assert.Same(saveException, thrown);
        Assert.Equal(0, notifications);
        Assert.Empty(_itemCacheStore.Registered);
    }

    [Fact]
    public void CreateItem_SaveSucceedsButRegisterThrows_ItemPersistedNotRegistered_ExceptionPropagates()
    {
        // Characterizes the accepted, non-improved-on gap: a save that succeeds followed by a
        // register that fails leaves the item durably persisted (SaveItems was already called and
        // did not throw) but absent from the cache, with the register exception propagating
        // unchanged - exactly LibraryManager.CreateItems' current behavior (no compensating delete).
        var item = new UserView { Id = Guid.NewGuid(), Name = "Movies" };
        var notifications = 0;
        _changes.ItemAdded += (_, _) => notifications++;
        var registerException = new InvalidOperationException("register failed");
        _itemCacheStore.ThrowOnRegister = registerException;

        var thrown = Assert.Throws<InvalidOperationException>(() => _itemStore.CreateItem(item, null));

        Assert.Same(registerException, thrown);
        Assert.Equal(0, notifications);
        _persistenceServiceMock.Verify(p => p.SaveItems(It.Is<IReadOnlyList<BaseItem>>(items => items.Count == 1 && items[0] == item), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(_itemCacheStore.Registered);
    }

    // ---------------------------------------------------------------
    // ItemAdded - SourceType.Library filter, same as LibraryManager.CreateItems (LibraryManager.cs:2303-2328).
    // ---------------------------------------------------------------

    [Fact]
    public void CreateItem_LibrarySourceTypeItem_RaisesItemAddedOnce()
    {
        var item = new UserView { Id = Guid.NewGuid(), Name = "Movies" };
        Assert.Equal(SourceType.Library, item.SourceType);

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
        Assert.Null(raisedArgs.Parent);
    }

    [Fact]
    public void CreateItem_NonLibrarySourceTypeItem_DoesNotRaiseItemAdded()
    {
        var item = new Video { Id = Guid.NewGuid(), Name = "Channel-sourced", ChannelId = Guid.NewGuid() };
        Assert.Equal(SourceType.Channel, item.SourceType);

        var raised = false;
        _changes.ItemAdded += (_, _) => raised = true;

        _itemStore.CreateItem(item, null);

        Assert.False(raised);
    }

    [Fact]
    public void CreateItem_NoItemAddedSubscribers_DoesNotThrow()
    {
        var item = new UserView { Id = Guid.NewGuid(), Name = "Movies" };

        var exception = Record.Exception(() => _itemStore.CreateItem(item, null));

        Assert.Null(exception);
    }

    [Fact]
    public void CreateItem_ItemAddedHandlerThrows_ExceptionIsSwallowedAndLogged()
    {
        // Mirrors CreateItems' own try/catch-and-log around each ItemAdded invocation
        // (LibraryManager.cs:2313-2326): a misbehaving subscriber must not prevent CreateItem from
        // returning normally, nor mask that save+register already succeeded.
        var item = new UserView { Id = Guid.NewGuid(), Name = "Movies" };
        _changes.ItemAdded += (_, _) => throw new InvalidOperationException("subscriber exploded");

        var exception = Record.Exception(() => _itemStore.CreateItem(item, null));

        Assert.Null(exception);
        Assert.Single(_itemCacheStore.Registered);
    }

    // ---------------------------------------------------------------
    // RegisterItem - pure pass-through.
    // ---------------------------------------------------------------

    [Fact]
    public void RegisterItem_DelegatesToItemCacheStore()
    {
        var item = new Folder { Id = Guid.NewGuid(), Name = "Folder" };

        _itemStore.RegisterItem(item);

        Assert.Single(_itemCacheStore.Registered);
        Assert.Same(item, _itemCacheStore.Registered[0]);
    }

    [Fact]
    public void RegisterItem_NullItem_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _itemStore.RegisterItem(null!));
    }

    // ---------------------------------------------------------------
    // DI wiring (RFC I1): IItemStore must resolve to the autonomous ItemStore class, not a factory
    // casting another service - mirrors the DiWiring_ApplicationHostStyleRegistration_... pattern
    // used for IItemLookupService/IItemCacheStore (LibraryManagerItemLookupTests) and IItemAccessService
    // (ItemAccessServiceTests).
    // ---------------------------------------------------------------

    [Fact]
    public void DiWiring_ApplicationHostStyleRegistration_ItemStoreResolvesToAutonomousImplementation()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_persistenceServiceMock.Object);
        services.AddSingleton<IItemCacheStore>(_itemCacheStore);
        services.AddSingleton(_configurationManagerMock.Object);
        services.AddSingleton<IItemChangePublisher>(_changes);
        services.AddSingleton<ActiveRecordings>();
        services.AddSingleton<IItemStore, ItemStore>();

        using var provider = services.BuildServiceProvider();

        var resolved = provider.GetRequiredService<IItemStore>();

        Assert.IsType<ItemStore>(resolved);
    }

    [Fact]
    public void DiWiring_ApplicationHostStyleRegistration_ItemStoreIsSingleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_persistenceServiceMock.Object);
        services.AddSingleton<IItemCacheStore>(_itemCacheStore);
        services.AddSingleton(_configurationManagerMock.Object);
        services.AddSingleton<IItemChangePublisher>(_changes);
        services.AddSingleton<ActiveRecordings>();
        services.AddSingleton<IItemStore, ItemStore>();

        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IItemStore>();
        var second = provider.GetRequiredService<IItemStore>();

        Assert.Same(first, second);
    }

    /// <summary>
    /// Minimal hand-written <see cref="IItemCacheStore"/> test double. Moq cannot proxy this
    /// internal interface from the test project without also granting <c>InternalsVisibleTo</c> to
    /// Castle's dynamic-proxy assembly (not configured in this repo); implementing it directly is
    /// straightforward since <c>InternalsVisibleTo("Tesserafin.Server.Implementations.Tests")</c> already
    /// makes the interface visible here.
    /// </summary>
    private sealed class FakeItemCacheStore : IItemCacheStore
    {
        public List<BaseItem> Registered { get; } = new();

        public Exception? ThrowOnRegister { get; set; }

        public Action<BaseItem>? OnRegister { get; set; }

        public void Register(BaseItem item)
        {
            if (ThrowOnRegister is not null)
            {
                throw ThrowOnRegister;
            }

            Registered.Add(item);
            OnRegister?.Invoke(item);
        }

        public void Remove(Guid id)
        {
            Registered.RemoveAll(i => i.Id.Equals(id));
        }

        public void RemoveRange(IEnumerable<Guid> ids)
        {
            foreach (var id in ids)
            {
                Remove(id);
            }
        }
    }
}
