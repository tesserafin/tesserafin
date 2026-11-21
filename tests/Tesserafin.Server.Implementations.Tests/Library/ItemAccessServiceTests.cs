using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Data;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Globalization;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

/// <summary>
/// Tests for <see cref="ItemAccessService"/>, the user-visibility boundary extracted in PR77 from
/// the (now removed) user-aware overload of <c>ItemLookupService.GetItemById&lt;T&gt;(Guid, User)</c>.
/// Most of these tests are moved, unchanged in behavior, from
/// <c>LibraryManagerItemLookupTests</c> (formerly section 7-8, "user-aware variant" /
/// "UserRootFolder visibility exception") - only the entry point changed, from
/// <c>LibraryManager.GetItemById&lt;T&gt;(Guid, User)</c> / the removed
/// <c>IItemLookupService.GetItemById&lt;T&gt;(Guid, User)</c> overload to
/// <see cref="IItemAccessService.GetVisibleItemById{T}"/>.
/// </summary>
[Collection(Tesserafin.Server.Implementations.Tests.Library.LibraryManager.LibraryManagerStaticStateFixture.Name)]
public class ItemAccessServiceTests
{
    private readonly Tesserafin.Server.Core.Library.ItemLookupService _itemLookupService;
    private readonly ItemAccessService _itemAccessService;
    private readonly Mock<IItemRepository> _itemRepositoryMock;
    private readonly Mock<IServerConfigurationManager> _configurationManagerMock;

    public ItemAccessServiceTests()
    {
        _configurationManagerMock = new Mock<IServerConfigurationManager>();
        _configurationManagerMock.Setup(c => c.Configuration).Returns(new ServerConfiguration());

        _itemRepositoryMock = new Mock<IItemRepository>();
        _itemRepositoryMock.Setup(i => i.RetrieveItem(It.IsAny<Guid>())).Returns<BaseItem>(null);

        _itemLookupService = new Tesserafin.Server.Core.Library.ItemLookupService(_itemRepositoryMock.Object, _configurationManagerMock.Object);
        // No library membership: these items live outside every library.
        var noLibraries = Mock.Of<ICollectionFolderResolver>(r => r.GetCollectionFolders(It.IsAny<BaseItem>()) == new List<Folder>());
        _itemAccessService = new ItemAccessService(_itemLookupService, new TagVisibilityService(_itemLookupService, noLibraries, Mock.Of<IUserRootFolderProvider>(), _itemRepositoryMock.Object, Mock.Of<ILocalizationManager>(), _configurationManagerMock.Object, new Tesserafin.Controller.LiveTv.ActiveRecordings(), Mock.Of<ILibraryItemOptions>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<Tesserafin.Server.Core.Library.TagVisibilityService>.Instance));
    }

    private static void SetLibraryManagerStatic(ILibraryManager libraryManager)
    {
        BaseItem.LibraryManager = libraryManager;
    }

    [Fact]
    public void GetVisibleItemById_ItemNotFound_ReturnsNull()
    {
        var id = Guid.NewGuid();
        _itemRepositoryMock.Setup(r => r.RetrieveItem(id)).Returns((BaseItem)null!);

        var user = new User("test-user", "provider", "provider");
        var result = _itemAccessService.GetVisibleItemById<Movie>(id, user);

        Assert.Null(result);
    }

    [Fact]
    public void GetVisibleItemById_VisibleItemNoTagRestrictions_ReturnsItem()
    {
        // Audio (not Video) to avoid Video.SourceType touching the unset Video.RecordingsManager
        // static via IsActiveRecording(). No Path set -> IsVisibleStandaloneInternal short-circuits
        // to visible before touching collection folders / LibraryManager statics (topParent.Path is
        // empty -> return true).
        var audio = new Audio { Id = Guid.NewGuid(), Name = "Track" };
        _itemRepositoryMock.Setup(r => r.RetrieveItem(audio.Id)).Returns(audio);

        var user = new User("test-user", "provider", "provider");

        var result = _itemAccessService.GetVisibleItemById<Audio>(audio.Id, user);

        Assert.Same(audio, result);
    }

