using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Collections;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Playlists;
using Tesserafin.Controller.Sorting;
using Tesserafin.Controller.TV;
using Tesserafin.Data;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.IO;
using Tesserafin.Model.Library;
using Tesserafin.Server.Core.Library;
using Tesserafin.Server.Core.Playlists;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

/// <summary>
/// Characterisation of the playlists/boxsets probe that decides whether <see cref="UserViewCatalog"/>
/// lists such a library: every scenario is run through both the historical computation
/// (<c>ItemQueryService.GetItemList(library, new InternalItemsQuery(user) { ParentId = library.ParentId })
/// .Any(item =&gt; item.IsVisible(user))</c>, on a real <see cref="ItemQueryService"/>) and
/// <see cref="VisibleContentProbe"/>, and both are asserted against a hand-derived expectation, over
/// real <see cref="CollectionFolder"/>, <see cref="PlaylistsFolder"/>, <see cref="Playlist"/> and
/// <see cref="BoxSet"/> entities.
/// </summary>
/// <remarks>
/// The two library types are the only <see cref="ICollectionFolder"/> implementations in production,
/// and they take different routes through <see cref="ItemQueryService"/>: a
/// <see cref="CollectionFolder"/> takes the raw-query fast path (its children are the children of its
/// physical folders), while <see cref="PlaylistsFolder"/> reports <c>SupportsRawQueryItems = false</c>
/// and goes through <c>Folder.GetItemList</c> (its children are its own <see cref="Playlist"/>
/// children). Both end in "is any eligible or linked child visible to this user".
/// <see cref="VisibleContentProbe"/> must get there through its injected lookup and repository only:
/// each run also asserts it never reads the static <see cref="BaseItem.ItemRepository"/>, and reaches
/// the static <see cref="BaseItem.LibraryManager"/> only from inside <c>IsVisible</c> (tag inheritance
/// and rating country of a real item - the visibility rule itself, unchanged).
/// </remarks>
[Collection(Tesserafin.Server.Implementations.Tests.Library.LibraryManager.LibraryManagerStaticStateFixture.Name)]
public sealed class VisibleContentProbeTests
{
    private static User MakeUser(string name) => new(name, "auth", "reset") { Id = Guid.NewGuid() };

    [Fact]
    public void BoxsetsLibrary_NoPhysicalFolder_Hidden()
    {
        var world = new World();
        var library = world.AddBoxsetsLibrary();

        world.AssertProbe(library, MakeUser("a"), expected: false);
    }

    [Fact]
    public void BoxsetsLibrary_EmptyPhysicalFolder_Hidden()
    {
        var world = new World();
        var library = world.AddBoxsetsLibrary(world.AddPhysicalFolder("/data/collections"));

        world.AssertProbe(library, MakeUser("a"), expected: false);
    }

    [Fact]
    public void BoxsetsLibrary_SingleVisibleItem_Listed()
    {
        var world = new World();
        var physical = world.AddPhysicalFolder("/data/collections");
        world.AddChild(physical, new FlagBoxSet(visible: true));
        var library = world.AddBoxsetsLibrary(physical);

        world.AssertProbe(library, MakeUser("a"), expected: true);
    }

    [Fact]
    public void BoxsetsLibrary_OnlyForbiddenItems_Hidden()
    {
        var world = new World();
        var physical = world.AddPhysicalFolder("/data/collections");
        world.AddChild(physical, new FlagBoxSet(visible: false));
        world.AddChild(physical, new FlagBoxSet(visible: false));
        var library = world.AddBoxsetsLibrary(physical);

        world.AssertProbe(library, MakeUser("a"), expected: false);
    }

    [Fact]
    public void BoxsetsLibrary_ForbiddenFirstThenAllowed_Listed()
    {
        var world = new World();
        var physical = world.AddPhysicalFolder("/data/collections");
        world.AddChild(physical, new FlagBoxSet(visible: false));
        world.AddChild(physical, new FlagBoxSet(visible: false));
        world.AddChild(physical, new FlagBoxSet(visible: true));
        var library = world.AddBoxsetsLibrary(physical);

        world.AssertProbe(library, MakeUser("a"), expected: true);
    }

