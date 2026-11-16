using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Collections;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Sorting;
using Tesserafin.Controller.TV;
using Tesserafin.Data;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Channels;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.IO;
using Tesserafin.Model.Library;
using Tesserafin.Model.Querying;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

/// <summary>
/// Parity tests: <see cref="UserViewCatalog.GetUserViews"/> against the real
/// <c>UserViewManager.GetUserViews</c> (RFC <c>docs/rfc-di-query-user-views-v2.md</c> §9, PR109).
/// </summary>
/// <remarks>
/// <para>
/// Both sides share the exact same <see cref="Tesserafin.Server.Core.Library.UserViewFactory"/> instance
/// (itself built on a real, shared <see cref="Tesserafin.Server.Core.Library.ItemLookupService"/> /
/// <see cref="Tesserafin.Server.Core.Library.ItemStore"/> pair, exactly like
/// <c>LibraryManagerUserViewFactoryTests</c>, PR106b) - the manager side reaches it through an
/// <see cref="ILibraryManager"/> mock whose <c>GetNamedView</c>/<c>GetShadowView</c> overloads
/// forward one-for-one to the shared factory, so named/shadow views created by either side are
/// <em>literally the same objects</em> (same deterministic id), not merely equal by value. Channel and
/// Live TV results are similarly driven by shared provider functions so both sides observe the same
/// entities. <c>IItemSortService</c> is a real, shared
/// <see cref="Tesserafin.Server.Core.Sorting.ItemSortService"/> instance with no comparers registered -
/// its <c>Sort</c> is then the identity function on both sides, so the final ordering in
/// <see cref="UserViewCatalog.GetUserViews"/>/<c>UserViewManager.GetUserViews</c> is driven entirely
/// by <c>OrderedViews</c> and input order, both fully controlled by the test.
/// </para>
/// <para>
/// <b>Probe</b>: both sides share one <see cref="IVisibleContentProbe"/> double answering from the
/// <see cref="ProbeFolder"/>'s directly controlled items; the real probe is characterised against the
/// historical computation in <see cref="VisibleContentProbeTests"/>.
/// </para>
/// </remarks>
[Collection(Tesserafin.Server.Implementations.Tests.Library.LibraryManager.LibraryManagerStaticStateFixture.Name)]
public sealed class UserViewCatalogParityTests
{
    private static User MakeUser(string name = "alice")
    {
        return new User(name, "auth", "reset") { Id = Guid.NewGuid() };
    }

    [Fact]
    public void GetUserViews_StandardLibraries_Ungrouped_SameResultAsUserViewManager()
    {
        var user = MakeUser();
        var fixture = new Fixture();

        var movies = new CollectionFolder { Id = Guid.NewGuid(), Name = "Movies", CollectionType = CollectionType.movies };
        var tvShows = new CollectionFolder { Id = Guid.NewGuid(), Name = "TV Shows", CollectionType = CollectionType.tvshows };
        var music = new CollectionFolder { Id = Guid.NewGuid(), Name = "Music", CollectionType = CollectionType.music };

        var manager = fixture.BuildManager(fixture.MakeRoot(movies, tvShows, music));
        var catalog = fixture.BuildCatalog(fixture.MakeRoot(movies, tvShows, music));

        AssertSameResult(manager, catalog, user, includeExternalContent: false);
    }

    [Fact]
    public void GetUserViews_GroupingOn_MultipleMovieFolders_SameResultAsUserViewManager()
    {
        var user = MakeUser();
        var fixture = new Fixture();

        var moviesA = new CollectionFolder { Id = Guid.NewGuid(), Name = "Movies A", CollectionType = CollectionType.movies };
        var moviesB = new CollectionFolder { Id = Guid.NewGuid(), Name = "Movies B", CollectionType = CollectionType.movies };
        user.SetPreference(PreferenceKind.GroupedFolders, new[] { moviesA.Id, moviesB.Id });

        var manager = fixture.BuildManager(fixture.MakeRoot(moviesA, moviesB));
        var catalog = fixture.BuildCatalog(fixture.MakeRoot(moviesA, moviesB));

        AssertSameResult(manager, catalog, user, includeExternalContent: false);
    }