    [Fact]
    public void GetVisibleItemById_InvisibleViaBlockedTag_ReturnsNull()
    {
        // The blocked-tag path used to read the item's libraries through the static
        // BaseItem.LibraryManager (GetInheritedTags). Since DEBT-SERVER-3 the tags come from
        // ITagVisibilityService, so a strict static that throws on any call proves it is not read.
        SetLibraryManagerStatic(new Mock<ILibraryManager>(MockBehavior.Strict).Object);

        var audio = new Audio { Id = Guid.NewGuid(), Name = "Track", Tags = new[] { "blocked" } };
        _itemRepositoryMock.Setup(r => r.RetrieveItem(audio.Id)).Returns(audio);

        var user = new User("test-user", "provider", "provider");
        user.SetPreference(PreferenceKind.BlockedTags, new[] { "blocked" });

        var result = _itemAccessService.GetVisibleItemById<Audio>(audio.Id, user);

        Assert.Null(result);
    }

    [Fact]
    public void GetVisibleItemById_UserRootFolderWithUser_AlwaysReturnsItem()
    {
        // ItemIsVisible short-circuits on "item is UserRootFolder" (OR), so IsVisibleStandalone is
        // never evaluated - the blocked tag below would normally hide the item but never gets checked.
        var rootFolder = new UserRootFolder { Id = Guid.NewGuid(), Name = "root", Tags = new[] { "blocked" } };
        _itemRepositoryMock.Setup(r => r.RetrieveItem(rootFolder.Id)).Returns(rootFolder);

        var user = new User("test-user", "provider", "provider");
        user.SetPreference(PreferenceKind.BlockedTags, new[] { "blocked" });

        var result = _itemAccessService.GetVisibleItemById<UserRootFolder>(rootFolder.Id, user);

        Assert.Same(rootFolder, result);
    }

    [Fact]
    public void DiWiring_ApplicationHostStyleRegistration_ItemAccessServiceComposesSameLookupSingleton()
    {
        // Reproduces the ApplicationHost wiring (PR77): IItemAccessService is registered on top of
        // the single ItemLookupService singleton also exposed as IItemLookupService/IItemCacheStore.
        // Proves it behaviorally, not just by resolving types: register an item through the
        // resolved IItemCacheStore, then confirm the resolved IItemAccessService sees that exact
        // instance with zero repository reads - i.e. it is reading through the *same* cache, not an
        // independently-caching lookup.
        var services = new ServiceCollection();
        services.AddSingleton(_itemRepositoryMock.Object);
        services.AddSingleton(_configurationManagerMock.Object);
        services.AddSingleton<Tesserafin.Server.Core.Library.ItemLookupService>();
        services.AddSingleton<IItemLookupService>(sp => sp.GetRequiredService<Tesserafin.Server.Core.Library.ItemLookupService>());
        services.AddSingleton<Tesserafin.Server.Core.Library.IItemCacheStore>(sp => sp.GetRequiredService<Tesserafin.Server.Core.Library.ItemLookupService>());
        services.AddSingleton(Mock.Of<ICollectionFolderResolver>());
        services.AddSingleton(Mock.Of<IUserRootFolderProvider>());
        services.AddSingleton(Mock.Of<ILocalizationManager>());
        services.AddSingleton(Mock.Of<ILibraryItemOptions>());
        services.AddSingleton<Tesserafin.Controller.LiveTv.ActiveRecordings>();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        services.AddSingleton<IStandaloneVisibilityService, TagVisibilityService>();
        services.AddSingleton<IItemAccessService, ItemAccessService>();

        using var provider = services.BuildServiceProvider();

        var cacheStore = provider.GetRequiredService<Tesserafin.Server.Core.Library.IItemCacheStore>();
        var accessService = provider.GetRequiredService<IItemAccessService>();

        // Must be a *cacheable* type for Register to actually populate the cache: ShouldCacheItem
        // only caches folders, Video and LiveTvChannel (Audio, used above, is deliberately NOT
        // cacheable, so registering one is a no-op and the lookup would fall through to the repo).
        // A folder with no Path set also short-circuits IsVisibleStandaloneInternal to visible
        // without touching collection folders / the static BaseItem.LibraryManager (see
        // VisibleItemNoTagRestrictions above for the same reasoning).
        var folder = new Folder { Id = Guid.NewGuid(), Name = "Cached folder" };
        cacheStore.Register(folder);

        var user = new User("test-user", "provider", "provider");
        var result = accessService.GetVisibleItemById<Folder>(folder.Id, user);

        Assert.Same(folder, result);
        _itemRepositoryMock.Verify(r => r.RetrieveItem(It.IsAny<Guid>()), Times.Never);
    }
}