    [Fact]
    public void BoxsetsLibrary_AllowedFirstThenForbidden_Listed()
    {
        var world = new World();
        var physical = world.AddPhysicalFolder("/data/collections");
        world.AddChild(physical, new FlagBoxSet(visible: true));
        world.AddChild(physical, new FlagBoxSet(visible: false));
        var library = world.AddBoxsetsLibrary(physical);

        world.AssertProbe(library, MakeUser("a"), expected: true);
    }

    [Fact]
    public void BoxsetsLibrary_VisibleItemOnlyInSecondPhysicalFolder_Listed()
    {
        var world = new World();
        var first = world.AddPhysicalFolder("/data/collections");
        world.AddChild(first, new FlagBoxSet(visible: false));
        var second = world.AddPhysicalFolder("/media/more-collections");
        world.AddChild(second, new FlagBoxSet(visible: true));
        var library = world.AddBoxsetsLibrary(first, second);

        world.AssertProbe(library, MakeUser("a"), expected: true);
    }

    [Fact]
    public void BoxsetsLibrary_UnresolvablePhysicalFolderIds_Skipped()
    {
        var world = new World();
        var physical = world.AddPhysicalFolder("/data/collections");
        world.AddChild(physical, new FlagBoxSet(visible: true));
        var notAFolder = world.Register(new Video());
        var library = world.AddBoxsetsLibrary(physical);
        library.PhysicalFolderIds = [Guid.NewGuid(), physical.Id];

        world.AssertProbe(library, MakeUser("a"), expected: true);

        library.PhysicalFolderIds = [Guid.NewGuid()];
        world.AssertProbe(library, MakeUser("a"), expected: false);

        // A physical id that resolves to a non-folder item contributes nothing.
        library.PhysicalFolderIds = [notAFolder.Id];
        world.AssertProbe(library, MakeUser("a"), expected: false);
    }

    [Fact]
    public void BoxsetsLibrary_VisibleLinkedChildOnly_Listed()
    {
        var world = new World();
        var linked = world.Register(new FlagBoxSet(visible: true));
        var library = world.AddBoxsetsLibrary(world.AddPhysicalFolder("/data/collections"));
        library.LinkedChildren = [LinkedChild.Create(linked)];

        world.AssertProbe(library, MakeUser("a"), expected: true);
    }

    [Fact]
    public void BoxsetsLibrary_ForbiddenLinkedChildOnly_Hidden()
    {
        var world = new World();
        var linked = world.Register(new FlagBoxSet(visible: false));
        var library = world.AddBoxsetsLibrary(world.AddPhysicalFolder("/data/collections"));
        library.LinkedChildren = [LinkedChild.Create(linked)];

        world.AssertProbe(library, MakeUser("a"), expected: false);
    }

    [Fact]
    public void BoxsetsLibrary_DanglingLinkedChild_Hidden()
    {
        var world = new World();
        var library = world.AddBoxsetsLibrary(world.AddPhysicalFolder("/data/collections"));
        library.LinkedChildren = [new LinkedChild { ItemId = Guid.NewGuid(), Type = Tesserafin.Controller.Entities.LinkedChildType.Manual }];

        world.AssertProbe(library, MakeUser("a"), expected: false);
    }

    [Fact]
    public void BoxsetsLibrary_ForbiddenChildAndVisibleLinkedChild_Listed()
    {
        var world = new World();
        var physical = world.AddPhysicalFolder("/data/collections");
        world.AddChild(physical, new FlagBoxSet(visible: false));
        var linked = world.Register(new FlagBoxSet(visible: true));
        var library = world.AddBoxsetsLibrary(physical);
        library.LinkedChildren = [LinkedChild.Create(linked)];

        world.AssertProbe(library, MakeUser("a"), expected: true);
    }

