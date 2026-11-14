using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Data;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.LiveTv;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.LiveTv;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

/// <summary>
/// Standalone tests for <see cref="LiveTvPresenceProvider"/> (PR108): the ported
/// <c>IsLiveTvEnabled</c> logic, the folder delegation to <see cref="IUserViewFactory"/>, and the
/// RFC invariant I1 (eager construction graph) / DI wiring proofs. Cross-checks against the real
/// <c>LiveTvManager.GetEnabledUsers</c>/<c>GetInternalLiveTvFolder</c> on shared fakes live in
/// <see cref="LiveTvPresenceProviderParityTests"/>.
/// </summary>
public sealed class LiveTvPresenceProviderTests
{
    private static User MakeUser(bool liveTvPermission)
    {
        var user = new User("user-" + Guid.NewGuid().ToString("N"), "auth", "reset");
        user.SetPermission(PermissionKind.EnableLiveTvAccess, liveTvPermission);
        return user;
    }

    /// <summary>
    /// <c>GetConfiguration&lt;LiveTvOptions&gt;("livetv")</c> is an extension method
    /// (<c>(T)manager.GetConfiguration(key)</c>) - Moq cannot intercept it directly, so tests stub
    /// the real interface member it casts, <see cref="IConfigurationManager.GetConfiguration(string)"/>.
    /// </summary>
    private static IServerConfigurationManager MakeConfig(LiveTvOptions? options = null)
    {
        var mock = new Mock<IServerConfigurationManager>();
        mock.Setup(c => c.GetConfiguration("livetv")).Returns(options ?? new LiveTvOptions());
        return mock.Object;
    }

    private static Lazy<IReadOnlyList<ILiveTvService>> MakeServicesFactory(params ILiveTvService[] services)
    {
        return new Lazy<IReadOnlyList<ILiveTvService>>(() => services);
    }

    private static LiveTvPresenceProvider Build(
        IUserManager? userManager = null,
        IServerConfigurationManager? config = null,
        ILocalizationManager? localization = null,
        IUserViewFactory? userViewFactory = null,
        Lazy<IReadOnlyList<ILiveTvService>>? servicesFactory = null)
    {
        return new LiveTvPresenceProvider(
            userManager ?? Mock.Of<IUserManager>(),
            config ?? MakeConfig(),
            localization ?? Mock.Of<ILocalizationManager>(),
            userViewFactory ?? Mock.Of<IUserViewFactory>(),
            servicesFactory ?? MakeServicesFactory());
    }

    // ---------------------------------------------------------------
    // GetEnabledUsers / IsLiveTvEnabled parity with LiveTvManager (RFC §5).
    // ---------------------------------------------------------------

    [Fact]
    public void GetEnabledUsers_UserWithoutPermission_IsExcluded()
    {
        var enabledUser = MakeUser(liveTvPermission: true);
        var disabledUser = MakeUser(liveTvPermission: false);
        var userManagerMock = new Mock<IUserManager>();
        userManagerMock.Setup(u => u.GetUsers()).Returns(new[] { enabledUser, disabledUser });

        var config = MakeConfig(new LiveTvOptions { TunerHosts = new[] { new TunerHostInfo() } });

        var provider = Build(userManager: userManagerMock.Object, config: config);

        var result = provider.GetEnabledUsers().ToArray();

        Assert.Same(enabledUser, Assert.Single(result));
    }

    [Fact]
    public void GetEnabledUsers_PermissionButNoTunerHostsAndSingleService_IsExcluded()
    {
        var user = MakeUser(liveTvPermission: true);
        var userManagerMock = new Mock<IUserManager>();
        userManagerMock.Setup(u => u.GetUsers()).Returns(new[] { user });

        var config = MakeConfig();

        var provider = Build(userManager: userManagerMock.Object, config: config, servicesFactory: MakeServicesFactory(Mock.Of<ILiveTvService>()));

        Assert.Empty(provider.GetEnabledUsers());
    }

