using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Sorting;
using Tesserafin.Data;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.LiveTv;
using Tesserafin.Model.Channels;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.Library;
using Tesserafin.Model.Querying;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

/// <summary>
/// Standalone tests for <see cref="UserViewCatalog"/> (PR109): behavior exercised with mocked
/// leaves (grouping, <c>EnableFolderView</c>, hidden/ordered preferences, channels/LiveTV toggles),
/// and the RFC invariant I1 (eager construction graph) / DI wiring proofs. The playlist/boxset probe
/// itself is characterised in <see cref="VisibleContentProbeTests"/>. Cross-checks against the real
/// <c>UserViewManager.GetUserViews</c> on shared fakes live in
/// <see cref="UserViewCatalogParityTests"/>.
/// </summary>
/// <remarks>
/// <c>BaseItem.SortName</c> (used by the final <c>OrderBy</c>/<c>ThenBy</c> in
/// <see cref="UserViewCatalog.GetUserViews"/>, verbatim from the historical implementation) reads
/// the process-wide static <c>BaseItem.ConfigurationManager</c> - this class sets it per test and
/// shares the same non-parallel xunit collection as <c>LibraryManagerUserViewFactoryTests</c> et al.
/// so tests touching this static never race each other (see
/// <see cref="Tesserafin.Server.Implementations.Tests.Library.LibraryManager.LibraryManagerStaticStateFixture"/>).
/// </remarks>
[Collection(Tesserafin.Server.Implementations.Tests.Library.LibraryManager.LibraryManagerStaticStateFixture.Name)]
public sealed class UserViewCatalogTests
{
    private static User MakeUser()
    {
        return new User("user-" + Guid.NewGuid().ToString("N"), "auth", "reset") { Id = Guid.NewGuid() };
    }

    private static IServerConfigurationManager MakeConfig(bool enableFolderView = false)
    {
        var mock = new Mock<IServerConfigurationManager>();
        mock.Setup(c => c.Configuration).Returns(new ServerConfiguration { EnableFolderView = enableFolderView });
        Tesserafin.Controller.Entities.BaseItem.ConfigurationManager = mock.Object;
        return mock.Object;
    }

    private static Folder MakeRoot(params Folder[] children)
    {
        return new FakeChildrenFolder(children);
    }

    private static IVisibleContentProbe MakeProbe(Folder library, User user, bool hasVisibleContent)
    {
        var probe = new Mock<IVisibleContentProbe>(MockBehavior.Strict);
        probe.Setup(p => p.HasVisibleContent(library, user)).Returns(hasVisibleContent);
        return probe.Object;
    }

    private static UserViewCatalog Build(
        IUserRootFolderProvider? userRootFolderProvider = null,
        IUserViewFactory? userViewFactory = null,
        IChannelCatalog? channelCatalog = null,
        ILiveTvPresenceProvider? liveTvPresenceProvider = null,
        IItemSortService? itemSortService = null,
        IServerConfigurationManager? config = null,
        ILocalizationManager? localizationManager = null,
        IVisibleContentProbe? visibleContentProbe = null)
    {
        return new UserViewCatalog(
            userRootFolderProvider ?? Mock.Of<IUserRootFolderProvider>(),
            userViewFactory ?? Mock.Of<IUserViewFactory>(),
            channelCatalog ?? Mock.Of<IChannelCatalog>(),
            liveTvPresenceProvider ?? Mock.Of<ILiveTvPresenceProvider>(),
            itemSortService ?? new NoOpItemSortService(),
            config ?? MakeConfig(),
            localizationManager ?? Mock.Of<ILocalizationManager>(),
            visibleContentProbe ?? new Mock<IVisibleContentProbe>(MockBehavior.Strict).Object,
            Mock.Of<IStandaloneVisibilityService>());
    }

    // ---------------------------------------------------------------
    // Basic pass-through, EnableFolderView, hidden/ordered preferences (RFC §6, verbatim reuse of
    // User.GetPreferenceValues/IsFolderGrouped - no new port).
    // ---------------------------------------------------------------