    [Fact]
    public void BoxsetsLibrary_RealBoxSet_ParentalRatingDecidesPerUser()
    {
        var world = new World();
        var physical = world.AddPhysicalFolder("/data/collections");
        world.AddChild(physical, new BoxSet { Name = "Adult saga", OfficialRating = "R" });
        var library = world.AddBoxsetsLibrary(physical);

        var adult = MakeUser("adult");
        var child = MakeUser("child");
        child.MaxParentalRatingScore = 10;

        world.AssertProbe(library, adult, expected: true);
        world.AssertProbe(library, child, expected: false);
    }

    [Fact]
    public void BoxsetsLibrary_RealBoxSet_BlockedTagHidesIt()
    {
        var world = new World();
        var physical = world.AddPhysicalFolder("/data/collections");
        world.AddChild(physical, new BoxSet { Name = "Tagged", Tags = ["blocked-tag"] });
        var library = world.AddBoxsetsLibrary(physical);

        var allowed = MakeUser("allowed");
        var blocked = MakeUser("blocked");
        blocked.SetPreference(PreferenceKind.BlockedTags, ["blocked-tag"]);

        world.AssertProbe(library, allowed, expected: true);
        world.AssertProbe(library, blocked, expected: false);
    }

    [Fact]
    public void PlaylistsFolder_Empty_Hidden()
    {
        var world = new World();
        var library = world.AddPlaylistsFolder();

        world.AssertProbe(library, MakeUser("a"), expected: false);
    }

    [Fact]
    public void PlaylistsFolder_ForbiddenFirstThenAllowed_Listed()
    {
        var world = new World();
        var library = world.AddPlaylistsFolder();
        world.AddChild(library, new FlagPlaylist(visible: false));
        world.AddChild(library, new FlagPlaylist(visible: true));

        world.AssertProbe(library, MakeUser("a"), expected: true);
    }

    [Fact]
    public void PlaylistsFolder_OnlyForbidden_Hidden()
    {
        var world = new World();
        var library = world.AddPlaylistsFolder();
        world.AddChild(library, new FlagPlaylist(visible: false));
        world.AddChild(library, new FlagPlaylist(visible: false));

        world.AssertProbe(library, MakeUser("a"), expected: false);
    }

    [Fact]
    public void PlaylistsFolder_VisibleNonPlaylistChild_Ignored()
    {
        var world = new World();
        var library = world.AddPlaylistsFolder();
        world.AddChild(library, new FlagBoxSet(visible: true));

        world.AssertProbe(library, MakeUser("a"), expected: false);
    }

    [Fact]
    public void PlaylistsFolder_VisibleLinkedChild_Listed()
    {
        var world = new World();
        var library = world.AddPlaylistsFolder();
        var linked = world.Register(new FlagBoxSet(visible: true));
        library.LinkedChildren = [LinkedChild.Create(linked)];

        world.AssertProbe(library, MakeUser("a"), expected: true);
    }

    [Fact]
    public void PlaylistsFolder_RealPlaylists_OwnershipAndSharesDecidePerUser()
    {
        var world = new World();
        var library = world.AddPlaylistsFolder();

        var owner = MakeUser("owner");
        var sharedWith = MakeUser("shared");
        var stranger = MakeUser("stranger");

        world.AddChild(library, new Playlist
        {
            Name = "Private",
            Path = "/data/playlists/private",
            OwnerUserId = owner.Id,
            Shares = [new PlaylistUserPermissions(sharedWith.Id)]
        });

        world.AssertProbe(library, owner, expected: true);
        world.AssertProbe(library, sharedWith, expected: true);
        world.AssertProbe(library, stranger, expected: false);

        world.AddChild(library, new Playlist
        {
            Name = "Open",
            Path = "/data/playlists/open",
            OwnerUserId = owner.Id,
            OpenAccess = true
        });

        world.AssertProbe(library, stranger, expected: true);
    }

