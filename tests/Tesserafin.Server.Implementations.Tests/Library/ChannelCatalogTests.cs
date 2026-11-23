using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Data;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Channels;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

/// <summary>
/// Standalone tests for <see cref="ChannelCatalog"/> (PR108): filtering/paging behavior against
/// mocked leaves, the two documented unsupported <see cref="ChannelQuery"/> options, and the RFC
/// invariant I1 (eager construction graph) / DI wiring proofs. Cross-checks against the real
/// <c>ChannelManager.GetChannelsInternalAsync</c> on shared fakes live in
/// <see cref="ChannelCatalogParityTests"/>.
/// </summary>
public sealed class ChannelCatalogTests
{
    private static IChannel MakeChannel(string name, bool enabledForAllUsers = true)
    {
        var mock = new Mock<IChannel>();
        mock.Setup(c => c.Name).Returns(name);
        mock.Setup(c => c.IsEnabledFor(It.IsAny<string>())).Returns(enabledForAllUsers);
        return mock.Object;
    }

    /// <summary>
    /// A user with the <c>EnableAllChannels</c> permission - <c>Channel.IsVisible</c> (an override
    /// this leaf inherits unchanged, since it calls the item's own virtual method rather than
    /// reimplementing visibility) otherwise hides every channel from a user with no
    /// <c>BlockedChannels</c>/<c>EnabledChannels</c> preference at all.
    /// </summary>
    private static User MakeUserWithChannelAccess()
    {
        var user = new User("user-" + Guid.NewGuid().ToString("N"), "auth", "reset");
        user.SetPermission(PermissionKind.EnableAllChannels, true);
        return user;
    }

    private static Channel MakeChannelItem(Guid id, string name)
    {
        return new Channel
        {
            Id = id,
            ChannelId = id,
            Name = name,
            ForcedSortName = name
        };
    }