    [Fact]
    public void GetUserViews_PlainUngroupedFolder_PassedThroughUnchanged()
    {
        var user = MakeUser();
        var movies = new CollectionFolder { Id = Guid.NewGuid(), Name = "Movies", CollectionType = CollectionType.movies };
        var rootFolderProviderMock = new Mock<IUserRootFolderProvider>();
        rootFolderProviderMock.Setup(p => p.GetUserRootFolder()).Returns(MakeRoot(movies));

        var catalog = Build(userRootFolderProvider: rootFolderProviderMock.Object);

        var result = catalog.GetUserViews(new UserViewQuery { User = user, IncludeExternalContent = false });

        Assert.Same(movies, Assert.Single(result));
    }

    [Fact]
    public void GetUserViews_EnableFolderViewTrue_AddsFoldersNamedView()
    {
        var user = MakeUser();
        var rootFolderProviderMock = new Mock<IUserRootFolderProvider>();
        rootFolderProviderMock.Setup(p => p.GetUserRootFolder()).Returns(MakeRoot());

        var foldersView = new UserView { Id = Guid.NewGuid(), Name = "Folders", ViewType = CollectionType.folders };
        var userViewFactoryMock = new Mock<IUserViewFactory>();
        userViewFactoryMock.Setup(f => f.GetNamedView("Folders label", CollectionType.folders, string.Empty)).Returns(foldersView);

        var localizationMock = new Mock<ILocalizationManager>();
        localizationMock.Setup(l => l.GetLocalizedString("Folders")).Returns("Folders label");

        var catalog = Build(
            userRootFolderProvider: rootFolderProviderMock.Object,
            userViewFactory: userViewFactoryMock.Object,
            localizationManager: localizationMock.Object,
            config: MakeConfig(enableFolderView: true));

        var result = catalog.GetUserViews(new UserViewQuery { User = user, IncludeExternalContent = false });

        Assert.Same(foldersView, Assert.Single(result));
    }

    [Fact]
    public void GetUserViews_EnableFolderViewFalse_NoFoldersView()
    {
        var user = MakeUser();
        var rootFolderProviderMock = new Mock<IUserRootFolderProvider>();
        rootFolderProviderMock.Setup(p => p.GetUserRootFolder()).Returns(MakeRoot());

        var catalog = Build(userRootFolderProvider: rootFolderProviderMock.Object, config: MakeConfig(enableFolderView: false));

        var result = catalog.GetUserViews(new UserViewQuery { User = user, IncludeExternalContent = false });

        Assert.Empty(result);
    }

    [Fact]
    public void GetUserViews_IncludeHiddenFalse_ExcludesMyMediaExcludes()
    {
        var user = MakeUser();
        var visible = new CollectionFolder { Id = Guid.NewGuid(), Name = "Visible", CollectionType = CollectionType.music };
        var excluded = new CollectionFolder { Id = Guid.NewGuid(), Name = "Excluded", CollectionType = CollectionType.music };
        user.SetPreference(PreferenceKind.MyMediaExcludes, new[] { excluded.Id });

        var rootFolderProviderMock = new Mock<IUserRootFolderProvider>();
        rootFolderProviderMock.Setup(p => p.GetUserRootFolder()).Returns(MakeRoot(visible, excluded));

        var catalog = Build(userRootFolderProvider: rootFolderProviderMock.Object);

        var result = catalog.GetUserViews(new UserViewQuery { User = user, IncludeExternalContent = false, IncludeHidden = false });

        Assert.Same(visible, Assert.Single(result));
    }

    [Fact]
    public void GetUserViews_IncludeHiddenTrue_KeepsMyMediaExcludes()
    {
        var user = MakeUser();
        var excluded = new CollectionFolder { Id = Guid.NewGuid(), Name = "Excluded", CollectionType = CollectionType.music };
        user.SetPreference(PreferenceKind.MyMediaExcludes, new[] { excluded.Id });

        var rootFolderProviderMock = new Mock<IUserRootFolderProvider>();
        rootFolderProviderMock.Setup(p => p.GetUserRootFolder()).Returns(MakeRoot(excluded));

        var catalog = Build(userRootFolderProvider: rootFolderProviderMock.Object);

        var result = catalog.GetUserViews(new UserViewQuery { User = user, IncludeExternalContent = false, IncludeHidden = true });

        Assert.Same(excluded, Assert.Single(result));
    }