    [Fact]
    public void Catalog_TwoUsers_ListsExactlyTheirVisibleLibraries_InOrder()
    {
        var world = new World();
        var owner = MakeUser("owner");
        var child = MakeUser("child");
        child.MaxParentalRatingScore = 10;

        var movies = world.Register(new CollectionFolder { Name = "Movies", CollectionType = CollectionType.movies });
        var playlists = world.AddPlaylistsFolder();
        world.AddChild(playlists, new Playlist { Name = "Mine", Path = "/data/playlists/mine", OwnerUserId = owner.Id });
        var physical = world.AddPhysicalFolder("/data/collections");
        world.AddChild(physical, new BoxSet { Name = "Adult saga", OfficialRating = "R" });
        var boxsets = world.AddBoxsetsLibrary(physical);

        var catalog = world.BuildCatalog(movies, playlists, boxsets);

        var ownerViews = catalog.GetUserViews(new UserViewQuery { User = owner, IncludeExternalContent = false });
        Assert.Equal(
            [movies.Id, World.NamedViewId(owner, playlists), boxsets.Id],
            ownerViews.Select(v => v.Id));

        var childViews = catalog.GetUserViews(new UserViewQuery { User = child, IncludeExternalContent = false });
        Assert.Equal([movies.Id], childViews.Select(v => v.Id));

        owner.SetPreference(PreferenceKind.OrderedViews, [boxsets.Id, playlists.Id]);
        ownerViews = catalog.GetUserViews(new UserViewQuery { User = owner, IncludeExternalContent = false });
        Assert.Equal(
            [boxsets.Id, World.NamedViewId(owner, playlists), movies.Id],
            ownerViews.Select(v => v.Id));
    }

    /// <summary>
    /// One self-contained library world: the process-wide entity statics
    /// (<see cref="BaseItem.LibraryManager"/>, <see cref="BaseItem.ItemRepository"/>, ...) the historical
    /// probe reads are pointed at mocks backed by the same item and child tables.
    /// </summary>
    private sealed class World
    {
        private readonly Dictionary<Guid, BaseItem> _items = new();
        private readonly Dictionary<Guid, List<BaseItem>> _children = new();
        private readonly Guid _rootId = Guid.NewGuid();
        private readonly Mock<IServerConfigurationManager> _config = new();
        private readonly Mock<IItemRepository> _staticRepository = new();
        private readonly Mock<ILibraryManager> _libraryManager = new(MockBehavior.Strict);
        private readonly List<System.Diagnostics.StackTrace> _libraryManagerCalls = new();
        private bool _recordLibraryManagerCalls;

        public World()
        {
            _config.Setup(c => c.Configuration).Returns(new ServerConfiguration());
            _config.Setup(c => c.ApplicationPaths.DataPath).Returns("/data");
            BaseItem.ConfigurationManager = _config.Object;
            BaseItem.Logger = NullLogger<BaseItem>.Instance;

            var fileSystem = new Mock<IFileSystem>();
            fileSystem
                .Setup(f => f.ContainsSubPath(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string parent, string path) => path.StartsWith(parent.TrimEnd('/') + "/", StringComparison.Ordinal));
            BaseItem.FileSystem = fileSystem.Object;

            var localization = new Mock<ILocalizationManager>();
            localization
                .Setup(l => l.GetRatingScore("R", It.IsAny<string?>()))
                .Returns(new ParentalRatingScore(17, null));

            BackRepository(_staticRepository);
            BaseItem.ItemRepository = _staticRepository.Object;

            var injectedRepository = new Mock<IItemRepository>();
            BackRepository(injectedRepository);
            var lookup = new Mock<IItemLookupService>();
            lookup.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => Find(id));
            // The probe's scope sees the same world the legacy static path sees: no libraries, default options.
            var emptyRoot = new Folder { Children = [] };
            var rootProvider = Mock.Of<IUserRootFolderProvider>(p => p.GetUserRootFolder() == emptyRoot);
            var scope = new TagVisibilityService(
                lookup.Object,
                new CollectionFolderResolver(lookup.Object, rootProvider),
                rootProvider,
                injectedRepository.Object,
                localization.Object,
                _config.Object,
                new Tesserafin.Controller.LiveTv.ActiveRecordings(),
                Mock.Of<ILibraryItemOptions>(o => o.GetLibraryOptions(It.IsAny<BaseItem>()) == new LibraryOptions()));
            Scope = scope;
            Probe = new VisibleContentProbe(lookup.Object, injectedRepository.Object, scope);