    // ---------------------------------------------------------------
    // Lookup-only behavior: a channel plugin with no matching materialized item is silently
    // omitted (see ChannelCatalog's type-level remarks - materialization stays ChannelManager's
    // job, since it reaches into the SCC via its provider manager).
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetChannelsAsync_ChannelNotYetMaterialized_IsOmitted()
    {
        var id = Guid.NewGuid();
        var itemStoreMock = new Mock<IItemStore>();
        itemStoreMock.Setup(s => s.GetNewItemId("Channel Alpha", typeof(Channel))).Returns(id);
        var itemLookupServiceMock = new Mock<IItemLookupService>();
        itemLookupServiceMock.Setup(l => l.GetItemById<Channel>(id)).Returns((Channel?)null);

        var catalog = new ChannelCatalog(
            new[] { MakeChannel("Alpha") },
            itemLookupServiceMock.Object,
            itemStoreMock.Object,
            Mock.Of<IUserManager>(),
            Mock.Of<IStandaloneVisibilityService>(v => v.IsVisibleViaTags(It.IsAny<BaseItem>(), It.IsAny<User>(), It.IsAny<bool>())));

        var result = await catalog.GetChannelsAsync(new ChannelQuery());

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalRecordCount);
    }

    [Fact]
    public async Task GetChannelsAsync_MaterializedChannel_IsReturned()
    {
        var id = Guid.NewGuid();
        var item = MakeChannelItem(id, "Alpha");
        var itemStoreMock = new Mock<IItemStore>();
        itemStoreMock.Setup(s => s.GetNewItemId("Channel Alpha", typeof(Channel))).Returns(id);
        var itemLookupServiceMock = new Mock<IItemLookupService>();
        itemLookupServiceMock.Setup(l => l.GetItemById<Channel>(id)).Returns(item);

        var catalog = new ChannelCatalog(
            new[] { MakeChannel("Alpha") },
            itemLookupServiceMock.Object,
            itemStoreMock.Object,
            Mock.Of<IUserManager>(),
            Mock.Of<IStandaloneVisibilityService>(v => v.IsVisibleViaTags(It.IsAny<BaseItem>(), It.IsAny<User>(), It.IsAny<bool>())));

        var result = await catalog.GetChannelsAsync(new ChannelQuery());

        Assert.Same(item, Assert.Single(result.Items));
        Assert.Equal(1, result.TotalRecordCount);
    }

    // ---------------------------------------------------------------
    // User-scoped filtering: visibility + IChannel.IsEnabledFor, matching
    // ChannelManager.GetChannelsInternalAsync exactly for the subset UserViewManager exercises
    // (ChannelQuery { UserId = user.Id }).
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetChannelsAsync_UserSet_FiltersOutChannelsNotEnabledForUser()
    {
        var enabledId = Guid.NewGuid();
        var disabledId = Guid.NewGuid();
        var enabledItem = MakeChannelItem(enabledId, "Enabled");
        var disabledItem = MakeChannelItem(disabledId, "Disabled");

        var itemStoreMock = new Mock<IItemStore>();
        itemStoreMock.Setup(s => s.GetNewItemId("Channel Enabled", typeof(Channel))).Returns(enabledId);
        itemStoreMock.Setup(s => s.GetNewItemId("Channel Disabled", typeof(Channel))).Returns(disabledId);

        var itemLookupServiceMock = new Mock<IItemLookupService>();
        itemLookupServiceMock.Setup(l => l.GetItemById<Channel>(enabledId)).Returns(enabledItem);
        itemLookupServiceMock.Setup(l => l.GetItemById<Channel>(disabledId)).Returns(disabledItem);

        var user = MakeUserWithChannelAccess();
        var userManagerMock = new Mock<IUserManager>();
        userManagerMock.Setup(u => u.GetUserById(user.Id)).Returns(user);

        var channels = new[]
        {
            MakeChannel("Enabled", enabledForAllUsers: true),
            MakeChannel("Disabled", enabledForAllUsers: false)
        };

        var catalog = new ChannelCatalog(channels, itemLookupServiceMock.Object, itemStoreMock.Object, userManagerMock.Object, Mock.Of<IStandaloneVisibilityService>(v => v.IsVisibleViaTags(It.IsAny<BaseItem>(), It.IsAny<User>(), It.IsAny<bool>())));

        var result = await catalog.GetChannelsAsync(new ChannelQuery { UserId = user.Id });

        var returned = Assert.Single(result.Items);
        Assert.Same(enabledItem, returned);
    }

    /// <summary>
    /// <c>BlockedChannels</c>, now stored by the user policy: a non-empty list hides exactly the
    /// blocked channels and wins over <c>EnableAllChannels</c> and an <c>EnabledChannels</c> list
    /// naming the blocked one; an empty list defers to them again.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task GetChannelsAsync_UserSet_BlockedChannelsWinOverEnabledChannels()
    {
        var keptId = Guid.NewGuid();
        var blockedId = Guid.NewGuid();
        var keptItem = MakeChannelItem(keptId, "Kept");
        var blockedItem = MakeChannelItem(blockedId, "Blocked");

        var itemStoreMock = new Mock<IItemStore>();
        itemStoreMock.Setup(s => s.GetNewItemId("Channel Kept", typeof(Channel))).Returns(keptId);
        itemStoreMock.Setup(s => s.GetNewItemId("Channel Blocked", typeof(Channel))).Returns(blockedId);

        var itemLookupServiceMock = new Mock<IItemLookupService>();
        itemLookupServiceMock.Setup(l => l.GetItemById<Channel>(keptId)).Returns(keptItem);
        itemLookupServiceMock.Setup(l => l.GetItemById<Channel>(blockedId)).Returns(blockedItem);

        var user = MakeUserWithChannelAccess();
        var userManagerMock = new Mock<IUserManager>();
        userManagerMock.Setup(u => u.GetUserById(user.Id)).Returns(user);

        var catalog = new ChannelCatalog(new[] { MakeChannel("Kept"), MakeChannel("Blocked") }, itemLookupServiceMock.Object, itemStoreMock.Object, userManagerMock.Object, Mock.Of<IStandaloneVisibilityService>(v => v.IsVisibleViaTags(It.IsAny<BaseItem>(), It.IsAny<User>(), It.IsAny<bool>())));

        async Task<BaseItem[]> VisibleAsync() => (await catalog.GetChannelsAsync(new ChannelQuery { UserId = user.Id })).Items.ToArray();

        user.SetPreference(PreferenceKind.BlockedChannels, [blockedId]);
        Assert.Same(keptItem, Assert.Single(await VisibleAsync()));

        user.SetPermission(PermissionKind.EnableAllChannels, false);
        user.SetPreference(PreferenceKind.EnabledChannels, [blockedId]);
        Assert.Same(keptItem, Assert.Single(await VisibleAsync()));

        user.SetPreference(PreferenceKind.BlockedChannels, []);
        Assert.Same(blockedItem, Assert.Single(await VisibleAsync()));
    }

    [Fact]
    public async Task GetChannelsAsync_NoUserId_DoesNotResolveUserAndSkipsUserFiltering()
    {
        var id = Guid.NewGuid();
        var item = MakeChannelItem(id, "Alpha");
        var itemStoreMock = new Mock<IItemStore>();
        itemStoreMock.Setup(s => s.GetNewItemId("Channel Alpha", typeof(Channel))).Returns(id);
        var itemLookupServiceMock = new Mock<IItemLookupService>();
        itemLookupServiceMock.Setup(l => l.GetItemById<Channel>(id)).Returns(item);
        var userManagerMock = new Mock<IUserManager>();

        var catalog = new ChannelCatalog(new[] { MakeChannel("Alpha") }, itemLookupServiceMock.Object, itemStoreMock.Object, userManagerMock.Object, Mock.Of<IStandaloneVisibilityService>(v => v.IsVisibleViaTags(It.IsAny<BaseItem>(), It.IsAny<User>(), It.IsAny<bool>())));

        var result = await catalog.GetChannelsAsync(new ChannelQuery());

        Assert.Single(result.Items);
        userManagerMock.Verify(u => u.GetUserById(It.IsAny<Guid>()), Times.Never);
    }

    // ---------------------------------------------------------------
    // Paging: identical StartIndex/Limit math to ChannelManager.GetChannelsInternalAsync.
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetChannelsAsync_StartIndexAndLimit_PagesResults()
    {
        var names = new[] { "A", "B", "C", "D" };
        var itemStoreMock = new Mock<IItemStore>();
        var itemLookupServiceMock = new Mock<IItemLookupService>();
        var channels = new List<IChannel>();

        foreach (var name in names)
        {
            var id = Guid.NewGuid();
            itemStoreMock.Setup(s => s.GetNewItemId("Channel " + name, typeof(Channel))).Returns(id);
            itemLookupServiceMock.Setup(l => l.GetItemById<Channel>(id)).Returns(MakeChannelItem(id, name));
            channels.Add(MakeChannel(name));
        }

        var catalog = new ChannelCatalog(channels, itemLookupServiceMock.Object, itemStoreMock.Object, Mock.Of<IUserManager>(), Mock.Of<IStandaloneVisibilityService>(v => v.IsVisibleViaTags(It.IsAny<BaseItem>(), It.IsAny<User>(), It.IsAny<bool>())));

        var result = await catalog.GetChannelsAsync(new ChannelQuery { StartIndex = 1, Limit = 2 });

        Assert.Equal(4, result.TotalRecordCount);
        Assert.Equal(new[] { "B", "C" }, result.Items.Select(i => i.Name));
    }

    // ---------------------------------------------------------------
    // Documented unsupported options (RFC PR108 finding: no IUserDataManager/channel-refresh
    // dependency in this leaf's authorized dependency set).
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetChannelsAsync_IsFavoriteSet_Throws()
    {
        var catalog = new ChannelCatalog(Array.Empty<IChannel>(), Mock.Of<IItemLookupService>(), Mock.Of<IItemStore>(), Mock.Of<IUserManager>(), Mock.Of<IStandaloneVisibilityService>(v => v.IsVisibleViaTags(It.IsAny<BaseItem>(), It.IsAny<User>(), It.IsAny<bool>())));

        await Assert.ThrowsAsync<NotSupportedException>(() => catalog.GetChannelsAsync(new ChannelQuery { IsFavorite = true }));
    }

    [Fact]
    public async Task GetChannelsAsync_RefreshLatestChannelItemsSet_Throws()
    {
        var catalog = new ChannelCatalog(Array.Empty<IChannel>(), Mock.Of<IItemLookupService>(), Mock.Of<IItemStore>(), Mock.Of<IUserManager>(), Mock.Of<IStandaloneVisibilityService>(v => v.IsVisibleViaTags(It.IsAny<BaseItem>(), It.IsAny<User>(), It.IsAny<bool>())));

        await Assert.ThrowsAsync<NotSupportedException>(() => catalog.GetChannelsAsync(new ChannelQuery { RefreshLatestChannelItems = true }));
    }

    [Fact]
    public async Task GetChannelsAsync_NullQuery_Throws()
    {
        var catalog = new ChannelCatalog(Array.Empty<IChannel>(), Mock.Of<IItemLookupService>(), Mock.Of<IItemStore>(), Mock.Of<IUserManager>(), Mock.Of<IStandaloneVisibilityService>(v => v.IsVisibleViaTags(It.IsAny<BaseItem>(), It.IsAny<User>(), It.IsAny<bool>())));

        await Assert.ThrowsAsync<ArgumentNullException>(() => catalog.GetChannelsAsync(null!));
    }

    // ---------------------------------------------------------------
    // RFC I1 (§8): eager construction graph - no ctor parameter of ChannelCatalog, nor of the
    // known-autonomous concrete classes actually registered behind its interface dependencies in
    // ApplicationHost (ItemStore, ItemLookupService), is ILibraryManager/IUserViewManager/
    // IChannelManager/ILiveTvManager. No IDtoService either (RFC §4/§9 exit criterion).
    // ---------------------------------------------------------------

    [Fact]
    public void DiWiring_ChannelCatalogConstructorGraph_NoEagerEdgeToSccMembersOrDtoService()
    {
        var forbiddenTypes = new[]
        {
            typeof(ILibraryManager),
            typeof(IUserViewManager),
            typeof(IChannelManager),
            typeof(ILiveTvManager),
            typeof(Tesserafin.Controller.Dto.IDtoService)
        };

        var ctor = typeof(ChannelCatalog).GetConstructors().Single();
        var directParameterTypes = ctor.GetParameters().Select(p => p.ParameterType).ToArray();

        foreach (var forbidden in forbiddenTypes)
        {
            Assert.DoesNotContain(forbidden, directParameterTypes);
        }

        var knownAutonomousConcretes = new[]
        {
            typeof(Tesserafin.Server.Core.Library.ItemStore),
            typeof(Tesserafin.Server.Core.Library.ItemLookupService)
        };

        foreach (var concreteType in knownAutonomousConcretes)
        {
            var concreteCtor = concreteType.GetConstructors().Single();
            var concreteParameterTypes = concreteCtor.GetParameters().Select(p => p.ParameterType).ToArray();

            foreach (var forbidden in forbiddenTypes)
            {
                Assert.DoesNotContain(forbidden, concreteParameterTypes);
            }
        }
    }

    [Fact]
    public void DiWiring_ApplicationHostStyleRegistration_ChannelCatalogResolvesToAutonomousImplementation()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEnumerable<IChannel>>(Array.Empty<IChannel>());
        services.AddSingleton(Mock.Of<IItemLookupService>());
        services.AddSingleton(Mock.Of<IItemStore>());
        services.AddSingleton(Mock.Of<IUserManager>());
        services.AddSingleton(Mock.Of<IStandaloneVisibilityService>());
        services.AddSingleton<IChannelCatalog, ChannelCatalog>();

        using var provider = services.BuildServiceProvider();

        var resolved = provider.GetRequiredService<IChannelCatalog>();

        Assert.IsType<ChannelCatalog>(resolved);
    }

    [Fact]
    public void DiWiring_ApplicationHostStyleRegistration_ChannelCatalogIsSingleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEnumerable<IChannel>>(Array.Empty<IChannel>());
        services.AddSingleton(Mock.Of<IItemLookupService>());
        services.AddSingleton(Mock.Of<IItemStore>());
        services.AddSingleton(Mock.Of<IUserManager>());
        services.AddSingleton(Mock.Of<IStandaloneVisibilityService>());
        services.AddSingleton<IChannelCatalog, ChannelCatalog>();

        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IChannelCatalog>();
        var second = provider.GetRequiredService<IChannelCatalog>();

        Assert.Same(first, second);
    }
}