    [Fact]
    public void GetEnabledUsers_PermissionAndMultipleServices_NoTunerHostsNeeded_IsIncluded()
    {
        var user = MakeUser(liveTvPermission: true);
        var userManagerMock = new Mock<IUserManager>();
        userManagerMock.Setup(u => u.GetUsers()).Returns(new[] { user });

        var config = MakeConfig();

        var provider = Build(
            userManager: userManagerMock.Object,
            config: config,
            servicesFactory: MakeServicesFactory(Mock.Of<ILiveTvService>(), Mock.Of<ILiveTvService>()));

        Assert.Same(user, Assert.Single(provider.GetEnabledUsers()));
    }

    [Fact]
    public void GetEnabledUsers_PermissionAndTunerHostConfigured_SingleService_IsIncluded()
    {
        var user = MakeUser(liveTvPermission: true);
        var userManagerMock = new Mock<IUserManager>();
        userManagerMock.Setup(u => u.GetUsers()).Returns(new[] { user });

        var config = MakeConfig(new LiveTvOptions { TunerHosts = new[] { new TunerHostInfo() } });

        var provider = Build(userManager: userManagerMock.Object, config: config, servicesFactory: MakeServicesFactory(Mock.Of<ILiveTvService>()));

        Assert.Same(user, Assert.Single(provider.GetEnabledUsers()));
    }

    [Fact]
    public void GetEnabledUsers_ServicesFactoryNotEvaluated_WhenUserLacksPermission()
    {
        // IsLiveTvEnabled short-circuits on the permission check before touching Services.Count -
        // Lazy<T>.Value should never be forced for a user who fails the permission gate alone.
        var user = MakeUser(liveTvPermission: false);
        var userManagerMock = new Mock<IUserManager>();
        userManagerMock.Setup(u => u.GetUsers()).Returns(new[] { user });

        var evaluated = false;
        var servicesFactory = new Lazy<IReadOnlyList<ILiveTvService>>(() =>
        {
            evaluated = true;
            return Array.Empty<ILiveTvService>();
        });

        var provider = Build(userManager: userManagerMock.Object, servicesFactory: servicesFactory);

        Assert.Empty(provider.GetEnabledUsers());
        Assert.False(evaluated);
    }

    // ---------------------------------------------------------------
    // GetLiveTvFolder: delegates to IUserViewFactory.GetNamedView, never ILibraryManager directly.
    // ---------------------------------------------------------------

    [Fact]
    public void GetLiveTvFolder_DelegatesToUserViewFactoryWithLocalizedNameAndLiveTvViewType()
    {
        var localizationMock = new Mock<ILocalizationManager>();
        localizationMock.Setup(l => l.GetLocalizedString("HeaderLiveTV")).Returns("Live TV");

        var expected = new UserView { Id = Guid.NewGuid() };
        var userViewFactoryMock = new Mock<IUserViewFactory>();
        userViewFactoryMock
            .Setup(f => f.GetNamedView("Live TV", CollectionType.livetv, "Live TV"))
            .Returns(expected);

        var provider = Build(localization: localizationMock.Object, userViewFactory: userViewFactoryMock.Object);

        var result = provider.GetLiveTvFolder(CancellationToken.None);

        Assert.Same(expected, result);
        userViewFactoryMock.Verify(f => f.GetNamedView("Live TV", CollectionType.livetv, "Live TV"), Times.Once);
    }

    // ---------------------------------------------------------------
    // RFC I1 (§8): eager construction graph - no ctor parameter of LiveTvPresenceProvider is
    // ILibraryManager/IUserViewManager/IChannelManager/ILiveTvManager/IDtoService, IUserViewFactory
    // (its one indirect path toward a Lazy<IProviderManager>) is itself already proven I1-clean
    // (PR106b), and - the PR108 finding this class's remarks document - ILiveTvService only ever
    // appears wrapped in Lazy<IReadOnlyList<T>>, never as a direct IEnumerable<ILiveTvService>:
    // the in-tree DefaultLiveTvService concrete takes ILibraryManager directly, so a direct
    // injection would recreate the construction cycle exactly like a non-Lazy IProviderManager
    // would in UserViewFactory.
    // ---------------------------------------------------------------