            _libraryManager.Setup(l => l.GetItemById(It.IsAny<Guid>())).Callback(RecordLibraryManagerCall).Returns((Guid id) => Find(id));
            _libraryManager
                .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Callback(RecordLibraryManagerCall)
                .Returns((InternalItemsQuery q) => q.ItemIds.Select(Find).OfType<BaseItem>().ToArray());
            _libraryManager.Setup(l => l.GetCollectionFolders(It.IsAny<BaseItem>())).Callback(RecordLibraryManagerCall).Returns([]);
            _libraryManager.Setup(l => l.GetLibraryOptions(It.IsAny<BaseItem>())).Callback(RecordLibraryManagerCall).Returns(new LibraryOptions());
            BaseItem.LibraryManager = _libraryManager.Object;
        }

        public VisibleContentProbe Probe { get; }

        public IStandaloneVisibilityService Scope { get; }

        public T Register<T>(T item)
            where T : BaseItem
        {
            if (item.Id.IsEmpty())
            {
                item.Id = Guid.NewGuid();
            }

            _items[item.Id] = item;
            return item;
        }

        public T AddChild<T>(Folder parent, T item)
            where T : BaseItem
        {
            Register(item);
            item.ParentId = parent.Id;
            if (!_children.TryGetValue(parent.Id, out var list))
            {
                _children[parent.Id] = list = new List<BaseItem>();
            }

            list.Add(item);
            return item;
        }

        public Folder AddPhysicalFolder(string path) => Register(new Folder { Name = path, Path = path, ParentId = _rootId });

        public CollectionFolder AddBoxsetsLibrary(params Folder[] physicalFolders)
        {
            return Register(new CollectionFolder
            {
                Name = "Collections",
                CollectionType = CollectionType.boxsets,
                ParentId = _rootId,
                PhysicalFolderIds = physicalFolders.Select(f => f.Id).ToArray()
            });
        }

        public PlaylistsFolder AddPlaylistsFolder() => Register(new PlaylistsFolder { Path = "/data/playlists", ParentId = _rootId });

        public void AssertProbe(Folder library, User user, bool expected)
        {
            Assert.Equal(expected, LegacyProbe(library, user));

            ResetChildCaches();
            _staticRepository.Invocations.Clear();
            _libraryManagerCalls.Clear();
            _recordLibraryManagerCalls = true;

            try
            {
                Assert.Equal(expected, Probe.HasVisibleContent(library, user));
            }
            finally
            {
                _recordLibraryManagerCalls = false;
            }

            Assert.Empty(_staticRepository.Invocations);
            Assert.All(_libraryManagerCalls, stack => Assert.Contains(
                stack.GetFrames(),
                frame => frame.GetMethod() is { Name: nameof(BaseItem.IsVisible) } method
                    && typeof(BaseItem).IsAssignableFrom(method.DeclaringType)));
        }

        public static Guid NamedViewId(User user, Folder library) =>
            new(user.Id.ToByteArray().Zip(library.Id.ToByteArray(), (a, b) => (byte)(a ^ b)).ToArray());