    [Fact]
    public void GetUserViews_OrderedViewsPreference_ControlsResultOrder()
    {
        var user = MakeUser();
        var a = new CollectionFolder { Id = Guid.NewGuid(), Name = "A", CollectionType = CollectionType.music };
        var b = new CollectionFolder { Id = Guid.NewGuid(), Name = "B", CollectionType = CollectionType.music };
        user.SetPreference(PreferenceKind.OrderedViews, new[] { b.Id, a.Id });

        var rootFolderProviderMock = new Mock<IUserRootFolderProvider>();
        rootFolderProviderMock.Setup(p => p.GetUserRootFolder()).Returns(MakeRoot(a, b));

        var catalog = Build(userRootFolderProvider: rootFolderProviderMock.Object);

        var result = catalog.GetUserViews(new UserViewQuery { User = user, IncludeExternalContent = false });

        Assert.Equal(new[] { b.Id, a.Id }, result.Select(f => f.Id));
    }

    // ---------------------------------------------------------------
    // Grouping (movies/tvshows merge into a single named view when the user has grouped them).
    // ---------------------------------------------------------------

    [Fact]
    public void GetUserViews_MultipleGroupedMovieFolders_MergedIntoSingleNamedView()
    {
        var user = MakeUser();
        var moviesA = new CollectionFolder { Id = Guid.NewGuid(), Name = "Movies A", CollectionType = CollectionType.movies };
        var moviesB = new CollectionFolder { Id = Guid.NewGuid(), Name = "Movies B", CollectionType = CollectionType.movies };
        user.SetPreference(PreferenceKind.GroupedFolders, new[] { moviesA.Id, moviesB.Id });

        var mergedView = new UserView { Id = Guid.NewGuid(), Name = "Movies", ViewType = CollectionType.movies };
        var userViewFactoryMock = new Mock<IUserViewFactory>();
        userViewFactoryMock
            .Setup(f => f.GetNamedView(user, "Movies label", CollectionType.movies, string.Empty))
            .Returns(mergedView);

        var localizationMock = new Mock<ILocalizationManager>();
        localizationMock.Setup(l => l.GetLocalizedString("Movies")).Returns("Movies label");

        var rootFolderProviderMock = new Mock<IUserRootFolderProvider>();
        rootFolderProviderMock.Setup(p => p.GetUserRootFolder()).Returns(MakeRoot(moviesA, moviesB));

        var catalog = Build(
            userRootFolderProvider: rootFolderProviderMock.Object,
            userViewFactory: userViewFactoryMock.Object,
            localizationManager: localizationMock.Object);

        var result = catalog.GetUserViews(new UserViewQuery { User = user, IncludeExternalContent = false });

        Assert.Same(mergedView, Assert.Single(result));
    }

    [Fact]
    public void GetUserViews_SingleGroupedMovieFolder_ReturnedDirectlyNotWrapped()
    {
        var user = MakeUser();
        var movies = new CollectionFolder { Id = Guid.NewGuid(), Name = "Movies", CollectionType = CollectionType.movies };
        user.SetPreference(PreferenceKind.GroupedFolders, new[] { movies.Id });

        var rootFolderProviderMock = new Mock<IUserRootFolderProvider>();
        rootFolderProviderMock.Setup(p => p.GetUserRootFolder()).Returns(MakeRoot(movies));

        var userViewFactoryMock = new Mock<IUserViewFactory>();

        var catalog = Build(userRootFolderProvider: rootFolderProviderMock.Object, userViewFactory: userViewFactoryMock.Object);

        var result = catalog.GetUserViews(new UserViewQuery { User = user, IncludeExternalContent = false });

        Assert.Same(movies, Assert.Single(result));
        userViewFactoryMock.Verify(f => f.GetNamedView(It.IsAny<User>(), It.IsAny<string>(), It.IsAny<CollectionType?>(), It.IsAny<string>()), Times.Never);
    }

