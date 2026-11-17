using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Data;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.LiveTv.Channels;
using Tesserafin.Model.Channels;
using Tesserafin.Model.IO;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

/// <summary>
/// Parity tests: <see cref="ChannelCatalog.GetChannelsAsync"/> against the real
/// <c>ChannelManager.GetChannelsInternalAsync</c> (RFC <c>docs/rfc-di-query-user-views-v2.md</c>
/// §4, PR108), both driven off the exact same channel-plugin array and the exact same materialized
/// item registry (a shared <see cref="Dictionary{Guid, Channel}"/> plus a shared deterministic id
/// function fed to both <c>ChannelCatalog</c>'s leaf mocks and <c>ChannelManager</c>'s
/// <c>ILibraryManager</c> mock), so that any divergence in filtering/paging behavior for the
/// lookup-only subset shows up as a real assertion failure rather than an artifact of
/// differently-configured fakes.
/// </summary>
/// <remarks>
/// Only the query shape <c>UserViewManager</c> actually uses (<c>ChannelQuery { UserId = user.Id }</c>,
/// RFC §4) plus a couple of adjacent variants are exercised here - <see cref="ChannelCatalog"/>
/// deliberately does not support <c>IsFavorite</c>/<c>RefreshLatestChannelItems</c> (see
/// <see cref="ChannelCatalogTests"/>), so no materialization path (which would diverge, see
/// <see cref="ChannelCatalog"/>'s type-level remarks) is ever exercised by either side here.
/// </remarks>
public sealed class ChannelCatalogParityTests
{
    /// <summary>
    /// Deterministic id function shared by both sides' id-generation mock, standing in for the real
    /// <c>GetNewItemId</c> MD5-based algorithm (RFC PR106a) - what matters for parity is that both
    /// sides compute the exact same id for the exact same (key, type) pair, not that the algorithm
    /// matches production bit-for-bit.
    /// </summary>
    private static Guid ComputeId(string key, Type type)
    {
#pragma warning disable CA5351 // matches Tesserafin.Common.Extensions.BaseExtensions.GetMD5's suppression - test-only stand-in, see remarks above
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(key + "|" + type.FullName));
#pragma warning restore CA5351
        return new Guid(hash);
    }

    private static IChannel MakePlugin(string name, bool enabledForAllUsers = true)
    {
        var mock = new Mock<IChannel>();
        mock.Setup(c => c.Name).Returns(name);
        mock.Setup(c => c.IsEnabledFor(It.IsAny<string>())).Returns(enabledForAllUsers);
        return mock.Object;
    }

    [Fact]
    public async Task GetChannels_NoUser_SameResultAsChannelManager()
    {
        var fixture = new Fixture { Plugins = new[] { MakePlugin("Zulu"), MakePlugin("Alpha") } };
        fixture.AddMaterializedChannel("Zulu");
        fixture.AddMaterializedChannel("Alpha");

        var userManager = Mock.Of<IUserManager>();
        var catalog = fixture.BuildChannelCatalog(userManager);
        var manager = fixture.BuildChannelManager(userManager);

        var catalogResult = await catalog.GetChannelsAsync(new ChannelQuery());
        var managerResult = await manager.GetChannelsInternalAsync(new ChannelQuery());

        Assert.Equal(managerResult.TotalRecordCount, catalogResult.TotalRecordCount);
        Assert.Equal(managerResult.Items.Select(i => i.Id), catalogResult.Items.Select(i => i.Id));
        Assert.Equal(managerResult.Items.Select(i => i.Name), catalogResult.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task GetChannels_UserFiltersDisabledChannel_SameResultAsChannelManager()
    {
        var fixture = new Fixture
        {
            Plugins = new[]
            {
                MakePlugin("Enabled", enabledForAllUsers: true),
                MakePlugin("Disabled", enabledForAllUsers: false)
            }
        };
        fixture.AddMaterializedChannel("Enabled");
        fixture.AddMaterializedChannel("Disabled");

        // Channel.IsVisible (an override this leaf inherits unchanged) hides every channel from a
        // user with no BlockedChannels/EnabledChannels preference and no EnableAllChannels
        // permission - grant it so the "Enabled" channel is actually reachable for both sides.
        var user = new User("bob", "auth", "reset");
        user.SetPermission(PermissionKind.EnableAllChannels, true);
        var userManagerMock = new Mock<IUserManager>();
        userManagerMock.Setup(u => u.GetUserById(user.Id)).Returns(user);

        var catalog = fixture.BuildChannelCatalog(userManagerMock.Object);
        var manager = fixture.BuildChannelManager(userManagerMock.Object);

        // The exact query shape UserViewManager.GetUserViews builds (RFC §4).
        var catalogResult = await catalog.GetChannelsAsync(new ChannelQuery { UserId = user.Id });
        var managerResult = await manager.GetChannelsInternalAsync(new ChannelQuery { UserId = user.Id });

        Assert.Equal(managerResult.TotalRecordCount, catalogResult.TotalRecordCount);
        Assert.Equal(managerResult.Items.Select(i => i.Name), catalogResult.Items.Select(i => i.Name));
        Assert.Equal("Enabled", Assert.Single(catalogResult.Items).Name);
    }

    [Fact]
    public async Task GetChannels_StartIndexAndLimit_SameResultAsChannelManager()
    {
        var names = new[] { "A", "B", "C", "D", "E" };
        var fixture = new Fixture { Plugins = names.Select(n => MakePlugin(n)).ToArray() };
        foreach (var name in names)
        {
            fixture.AddMaterializedChannel(name);
        }

        var userManager = Mock.Of<IUserManager>();
        var catalog = fixture.BuildChannelCatalog(userManager);
        var manager = fixture.BuildChannelManager(userManager);

        var query = new ChannelQuery { StartIndex = 2, Limit = 2 };
        var catalogResult = await catalog.GetChannelsAsync(query);
        var managerResult = await manager.GetChannelsInternalAsync(query);

        Assert.Equal(managerResult.TotalRecordCount, catalogResult.TotalRecordCount);
        Assert.Equal(managerResult.Items.Select(i => i.Name), catalogResult.Items.Select(i => i.Name));
    }

    private sealed class Fixture
    {
        public Dictionary<Guid, Channel> Registry { get; } = new();

        public IChannel[] Plugins { get; init; } = Array.Empty<IChannel>();

        public ChannelCatalog BuildChannelCatalog(IUserManager userManager)
        {
            var itemStoreMock = new Mock<IItemStore>();
            itemStoreMock.Setup(s => s.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>()))
                .Returns((string key, Type type) => ComputeId(key, type));

            var itemLookupServiceMock = new Mock<IItemLookupService>();
            itemLookupServiceMock.Setup(l => l.GetItemById<Channel>(It.IsAny<Guid>()))
                .Returns((Guid id) => Registry.GetValueOrDefault(id));

            return new ChannelCatalog(Plugins, itemLookupServiceMock.Object, itemStoreMock.Object, userManager);
        }

        public ChannelManager BuildChannelManager(IUserManager userManager)
        {
            var libraryManagerMock = new Mock<ILibraryManager>();
            libraryManagerMock.Setup(l => l.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>()))
                .Returns((string key, Type type) => ComputeId(key, type));
            libraryManagerMock.Setup(l => l.GetItemById(It.IsAny<Guid>()))
                .Returns((Guid id) => Registry.GetValueOrDefault(id));

            return new ChannelManager(
                Mock.Of<IItemPeopleCommands>(),
                Mock.Of<IItemWriteCommands>(),
                Mock.Of<IItemDeletionCommands>(),
                userManager,
                Mock.Of<IDtoService>(),
                libraryManagerMock.Object,
                NullLogger<ChannelManager>.Instance,
                Mock.Of<IServerConfigurationManager>(),
                Mock.Of<IFileSystem>(),
                Mock.Of<IUserDataManager>(),
                Mock.Of<IProviderManager>(),
                new MemoryCache(new MemoryCacheOptions()),
                Plugins);
        }

        public Channel AddMaterializedChannel(string name)
        {
            var id = ComputeId("Channel " + name, typeof(Channel));
            var item = new Channel
            {
                Id = id,
                ChannelId = id,
                Name = name,
                ForcedSortName = name
            };
            Registry[id] = item;
            return item;
        }
    }
}
