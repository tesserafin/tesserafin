using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Common;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Drawing;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Data;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.LiveTv;
using Tesserafin.LiveTv.Timers;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.LiveTv;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

/// <summary>
/// Parity tests: <see cref="LiveTvPresenceProvider"/> against the real
/// <c>LiveTvManager.GetEnabledUsers</c>/<c>GetInternalLiveTvFolder</c> (RFC
/// <c>docs/rfc-di-query-user-views-v2.md</c> §5, PR108), sharing the exact same
/// <see cref="IUserManager"/>, <see cref="IServerConfigurationManager"/>,
/// <see cref="IEnumerable{ILiveTvService}"/> and <see cref="ILocalizationManager"/> fakes between
/// both sides. <b>PR110</b>: <c>LiveTvManager.GetInternalLiveTvFolder</c> now also calls
/// <c>IUserViewFactory.GetNamedView</c> (no more <c>ILibraryManager.GetNamedView</c>) - both sides
/// share the exact same <see cref="IUserViewFactory"/> mock, configured to return the same sentinel
/// <see cref="UserView"/> instance for the same arguments.
/// </summary>
public sealed class LiveTvPresenceProviderParityTests
{
    private static User MakeUser(bool liveTvPermission)
    {
        var user = new User("user-" + Guid.NewGuid().ToString("N"), "auth", "reset");
        user.SetPermission(PermissionKind.EnableLiveTvAccess, liveTvPermission);
        return user;
    }

    /// <summary>
    /// <c>IServerConfigurationManager</c> stub whose <c>CommonApplicationPaths.DataPath</c> is a
    /// real path - needed only because <c>LiveTvManager</c>'s own constructor requires an actual
    /// <see cref="DefaultLiveTvService"/> instance (<c>_services.OfType&lt;DefaultLiveTvService&gt;().First()</c>,
    /// <c>LiveTvManager.cs:76</c> - unrelated to <see cref="LiveTvPresenceProvider"/>'s ported
    /// behavior, but unavoidable to construct a real comparison target), whose own dependency chain
    /// (<see cref="TimerManager"/>/<see cref="SeriesTimerManager"/>) computes a data file path at
    /// construction time (lazily read, never touched by the calls under test here).
    /// </summary>
    private static Mock<IServerConfigurationManager> MakeConfigMock(LiveTvOptions? liveTvOptions = null)
    {
        var mock = new Mock<IServerConfigurationManager>();
        mock.Setup(c => c.CommonApplicationPaths).Returns(Mock.Of<IApplicationPaths>(p => p.DataPath == Path.GetTempPath()));
        mock.Setup(c => c.GetConfiguration("livetv")).Returns(liveTvOptions ?? new LiveTvOptions());
        return mock;
    }

    /// <summary>
    /// Builds the one real <see cref="ILiveTvService"/> <c>LiveTvManager</c>'s constructor requires
    /// (see <see cref="MakeConfigMock"/>'s remarks) - every one of its own dependencies is either an
    /// unused interface mock or a lazily-loading concrete data provider, since none of them are
    /// exercised by <c>GetEnabledUsers</c>/<c>GetInternalLiveTvFolder</c>.
    /// </summary>
    private static DefaultLiveTvService MakeDefaultLiveTvService(IServerConfigurationManager config, ILibraryManager libraryManager, LiveTvDtoService dtoService)
    {
        return new DefaultLiveTvService(
            NullLogger<DefaultLiveTvService>.Instance,
            config,
            Mock.Of<ITunerHostManager>(),
            Mock.Of<IListingsManager>(),
            Mock.Of<IRecordingsManager>(),
            libraryManager,
            dtoService,
            new TimerManager(NullLogger<TimerManager>.Instance, config),
            new SeriesTimerManager(NullLogger<SeriesTimerManager>.Instance, config));
    }