    // ---------------------------------------------------------------
    // IsUserSpecific (playlists): always a per-user named view, regardless of grouping.
    // ---------------------------------------------------------------

    [Fact]
    public void GetUserViews_PlaylistsFolder_AlwaysNamedViewPerUser()
    {
        var user = MakeUser();
        var playlists = new CollectionFolder { Id = Guid.NewGuid(), Name = "Playlists", CollectionType = CollectionType.playlists };

        var namedView = new UserView { Id = Guid.NewGuid(), Name = "Playlists", ViewType = CollectionType.playlists, UserId = user.Id };
        var userViewFactoryMock = new Mock<IUserViewFactory>();
        userViewFactoryMock
            .Setup(f => f.GetNamedView(user, "Playlists", playlists.Id, CollectionType.playlists, null!))
            .Returns(namedView);

        var rootFolderProviderMock = new Mock<IUserRootFolderProvider>();
        rootFolderProviderMock.Setup(p => p.GetUserRootFolder()).Returns(MakeRoot(playlists));

        var catalog = Build(
            userRootFolderProvider: rootFolderProviderMock.Object,
            userViewFactory: userViewFactoryMock.Object,
            visibleContentProbe: MakeProbe(playlists, user, hasVisibleContent: true));

        var result = catalog.GetUserViews(new UserViewQuery { User = user, IncludeExternalContent = false });

        Assert.Same(namedView, Assert.Single(result));
    }

    [Fact]
    public void GetUserViews_PlaylistsFolder_ProbeAllInvisible_FolderExcluded()
    {
        var user = MakeUser();
        var playlists = new CollectionFolder { Id = Guid.NewGuid(), Name = "Playlists", CollectionType = CollectionType.playlists };

        var rootFolderProviderMock = new Mock<IUserRootFolderProvider>();
        rootFolderProviderMock.Setup(p => p.GetUserRootFolder()).Returns(MakeRoot(playlists));

        var userViewFactoryMock = new Mock<IUserViewFactory>();

        var catalog = Build(
            userRootFolderProvider: rootFolderProviderMock.Object,
            userViewFactory: userViewFactoryMock.Object,
            visibleContentProbe: MakeProbe(playlists, user, hasVisibleContent: false));

        var result = catalog.GetUserViews(new UserViewQuery { User = user, IncludeExternalContent = false });

        Assert.Empty(result);
        userViewFactoryMock.Verify(
            f => f.GetNamedView(It.IsAny<User>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CollectionType?>(), It.IsAny<string>()),
            Times.Never);
    }

    // ---------------------------------------------------------------
    // Channels / LiveTV toggles (RFC §4/§5 leaves).
    // ---------------------------------------------------------------

    [Fact]
    public void GetUserViews_IncludeExternalContentFalse_SkipsChannelsAndLiveTv()
    {
        var user = MakeUser();
        var rootFolderProviderMock = new Mock<IUserRootFolderProvider>();
        rootFolderProviderMock.Setup(p => p.GetUserRootFolder()).Returns(MakeRoot());

        var channelCatalogMock = new Mock<IChannelCatalog>();
        var liveTvMock = new Mock<ILiveTvPresenceProvider>();

        var catalog = Build(
            userRootFolderProvider: rootFolderProviderMock.Object,
            channelCatalog: channelCatalogMock.Object,
            liveTvPresenceProvider: liveTvMock.Object);

        var result = catalog.GetUserViews(new UserViewQuery { User = user, IncludeExternalContent = false });

        Assert.Empty(result);
        channelCatalogMock.Verify(c => c.GetChannelsAsync(It.IsAny<ChannelQuery>()), Times.Never);
        liveTvMock.Verify(l => l.GetEnabledUsers(), Times.Never);
    }