    [Fact]
    public void DiWiring_LiveTvPresenceProviderConstructorGraph_NoEagerEdgeToSccMembers()
    {
        var forbiddenTypes = new[]
        {
            typeof(ILibraryManager),
            typeof(IUserViewManager),
            typeof(Tesserafin.Controller.Channels.IChannelManager),
            typeof(ILiveTvManager),
            typeof(Tesserafin.Controller.Dto.IDtoService)
        };

        var ctor = typeof(LiveTvPresenceProvider).GetConstructors().Single();
        var directParameterTypes = ctor.GetParameters().Select(p => p.ParameterType).ToArray();

        foreach (var forbidden in forbiddenTypes)
        {
            Assert.DoesNotContain(forbidden, directParameterTypes);
        }

        // ILiveTvService must never appear as a direct/IEnumerable parameter - only under
        // Lazy<IReadOnlyList<T>> (see type-level remarks: PR108 finding).
        Assert.DoesNotContain(typeof(IEnumerable<ILiveTvService>), directParameterTypes);
        Assert.Contains(typeof(Lazy<IReadOnlyList<ILiveTvService>>), directParameterTypes);

        // The one concrete class behind IUserViewFactory that ApplicationHost actually wires up.
        var userViewFactoryCtor = typeof(UserViewFactory).GetConstructors().Single();
        var userViewFactoryParameterTypes = userViewFactoryCtor.GetParameters().Select(p => p.ParameterType).ToArray();

        foreach (var forbidden in forbiddenTypes)
        {
            Assert.DoesNotContain(forbidden, userViewFactoryParameterTypes);
        }
    }

    /// <summary>
    /// The PR108 finding, asserted directly: <c>DefaultLiveTvService</c> (the concrete
    /// <c>ApplicationHost</c>/<c>AddLiveTvServices</c> actually registers behind
    /// <c>ILiveTvService</c>) has a real eager edge to <c>ILibraryManager</c> in its own
    /// constructor - proving that a direct (non-Lazy) <c>IEnumerable&lt;ILiveTvService&gt;</c>
    /// injection on <see cref="LiveTvPresenceProvider"/> would have violated I1, and that the
    /// <c>Lazy&lt;IReadOnlyList&lt;ILiveTvService&gt;&gt;</c> wrapping is load-bearing, not
    /// decorative.
    /// </summary>
    [Fact]
    public void DiWiring_DefaultLiveTvServiceConcrete_HasEagerEdgeToLibraryManager_JustifyingLazyWrapping()
    {
        var defaultLiveTvServiceCtor = typeof(DefaultLiveTvService).GetConstructors().Single();
        var parameterTypes = defaultLiveTvServiceCtor.GetParameters().Select(p => p.ParameterType).ToArray();

        Assert.Contains(typeof(ILibraryManager), parameterTypes);
    }

    [Fact]
    public void DiWiring_ApplicationHostStyleRegistration_LiveTvPresenceProviderResolvesToAutonomousImplementation()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IUserManager>());
        services.AddSingleton(Mock.Of<IServerConfigurationManager>());
        services.AddSingleton(Mock.Of<ILocalizationManager>());
        services.AddSingleton(Mock.Of<IUserViewFactory>());
        services.AddSingleton(MakeServicesFactory());
        services.AddSingleton<ILiveTvPresenceProvider, LiveTvPresenceProvider>();

        using var provider = services.BuildServiceProvider();

        var resolved = provider.GetRequiredService<ILiveTvPresenceProvider>();

        Assert.IsType<LiveTvPresenceProvider>(resolved);
    }

    [Fact]
    public void DiWiring_ApplicationHostStyleRegistration_LiveTvPresenceProviderIsSingleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IUserManager>());
        services.AddSingleton(Mock.Of<IServerConfigurationManager>());
        services.AddSingleton(Mock.Of<ILocalizationManager>());
        services.AddSingleton(Mock.Of<IUserViewFactory>());
        services.AddSingleton(MakeServicesFactory());
        services.AddSingleton<ILiveTvPresenceProvider, LiveTvPresenceProvider>();

        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<ILiveTvPresenceProvider>();
        var second = provider.GetRequiredService<ILiveTvPresenceProvider>();

        Assert.Same(first, second);
    }
}