    [Fact]
    public void GetEnabledUsers_SameResultAsLiveTvManager()
    {
        var enabledUser = MakeUser(liveTvPermission: true);
        var disabledUser = MakeUser(liveTvPermission: false);

        var userManagerMock = new Mock<IUserManager>();
        userManagerMock.Setup(u => u.GetUsers()).Returns(new[] { enabledUser, disabledUser });

        var configMock = MakeConfigMock(new LiveTvOptions { TunerHosts = new[] { new TunerHostInfo() } });
        var libraryManagerMock = new Mock<ILibraryManager>();
        var localizationMock = new Mock<ILocalizationManager>();
        var liveTvDtoService = new LiveTvDtoService(Mock.Of<IDtoService>(), Mock.Of<IImageProcessor>(), NullLogger<LiveTvDtoService>.Instance, Mock.Of<IApplicationHost>(), libraryManagerMock.Object);
        var defaultService = MakeDefaultLiveTvService(configMock.Object, libraryManagerMock.Object, liveTvDtoService);
        var services = new ILiveTvService[] { defaultService };

        var provider = new LiveTvPresenceProvider(
            userManagerMock.Object,
            configMock.Object,
            localizationMock.Object,
            Mock.Of<IUserViewFactory>(),
            new Lazy<IReadOnlyList<ILiveTvService>>(() => services));

        var manager = new LiveTvManager(
            configMock.Object,
            NullLogger<LiveTvManager>.Instance,
            Mock.Of<IUserDataManager>(),
            Mock.Of<IDtoService>(),
            userManagerMock.Object,
            libraryManagerMock.Object,
            localizationMock.Object,
            Mock.Of<Tesserafin.Controller.Channels.IChannelManager>(),
            Mock.Of<IRecordingsManager>(),
            liveTvDtoService,
            services,
            Mock.Of<IUserViewFactory>(),
            Mock.Of<IStandaloneVisibilityService>());

        var providerResult = provider.GetEnabledUsers().Select(u => u.Id).ToArray();
        var managerResult = manager.GetEnabledUsers().Select(u => u.Id).ToArray();

        Assert.Equal(managerResult, providerResult);
        Assert.Equal(new[] { enabledUser.Id }, providerResult);
    }

    [Fact]
    public void GetLiveTvFolder_SameResultShapeAsLiveTvManager()
    {
        var sharedFolder = new UserView { Id = Guid.NewGuid(), Name = "Live TV", ViewType = CollectionType.livetv };

        var localizationMock = new Mock<ILocalizationManager>();
        localizationMock.Setup(l => l.GetLocalizedString("HeaderLiveTV")).Returns("Live TV");

        var userViewFactoryMock = new Mock<IUserViewFactory>();
        userViewFactoryMock.Setup(f => f.GetNamedView("Live TV", CollectionType.livetv, "Live TV")).Returns(sharedFolder);

        var libraryManagerMock = new Mock<ILibraryManager>();

        var configMock = MakeConfigMock();
        var liveTvDtoService = new LiveTvDtoService(Mock.Of<IDtoService>(), Mock.Of<IImageProcessor>(), NullLogger<LiveTvDtoService>.Instance, Mock.Of<IApplicationHost>(), libraryManagerMock.Object);
        var services = new ILiveTvService[] { MakeDefaultLiveTvService(configMock.Object, libraryManagerMock.Object, liveTvDtoService) };

        var provider = new LiveTvPresenceProvider(
            Mock.Of<IUserManager>(),
            configMock.Object,
            localizationMock.Object,
            userViewFactoryMock.Object,
            new Lazy<IReadOnlyList<ILiveTvService>>(() => services));

        var manager = new LiveTvManager(
            configMock.Object,
            NullLogger<LiveTvManager>.Instance,
            Mock.Of<IUserDataManager>(),
            Mock.Of<IDtoService>(),
            Mock.Of<IUserManager>(),
            libraryManagerMock.Object,
            localizationMock.Object,
            Mock.Of<Tesserafin.Controller.Channels.IChannelManager>(),
            Mock.Of<IRecordingsManager>(),
            liveTvDtoService,
            services,
            userViewFactoryMock.Object,
            Mock.Of<IStandaloneVisibilityService>());

        var providerResult = provider.GetLiveTvFolder(CancellationToken.None);
        var managerResult = manager.GetInternalLiveTvFolder(CancellationToken.None);

        Assert.Same(sharedFolder, providerResult);
        Assert.Same(sharedFolder, managerResult);
    }
}