        public UserViewCatalog BuildCatalog(params Folder[] libraries)
        {
            var root = new Mock<Folder> { CallBase = true };
            root.Setup(r => r.GetChildren(It.IsAny<User>(), true, It.IsAny<InternalItemsQuery>(), It.IsAny<IItemSortService>())).Returns(libraries);
            var rootProvider = new Mock<IUserRootFolderProvider>();
            rootProvider.Setup(p => p.GetUserRootFolder()).Returns(root.Object);

            var viewFactory = new Mock<IUserViewFactory>();
            viewFactory
                .Setup(f => f.GetNamedView(It.IsAny<User>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CollectionType?>(), It.IsAny<string>()))
                .Returns((User user, string name, Guid parentId, CollectionType? type, string sortName) =>
                    new UserView { Id = NamedViewId(user, (Folder)Find(parentId)!), Name = name, DisplayParentId = parentId, ViewType = type });

            var sortService = new Tesserafin.Server.Core.Sorting.ItemSortService(Mock.Of<IUserManager>(), Mock.Of<IUserDataManager>());
            return new UserViewCatalog(
                rootProvider.Object,
                viewFactory.Object,
                Mock.Of<IChannelCatalog>(),
                Mock.Of<Tesserafin.Controller.LiveTv.ILiveTvPresenceProvider>(),
                sortService,
                _config.Object,
                Mock.Of<ILocalizationManager>(),
                Probe,
                Scope);
        }

        private ItemQueryService NewItemQueryService() =>
            new ItemQueryService(
                Mock.Of<IChannelManager>(),
                Mock.Of<ICollectionManager>(),
                Mock.Of<IUserViewManager>(),
                Mock.Of<ITVSeriesManager>(),
                _config.Object,
                new Tesserafin.Server.Core.Sorting.ItemSortService(Mock.Of<IUserManager>(), Mock.Of<IUserDataManager>()),
                Mock.Of<IItemLookupService>(),
                Mock.Of<IItemRepository>(),
                Mock.Of<IItemQueryScopeService>(),
                Scope);

        private bool LegacyProbe(Folder library, User user)
        {
            var itemQueryService = NewItemQueryService();
            ResetChildCaches();

            return itemQueryService
                .GetItemList(library, new InternalItemsQuery(user) { ParentId = library.ParentId, Visibility = Scope })
                .Any(item => item.IsVisible(user, false, Scope));
        }

        private BaseItem? Find(Guid id) => _items.GetValueOrDefault(id);

        // Folders cache their children on first read: start every probe cold, so neither run can
        // lean on a cache the other one filled.
        private void ResetChildCaches()
        {
            foreach (var folder in _items.Values.OfType<Folder>())
            {
                folder.Children = null;
            }
        }

        private void RecordLibraryManagerCall()
        {
            if (_recordLibraryManagerCalls)
            {
                _libraryManagerCalls.Add(new System.Diagnostics.StackTrace());
            }
        }

        private void BackRepository(Mock<IItemRepository> repository)
        {
            repository.Setup(r => r.RetrieveItem(It.IsAny<Guid>())).Returns((Guid id) => Find(id)!);
            repository.Setup(r => r.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q) => ChildrenOf(q.ParentId));
            repository.Setup(r => r.GetItemIdsList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q) => ChildrenOf(q.ParentId).Select(i => i.Id).ToArray());
        }

        private IReadOnlyList<BaseItem> ChildrenOf(Guid parentId) =>
            _children.TryGetValue(parentId, out var list) ? list.ToArray() : [];
    }

    private sealed class FlagBoxSet : BoxSet
    {
        private readonly bool _visible;

        public FlagBoxSet(bool visible)
        {
            _visible = visible;
        }

        public override bool IsVisible(User user, bool skipAllowedTagsCheck, IStandaloneVisibilityService tagVisibility) => _visible;
    }

    private sealed class FlagPlaylist : Playlist
    {
        private readonly bool _visible;

        public FlagPlaylist(bool visible)
        {
            _visible = visible;
        }

        public override bool IsVisible(User user, bool skipAllowedTagsCheck, IStandaloneVisibilityService tagVisibility) => _visible;
    }
}
