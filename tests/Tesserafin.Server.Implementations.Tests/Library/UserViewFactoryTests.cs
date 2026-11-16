using System.Linq;
using System.Reflection;
using System;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.IO;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

/// <summary>
/// Standalone tests for <see cref="UserViewFactory"/> (PR106b): guard clauses and the two RFC
/// invariant proofs (<c>docs/rfc-di-query-user-views-v2.md</c> §8) that do not need a real
/// <c>LibraryManager</c> - I1 (eager construction graph) and DI wiring shape. Parity/behavior tests
/// against the real, delegating <c>LibraryManager</c> facade (and the I2 evaluation-timing tests,
/// which need a shared <c>ItemLookupService</c>/<c>ItemStore</c> pair to set up an "existing view")
/// live in <c>LibraryManager/LibraryManagerUserViewFactoryTests.cs</c>.
/// </summary>
public class UserViewFactoryTests
{
    private readonly Mock<IItemLookupService> _itemLookupServiceMock;
    private readonly Mock<IItemStore> _itemStoreMock;
    private readonly Mock<IServerConfigurationManager> _configurationManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly UserViewFactory _userViewFactory;

    public UserViewFactoryTests()
    {
        _itemLookupServiceMock = new Mock<IItemLookupService>();
        _itemStoreMock = new Mock<IItemStore>();
        _configurationManagerMock = new Mock<IServerConfigurationManager>();
        _fileSystemMock = new Mock<IFileSystem>();

        _userViewFactory = new UserViewFactory(
            Mock.Of<IItemWriteCommands>(),
            _itemLookupServiceMock.Object,
            _itemStoreMock.Object,
            _configurationManagerMock.Object,
            _fileSystemMock.Object,
            new Lazy<IProviderManager>(() => Mock.Of<IProviderManager>()));
    }

    // ---------------------------------------------------------------
    // Guard clauses.
    // ---------------------------------------------------------------

    [Fact]
    public void GetShadowView_NullParent_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _userViewFactory.GetShadowView(null!, null, "SortName"));
    }

    [Fact]
    public void GetNamedView_UniqueIdOverload_EmptyName_Throws()
    {
        Assert.Throws<ArgumentException>(() => _userViewFactory.GetNamedView(string.Empty, Guid.NewGuid(), null, "SortName", "unique"));
    }

    // ---------------------------------------------------------------
    // RFC I1 (§8): eager construction graph - no ctor parameter of UserViewFactory, nor of the
    // known-autonomous concrete classes actually registered behind its interface dependencies in
    // ApplicationHost (ItemStore, ItemLookupService), is ILibraryManager/IUserViewManager/
    // IChannelManager/ILiveTvManager. IProviderManager appears only wrapped in Lazy<T> - a direct
    // (non-Lazy) injection would recreate the construction cycle once LibraryManager delegates to
    // this class (see UserViewFactory's type-level remarks and RFC §2's ProviderManager.cs:117
    // warning).
    // ---------------------------------------------------------------

    [Fact]
    public void DiWiring_UserViewFactoryConstructorGraph_NoEagerEdgeToSccMembers()
    {
        var forbiddenSccTypes = new[]
        {
            typeof(ILibraryManager),
            typeof(IUserViewManager),
            typeof(IChannelManager),
            typeof(ILiveTvManager)
        };

        var ctor = typeof(UserViewFactory).GetConstructors().Single();
        var directParameterTypes = ctor.GetParameters().Select(p => p.ParameterType).ToArray();

        foreach (var forbidden in forbiddenSccTypes)
        {
            Assert.DoesNotContain(forbidden, directParameterTypes);
        }

        // IProviderManager must never appear as a direct parameter - only under Lazy<T>.
        Assert.DoesNotContain(typeof(IProviderManager), directParameterTypes);
        Assert.Contains(typeof(Lazy<IProviderManager>), directParameterTypes);

        // Transitive level: the concrete classes ApplicationHost actually wires up behind
        // IItemLookupService/IItemStore (ItemLookupService, ItemStore - see ApplicationHost.cs
        // AddSingleton<IItemLookupService>/AddSingleton<IItemStore, ItemStore>). Verified at the
        // concrete-class level, not just the interface, per RFC §2's "vérifié au niveau de leurs
        // classes concrètes" requirement - an interface-only check would not catch an alias/factory
        // secretly casting a SCC member (the IUserRootFolderProvider trap, RFC §0/§1).
        var knownAutonomousConcretes = new[]
        {
            typeof(Tesserafin.Server.Core.Library.ItemStore),
            typeof(Tesserafin.Server.Core.Library.ItemLookupService)
        };

        foreach (var concreteType in knownAutonomousConcretes)
        {
            var concreteCtor = concreteType.GetConstructors().Single();
            var concreteParameterTypes = concreteCtor.GetParameters().Select(p => p.ParameterType).ToArray();

            foreach (var forbidden in forbiddenSccTypes)
            {
                Assert.DoesNotContain(forbidden, concreteParameterTypes);
            }

            Assert.DoesNotContain(typeof(IProviderManager), concreteParameterTypes);
        }
    }

    [Fact]
    public void DiWiring_ApplicationHostStyleRegistration_UserViewFactoryResolvesToAutonomousImplementation()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_itemLookupServiceMock.Object);
        services.AddSingleton(_itemStoreMock.Object);
        services.AddSingleton(_configurationManagerMock.Object);
        services.AddSingleton(_fileSystemMock.Object);
        services.AddSingleton(new Lazy<IProviderManager>(() => Mock.Of<IProviderManager>()));
        services.AddSingleton<IItemWriteCommands>(Mock.Of<IItemWriteCommands>());
        services.AddSingleton<IUserViewFactory, UserViewFactory>();

        using var provider = services.BuildServiceProvider();

        var resolved = provider.GetRequiredService<IUserViewFactory>();

        Assert.IsType<UserViewFactory>(resolved);
    }

    [Fact]
    public void DiWiring_ApplicationHostStyleRegistration_UserViewFactoryIsSingleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_itemLookupServiceMock.Object);
        services.AddSingleton(_itemStoreMock.Object);
        services.AddSingleton(_configurationManagerMock.Object);
        services.AddSingleton(_fileSystemMock.Object);
        services.AddSingleton(new Lazy<IProviderManager>(() => Mock.Of<IProviderManager>()));
        services.AddSingleton<IItemWriteCommands>(Mock.Of<IItemWriteCommands>());
        services.AddSingleton<IUserViewFactory, UserViewFactory>();

        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IUserViewFactory>();
        var second = provider.GetRequiredService<IUserViewFactory>();

        Assert.Same(first, second);
    }
}