    [Fact]
    public void GetUserViews_IncludeExternalContentTrue_AddsChannelsAndLiveTvFolderWhenEnabled()
    {
        var user = MakeUser();
        var rootFolderProviderMock = new Mock<IUserRootFolderProvider>();
        rootFolderProviderMock.Setup(p => p.GetUserRootFolder()).Returns(MakeRoot());

        var channel = new Channel { Id = Guid.NewGuid(), Name = "News" };
        var channelCatalogMock = new Mock<IChannelCatalog>();
        channelCatalogMock
            .Setup(c => c.GetChannelsAsync(It.Is<ChannelQuery>(q => q.UserId.Equals(user.Id))))
            .ReturnsAsync(new QueryResult<Channel>(0, 1, new[] { channel }));

        var liveTvFolder = new UserView { Id = Guid.NewGuid(), Name = "Live TV", ViewType = CollectionType.livetv };
        var liveTvMock = new Mock<ILiveTvPresenceProvider>();
        liveTvMock.Setup(l => l.GetEnabledUsers()).Returns(new[] { user });
        liveTvMock.Setup(l => l.GetLiveTvFolder(It.IsAny<CancellationToken>())).Returns(liveTvFolder);

        var catalog = Build(
            userRootFolderProvider: rootFolderProviderMock.Object,
            channelCatalog: channelCatalogMock.Object,
            liveTvPresenceProvider: liveTvMock.Object);

        var result = catalog.GetUserViews(new UserViewQuery { User = user, IncludeExternalContent = true });

        Assert.Equal(2, result.Length);
        Assert.Contains(result, f => f.Id.Equals(channel.Id));
        Assert.Contains(result, f => f.Id.Equals(liveTvFolder.Id));
    }