    [Fact]
    public void GetUserViews_GroupingOff_MultipleMovieFolders_SameResultAsUserViewManager()
    {
        var user = MakeUser();
        var fixture = new Fixture();

        var moviesA = new CollectionFolder { Id = Guid.NewGuid(), Name = "Movies A", CollectionType = CollectionType.movies };
        var moviesB = new CollectionFolder { Id = Guid.NewGuid(), Name = "Movies B", CollectionType = CollectionType.movies };
        // No GroupedFolders preference set - both folders pass through independently.

        var manager = fixture.BuildManager(fixture.MakeRoot(moviesA, moviesB));
        var catalog = fixture.BuildCatalog(fixture.MakeRoot(moviesA, moviesB));

        AssertSameResult(manager, catalog, user, includeExternalContent: false);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetUserViews_EnableFolderView_SameResultAsUserViewManager(bool enableFolderView)
    {
        var user = MakeUser();
        var fixture = new Fixture(enableFolderView);

        var movies = new CollectionFolder { Id = Guid.NewGuid(), Name = "Movies", CollectionType = CollectionType.movies };

        var manager = fixture.BuildManager(fixture.MakeRoot(movies));
        var catalog = fixture.BuildCatalog(fixture.MakeRoot(movies));

        AssertSameResult(manager, catalog, user, includeExternalContent: false);
    }

    [Fact]
    public void GetUserViews_IncludeHiddenFalse_MyMediaExcludes_SameResultAsUserViewManager()
    {
        var user = MakeUser();
        var fixture = new Fixture();

        var visible = new CollectionFolder { Id = Guid.NewGuid(), Name = "Visible", CollectionType = CollectionType.music };
        var excluded = new CollectionFolder { Id = Guid.NewGuid(), Name = "Excluded", CollectionType = CollectionType.music };
        user.SetPreference(PreferenceKind.MyMediaExcludes, new[] { excluded.Id });

        var manager = fixture.BuildManager(fixture.MakeRoot(visible, excluded));
        var catalog = fixture.BuildCatalog(fixture.MakeRoot(visible, excluded));

        AssertSameResult(manager, catalog, user, includeExternalContent: false, includeHidden: false);
    }

    [Fact]
    public void GetUserViews_OrderedViewsCustomOrder_SameResultAsUserViewManager()
    {
        var user = MakeUser();
        var fixture = new Fixture();

        var a = new CollectionFolder { Id = Guid.NewGuid(), Name = "A", CollectionType = CollectionType.music };
        var b = new CollectionFolder { Id = Guid.NewGuid(), Name = "B", CollectionType = CollectionType.music };
        var c = new CollectionFolder { Id = Guid.NewGuid(), Name = "C", CollectionType = CollectionType.music };
        user.SetPreference(PreferenceKind.OrderedViews, new[] { c.Id, a.Id, b.Id });

        var manager = fixture.BuildManager(fixture.MakeRoot(a, b, c));
        var catalog = fixture.BuildCatalog(fixture.MakeRoot(a, b, c));

        AssertSameResult(manager, catalog, user, includeExternalContent: false);
    }

    // ---------------------------------------------------------------
    // Playlists/boxsets libraries: listed or left out on the probe's answer, identically on both
    // sides (the probe itself is characterised in VisibleContentProbeTests).
    // ---------------------------------------------------------------

    [Fact]
    public void GetUserViews_PlaylistsLibrary_ProbeVisibleItem_IncludedOnBothSides()
    {
        var user = MakeUser();
        var fixture = new Fixture();

        var playlists = fixture.MakeProbeFolder("Playlists", CollectionType.playlists, visible: true);

        var manager = fixture.BuildManager(fixture.MakeRoot(playlists));
        var catalog = fixture.BuildCatalog(fixture.MakeRoot(playlists));

        var managerResult = manager.GetUserViews(new UserViewQuery { User = user, IncludeExternalContent = false });
        var catalogResult = catalog.GetUserViews(new UserViewQuery { User = user, IncludeExternalContent = false });

        // Playlists are IsUserSpecific -> always a per-user named view when the probe includes it.
        Assert.Single(managerResult);
        Assert.Single(catalogResult);
        Assert.Equal(managerResult[0].Id, catalogResult[0].Id);
        Assert.IsType<UserView>(managerResult[0]);
    }

    [Fact]
    public void GetUserViews_PlaylistsLibrary_ProbeAllInvisible_ExcludedOnBothSides()
    {
        var user = MakeUser();
        var fixture = new Fixture();

        var playlists = fixture.MakeProbeFolder("Playlists", CollectionType.playlists, visible: false);

        var manager = fixture.BuildManager(fixture.MakeRoot(playlists));
        var catalog = fixture.BuildCatalog(fixture.MakeRoot(playlists));

        var managerResult = manager.GetUserViews(new UserViewQuery { User = user, IncludeExternalContent = false });
        var catalogResult = catalog.GetUserViews(new UserViewQuery { User = user, IncludeExternalContent = false });

        Assert.Empty(managerResult);
        Assert.Empty(catalogResult);
    }

    [Fact]
    public void GetUserViews_BoxsetsLibrary_ProbeVisibleItem_IncludedOnBothSides()
    {
        var user = MakeUser();
        var fixture = new Fixture();

        var boxsets = fixture.MakeProbeFolder("Boxsets", CollectionType.boxsets, visible: true);

        var manager = fixture.BuildManager(fixture.MakeRoot(boxsets));
        var catalog = fixture.BuildCatalog(fixture.MakeRoot(boxsets));

        AssertSameResult(manager, catalog, user, includeExternalContent: false);

        var managerResult = manager.GetUserViews(new UserViewQuery { User = user, IncludeExternalContent = false });
        Assert.Same(boxsets, Assert.Single(managerResult));
    }

    [Fact]
    public void GetUserViews_BoxsetsLibrary_ProbeAllInvisible_ExcludedOnBothSides()
    {
        var user = MakeUser();
        var fixture = new Fixture();

        var boxsets = fixture.MakeProbeFolder("Boxsets", CollectionType.boxsets, visible: false);

        var manager = fixture.BuildManager(fixture.MakeRoot(boxsets));
        var catalog = fixture.BuildCatalog(fixture.MakeRoot(boxsets));

        var managerResult = manager.GetUserViews(new UserViewQuery { User = user, IncludeExternalContent = false });
        var catalogResult = catalog.GetUserViews(new UserViewQuery { User = user, IncludeExternalContent = false });

        Assert.Empty(managerResult);
        Assert.Empty(catalogResult);
    }

    // ---------------------------------------------------------------
    // Channels / LiveTV (RFC §4/§5).
    // ---------------------------------------------------------------

    [Fact]
    public void GetUserViews_ChannelsPresent_SameResultAsUserViewManager()
    {
        var user = MakeUser();
        var fixture = new Fixture();
        var channel = new Channel { Id = Guid.NewGuid(), Name = "News", ForcedSortName = "News" };
        fixture.ChannelsProvider = q => Task.FromResult(new QueryResult<Channel>(0, 1, new[] { channel }));

        var manager = fixture.BuildManager(fixture.MakeRoot());
        var catalog = fixture.BuildCatalog(fixture.MakeRoot());

        AssertSameResult(manager, catalog, user, includeExternalContent: true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetUserViews_LiveTvEnabled_SameResultAsUserViewManager(bool liveTvEnabled)
    {
        var user = MakeUser();
        var fixture = new Fixture();
        var liveTvFolder = new UserView { Id = Guid.NewGuid(), Name = "Live TV", ViewType = CollectionType.livetv };
        fixture.EnabledUsersProvider = () => liveTvEnabled ? new[] { user } : Array.Empty<User>();
        fixture.LiveTvFolderProvider = _ => liveTvFolder;

        var manager = fixture.BuildManager(fixture.MakeRoot());
        var catalog = fixture.BuildCatalog(fixture.MakeRoot());

        AssertSameResult(manager, catalog, user, includeExternalContent: true);
    }

    [Fact]
    public void GetUserViews_MixComplete_StandardLibrariesGroupingProbeChannelsLiveTv_SameResultAsUserViewManager()
    {
        var user = MakeUser();
        var fixture = new Fixture(enableFolderView: true);

        var moviesA = new CollectionFolder { Id = Guid.NewGuid(), Name = "Movies A", CollectionType = CollectionType.movies };
        var moviesB = new CollectionFolder { Id = Guid.NewGuid(), Name = "Movies B", CollectionType = CollectionType.movies };
        var music = new CollectionFolder { Id = Guid.NewGuid(), Name = "Music", CollectionType = CollectionType.music };
        var playlists = fixture.MakeProbeFolder("Playlists", CollectionType.playlists, visible: true);
        var boxsets = fixture.MakeProbeFolder("Boxsets", CollectionType.boxsets, visible: false);

        user.SetPreference(PreferenceKind.GroupedFolders, new[] { moviesA.Id, moviesB.Id });
        user.SetPreference(PreferenceKind.MyMediaExcludes, new[] { music.Id });

        var channel = new Channel { Id = Guid.NewGuid(), Name = "News", ForcedSortName = "News" };
        fixture.ChannelsProvider = q => Task.FromResult(new QueryResult<Channel>(0, 1, new[] { channel }));

        var liveTvFolder = new UserView { Id = Guid.NewGuid(), Name = "Live TV", ViewType = CollectionType.livetv };
        fixture.EnabledUsersProvider = () => new[] { user };
        fixture.LiveTvFolderProvider = _ => liveTvFolder;

        var root = fixture.MakeRoot(moviesA, moviesB, music, playlists, boxsets);
        var manager = fixture.BuildManager(root);
        // Boxsets is excluded by the probe (invisible) on both sides, so a fresh root with the same
        // child instances is fine for the catalog side too - no state is mutated by GetUserViews.
        var catalog = fixture.BuildCatalog(fixture.MakeRoot(moviesA, moviesB, music, playlists, boxsets));

        AssertSameResult(manager, catalog, user, includeExternalContent: true, includeHidden: false);
    }

    private static void AssertSameResult(
        UserViewManager manager,
        UserViewCatalog catalog,
        User user,
        bool includeExternalContent,
        bool includeHidden = true)
    {
        var managerResult = manager.GetUserViews(new UserViewQuery { User = user, IncludeExternalContent = includeExternalContent, IncludeHidden = includeHidden });
        var catalogResult = catalog.GetUserViews(new UserViewQuery { User = user, IncludeExternalContent = includeExternalContent, IncludeHidden = includeHidden });

        Assert.Equal(managerResult.Select(f => f.Id), catalogResult.Select(f => f.Id));
        Assert.Equal(managerResult.Select(f => f.GetType()), catalogResult.Select(f => f.GetType()));
    }

    /// <summary>
    /// Shared world: a single, real <see cref="Tesserafin.Server.Core.Library.UserViewFactory"/> (built
    /// on a real, shared <see cref="Tesserafin.Server.Core.Library.ItemLookupService"/>/
    /// <see cref="Tesserafin.Server.Core.Library.ItemStore"/> pair) backs both the manager side's
    /// <see cref="ILibraryManager"/> mock (via forwarding setups) and the catalog side's
    /// <see cref="IUserViewFactory"/> directly - see the type-level remarks for why this is
    /// deliberate, not incidental.
    /// </summary>
    private sealed class Fixture
    {
        public Fixture(bool enableFolderView = false)
        {
            var internalMetadataPath = Path.Combine(Path.GetTempPath(), "reefin-uvc-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(internalMetadataPath);

            ConfigMock = new Mock<IServerConfigurationManager>();
            ConfigMock.Setup(c => c.ApplicationPaths.ProgramDataPath).Returns("/data");
            ConfigMock.Setup(c => c.ApplicationPaths.InternalMetadataPath).Returns(internalMetadataPath);
            ConfigMock.Setup(c => c.Configuration).Returns(new ServerConfiguration { EnableFolderView = enableFolderView });
            Config = ConfigMock.Object;
            Tesserafin.Controller.Entities.BaseItem.ConfigurationManager = Config;

            // UserViewFactory.GetNamedView(string, CollectionType, string) - the "Folders" view's
            // overload (RFC PR106b) - calls item.UpdateToRepositoryAsync(...) on its new-item path,
            // which reaches the static BaseItem.LibraryManager fallback (BaseItem.cs:2370):
            // pre-existing behavior, reproduced verbatim by UserViewFactory (see its type-level
            // remarks and LibraryManagerUserViewFactoryTests' identical setup for the same overload).
            Tesserafin.Controller.Entities.BaseItem.LibraryManager = Mock.Of<ILibraryManager>();
            Tesserafin.Controller.Entities.BaseItem.MediaSourceManager = Mock.Of<IMediaSourceManager>();

            var localizationMock = new Mock<ILocalizationManager>();
            localizationMock.Setup(l => l.GetLocalizedString("Movies")).Returns("Movies");
            localizationMock.Setup(l => l.GetLocalizedString("TvShows")).Returns("TV Shows");
            localizationMock.Setup(l => l.GetLocalizedString("Folders")).Returns("Folders");
            Localization = localizationMock.Object;

            CollectionManager = Mock.Of<ICollectionManager>();
            TvSeriesManager = Mock.Of<ITVSeriesManager>();
            ItemSortService = new Tesserafin.Server.Core.Sorting.ItemSortService(Mock.Of<IUserManager>(), Mock.Of<IUserDataManager>());

            var itemRepositoryMock = new Mock<IItemRepository>();
            itemRepositoryMock.Setup(i => i.RetrieveItem(It.IsAny<Guid>())).Returns<BaseItem>(null);
            var persistenceServiceMock = new Mock<IItemPersistenceService>();

            var itemLookupService = new Tesserafin.Server.Core.Library.ItemLookupService(itemRepositoryMock.Object, ConfigMock.Object);
            var itemStore = new Tesserafin.Server.Core.Library.ItemStore(
                persistenceServiceMock.Object,
                itemLookupService,
                ConfigMock.Object,
                Mock.Of<IItemChangePublisher>());

            var fileSystemMock = new Mock<IFileSystem>();
            fileSystemMock.Setup(f => f.GetValidFilename(It.IsAny<string>())).Returns<string>(s => s);

            UserViewFactory = new Tesserafin.Server.Core.Library.UserViewFactory(
                Mock.Of<IItemWriteCommands>(),
                itemLookupService,
                itemStore,
                ConfigMock.Object,
                fileSystemMock.Object,
                new Lazy<IProviderManager>(() => Mock.Of<IProviderManager>()));
        }

        public IServerConfigurationManager Config { get; }

        public Mock<IServerConfigurationManager> ConfigMock { get; }

        public ILocalizationManager Localization { get; }

        public ICollectionManager CollectionManager { get; }

        public ITVSeriesManager TvSeriesManager { get; }

        public IItemSortService ItemSortService { get; }

        public Tesserafin.Server.Core.Library.UserViewFactory UserViewFactory { get; }

        public Func<ChannelQuery, Task<QueryResult<Channel>>> ChannelsProvider { get; set; } =
            _ => Task.FromResult(new QueryResult<Channel>(0, 0, Array.Empty<Channel>()));

        public Func<IEnumerable<User>> EnabledUsersProvider { get; set; } = () => Array.Empty<User>();

        public Func<CancellationToken, Folder> LiveTvFolderProvider { get; set; } =
            _ => throw new InvalidOperationException("LiveTvFolderProvider invoked with no enabled users configured.");

        public Folder MakeRoot(params Folder[] children) => new FixtureRootFolder(children);

        /// <summary>
        /// Builds a playlists/boxsets library folder whose probe items are directly controlled
        /// (RFC §3) - see <see cref="ProbeFolder"/>.
        /// </summary>
        public Folder MakeProbeFolder(string name, CollectionType collectionType, bool visible)
        {
            var item = new ProbeVideo { Id = Guid.NewGuid(), Name = "probe-item", Visible = visible };
            return new ProbeFolder(item) { Id = Guid.NewGuid(), Name = name, CollectionType = collectionType };
        }

        /// <summary>
        /// <b>PR110</b>: <c>UserViewManager.GetUserViews</c> now delegates entirely to
        /// <see cref="IUserViewCatalog"/> - the manager side is built on the exact same catalog
        /// construction as <see cref="BuildCatalog"/> (shared <see cref="UserViewFactory"/>, same
        /// channel/Live TV provider functions, same probe double),
        /// so these parity tests are now trivially green by construction (the point: they still prove
        /// the façade delegates without duplicating the listing logic, RFC §9/PR110 exit criterion
        /// "one real GetUserViews implementation"). <see cref="ILibraryManager"/> is kept as an inert
        /// mock purely to satisfy <see cref="UserViewManager"/>'s ctor (used only by
        /// <c>GetLatestItems</c>, out of scope for <c>GetUserViews</c> parity).
        /// </summary>
        public UserViewManager BuildManager(Folder root)
        {
            var catalog = BuildCatalog(root);

            var libraryManagerMock = new Mock<ILibraryManager>();

            var channelManagerMock = new Mock<IChannelManager>();
            channelManagerMock
                .Setup(c => c.GetChannelsInternalAsync(It.IsAny<ChannelQuery>()))
                .Returns((ChannelQuery q) => ChannelsProvider(q));

            return new UserViewManager(
                libraryManagerMock.Object,
                Localization,
                channelManagerMock.Object,
                ItemSortService,
                catalog,
                UserViewFactory);
        }

        public UserViewCatalog BuildCatalog(Folder root)
        {
            var rootProviderMock = new Mock<IUserRootFolderProvider>();
            rootProviderMock.Setup(p => p.GetUserRootFolder()).Returns(root);

            var channelCatalogMock = new Mock<IChannelCatalog>();
            channelCatalogMock
                .Setup(c => c.GetChannelsAsync(It.IsAny<ChannelQuery>()))
                .Returns((ChannelQuery q) => ChannelsProvider(q));

            var liveTvPresenceMock = new Mock<ILiveTvPresenceProvider>();
            liveTvPresenceMock.Setup(l => l.GetEnabledUsers()).Returns(() => EnabledUsersProvider());
            liveTvPresenceMock
                .Setup(l => l.GetLiveTvFolder(It.IsAny<CancellationToken>()))
                .Returns((CancellationToken ct) => LiveTvFolderProvider(ct));

            var probeMock = new Mock<IVisibleContentProbe>();
            probeMock
                .Setup(p => p.HasVisibleContent(It.IsAny<Folder>(), It.IsAny<User>()))
                .Returns((Folder library, User user) => library is ProbeFolder probeFolder && probeFolder.Items.Any(item => item.IsVisible(user)));

            return new UserViewCatalog(
                rootProviderMock.Object,
                UserViewFactory,
                channelCatalogMock.Object,
                liveTvPresenceMock.Object,
                ItemSortService,
                ConfigMock.Object,
                Localization,
                probeMock.Object);
        }
    }

    /// <summary>
    /// Root folder double whose <see cref="GetChildren(User, bool, InternalItemsQuery, IItemSortService)"/>
    /// returns a fixed set of children regardless of arguments (mirrors
    /// <c>UserViewCatalogTests.FakeChildrenFolder</c>).
    /// </summary>
    private sealed class FixtureRootFolder : Folder
    {
        private readonly IReadOnlyList<BaseItem> _children;

        public FixtureRootFolder(params Folder[] children)
        {
            _children = children;
        }

        public override IReadOnlyList<BaseItem> GetChildren(User user, bool includeLinkedChildren, InternalItemsQuery query, IItemSortService itemSortService)
        {
            return _children;
        }
    }

    /// <summary>
    /// Playlists/boxsets library folder double carrying the items the shared probe double answers from.
    /// </summary>
    private sealed class ProbeFolder : CollectionFolder
    {
        public ProbeFolder(params BaseItem[] items)
        {
            Items = items;
        }

        public IReadOnlyList<BaseItem> Items { get; }
    }

    /// <summary>
    /// Minimal <see cref="Video"/> double with a directly-controlled <see cref="IsVisible"/> result.
    /// </summary>
    private sealed class ProbeVideo : Video
    {
        public bool Visible { get; init; }

        public override bool IsVisible(User user, bool skipAllowedTagsCheck, ITagVisibilityService? tagVisibility) => Visible;
    }
}