    [Fact]
    public void GetUserViews_LiveTvNotEnabledForUser_NoLiveTvFolderAdded()
    {
        var user = MakeUser();
        var rootFolderProviderMock = new Mock<IUserRootFolderProvider>();
        rootFolderProviderMock.Setup(p => p.GetUserRootFolder()).Returns(MakeRoot());

        var channelCatalogMock = new Mock<IChannelCatalog>();
        channelCatalogMock
            .Setup(c => c.GetChannelsAsync(It.IsAny<ChannelQuery>()))
            .ReturnsAsync(new QueryResult<Channel>(0, 0, Array.Empty<Channel>()));

        var liveTvMock = new Mock<ILiveTvPresenceProvider>();
        liveTvMock.Setup(l => l.GetEnabledUsers()).Returns(Array.Empty<User>());

        var catalog = Build(
            userRootFolderProvider: rootFolderProviderMock.Object,
            channelCatalog: channelCatalogMock.Object,
            liveTvPresenceProvider: liveTvMock.Object);

        var result = catalog.GetUserViews(new UserViewQuery { User = user, IncludeExternalContent = true });

        Assert.Empty(result);
        liveTvMock.Verify(l => l.GetLiveTvFolder(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------------------------------------------------------------
    // The probe is only asked about playlists/boxsets libraries.
    // ---------------------------------------------------------------

    [Fact]
    public void GetUserViews_NoPlaylistOrBoxsetFolder_ProbeNeverAsked()
    {
        var user = MakeUser();
        var movies = new CollectionFolder { Id = Guid.NewGuid(), Name = "Movies", CollectionType = CollectionType.movies };

        var rootFolderProviderMock = new Mock<IUserRootFolderProvider>();
        rootFolderProviderMock.Setup(p => p.GetUserRootFolder()).Returns(MakeRoot(movies));

        // Strict with no setup: any call throws.
        var catalog = Build(userRootFolderProvider: rootFolderProviderMock.Object);

        Assert.Same(movies, Assert.Single(catalog.GetUserViews(new UserViewQuery { User = user, IncludeExternalContent = false })));
    }

    // ---------------------------------------------------------------
    // RFC I1 (§8): no constructor parameter of UserViewCatalog or VisibleContentProbe is a member of
    // the former SCC or IItemQueryService, plain or deferred. The transitive proof over the real
    // registrations lives in Tesserafin.Server.Integration.Tests (UserViewCatalogDependencyTests).
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(typeof(UserViewCatalog))]
    [InlineData(typeof(VisibleContentProbe))]
    public void DiWiring_ConstructorTakesNoSccMemberOrItemQueryService_PlainOrDeferred(Type type)
    {
        var forbiddenTypes = new[]
        {
            typeof(ILibraryManager),
            typeof(IUserViewManager),
            typeof(IChannelManager),
            typeof(ILiveTvManager),
            typeof(IItemQueryService),
            typeof(ItemQueryService),
            typeof(IServiceProvider)
        };

        var parameterTypes = type.GetConstructors().Single().GetParameters()
            .Select(p => p.ParameterType)
            .Select(t => t.IsGenericType ? t.GetGenericArguments()[0] : t)
            .ToArray();

        foreach (var forbidden in forbiddenTypes)
        {
            Assert.DoesNotContain(forbidden, parameterTypes);
        }
    }

    [Fact]
    public void DiWiring_ApplicationHostStyleRegistration_UserViewCatalogResolvesWithoutItemQueryService()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IUserRootFolderProvider>());
        services.AddSingleton(Mock.Of<IUserViewFactory>());
        services.AddSingleton(Mock.Of<IChannelCatalog>());
        services.AddSingleton(Mock.Of<ILiveTvPresenceProvider>());
        services.AddSingleton<IItemSortService>(new NoOpItemSortService());
        services.AddSingleton(MakeConfig());
        services.AddSingleton(Mock.Of<ILocalizationManager>());
        services.AddSingleton(Mock.Of<IItemLookupService>());
        services.AddSingleton(Mock.Of<IItemRepository>());
        services.AddSingleton(Mock.Of<IStandaloneVisibilityService>());
        services.AddSingleton<IVisibleContentProbe, VisibleContentProbe>();
        services.AddSingleton<IUserViewCatalog, UserViewCatalog>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        var first = provider.GetRequiredService<IUserViewCatalog>();

        Assert.IsType<UserViewCatalog>(first);
        Assert.Same(first, provider.GetRequiredService<IUserViewCatalog>());
        Assert.IsType<VisibleContentProbe>(provider.GetRequiredService<IVisibleContentProbe>());
    }

    // ---------------------------------------------------------------
    // Test doubles.
    // ---------------------------------------------------------------

    /// <summary>
    /// Hand-written <see cref="Folder"/> double whose <see cref="GetChildren(User, bool, InternalItemsQuery, IItemSortService)"/>
    /// returns a fixed set of children regardless of arguments - used as the user root folder stand-in
    /// so tests control the exact children list without needing a real item repository/cache.
    /// </summary>
    private sealed class FakeChildrenFolder : Folder
    {
        private readonly IReadOnlyList<BaseItem> _children;

        public FakeChildrenFolder(params Folder[] children)
        {
            _children = children;
        }

        public override IReadOnlyList<BaseItem> GetChildren(User user, bool includeLinkedChildren, InternalItemsQuery query, IItemSortService itemSortService)
        {
            return _children;
        }
    }

    /// <summary>
    /// <see cref="IItemSortService"/> double with no registered comparers - <c>Sort</c> is then the
    /// identity function (matches <c>ItemSortService</c>'s own behavior when
    /// <c>AddParts</c> was never called), keeping ordering fully controlled by test input order.
    /// </summary>
    private sealed class NoOpItemSortService : IItemSortService
    {
        public void AddParts(IEnumerable<IBaseItemComparer> itemComparers)
        {
        }

        public IEnumerable<BaseItem> Sort(IEnumerable<BaseItem> items, User? user, IEnumerable<ItemSortBy> sortBy, SortOrder sortOrder) => items;

        public IEnumerable<BaseItem> Sort(IEnumerable<BaseItem> items, User? user, IEnumerable<(ItemSortBy OrderBy, SortOrder SortOrder)> orderBy) => items;
    }
}
