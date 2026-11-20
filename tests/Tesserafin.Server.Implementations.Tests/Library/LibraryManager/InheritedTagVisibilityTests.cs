using System;
using System.Collections.Generic;
using System.Linq;
using AutoFixture;
using AutoFixture.AutoMoq;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Resolvers;
using Tesserafin.Controller.Sorting;
using Tesserafin.Data;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.MediaInfo;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library.LibraryManager;

/// <summary>
/// Characterisation of tag inheritance (<c>GetInheritedTags</c>: the item's own tags, then each
/// parent's, nearest first, then each member library's, deduplicated ignoring case, first spelling
/// kept) and of the tag filter that reads it: blocked tags first, then allowed tags, the
/// <c>skipAllowedTagsCheck</c> switch and the root-folder exception. Every expectation is
/// hand-derived and was green on the unmodified entities, read through the static
/// <see cref="BaseItem.LibraryManager"/>; since DEBT-SERVER-3 the same expectations hold through
/// <see cref="ITagVisibilityService"/>, and the static and the service must agree.
/// </summary>
/// <remarks>
/// The item graph is real entities over a real <c>ItemLookupService</c>; only the repository
/// (id to item) and the user root folder (its library children) are stubbed. The visibility
/// decisions are taken through <see cref="IItemAccessService.GetVisibleItemById{T}"/>, the
/// production path used by the playback, play-state and media-info endpoints, which now passes the
/// service down the entities' overrides. The <c>ServicePath_</c> tests make the static blind to
/// libraries, so a decision that still read tags through it would differ.
/// </remarks>
[Collection(LibraryManagerStaticStateFixture.Name)]
public sealed class InheritedTagVisibilityTests
{
    private readonly Dictionary<Guid, BaseItem> _items = [];
    private readonly List<Folder> _libraries = [];
    private readonly IItemAccessService _access;
    private readonly IStandaloneVisibilityService _tags;
    private readonly IItemLookupService _lookup;

    public InheritedTagVisibilityTests()
    {
        var fixture = new Fixture().Customize(new AutoMoqCustomization());

        var configuration = fixture.Freeze<Mock<IServerConfigurationManager>>();
        configuration.Setup(c => c.Configuration).Returns(new ServerConfiguration());

        var repository = fixture.Freeze<Mock<IItemRepository>>();
        repository.Setup(r => r.RetrieveItem(It.IsAny<Guid>())).Returns((Guid id) => _items.GetValueOrDefault(id)!);

        var lookup = new Tesserafin.Server.Core.Library.ItemLookupService(repository.Object, configuration.Object);
        fixture.Register<IItemLookupService>(() => lookup);
        fixture.Register<Tesserafin.Server.Core.Library.IItemCacheStore>(() => lookup);

        var root = new Folder { Id = Guid.NewGuid(), Children = _libraries };
        var rootProvider = Mock.Of<IUserRootFolderProvider>(p => p.GetUserRootFolder() == root);
        fixture.Register(() => rootProvider);

        var resolver = new Tesserafin.Server.Core.Library.CollectionFolderResolver(lookup, rootProvider);
        fixture.Register<ICollectionFolderResolver>(() => resolver);

        fixture.Inject<IItemQueryScopeService>(new Tesserafin.Server.Core.Library.ItemQueryScopeService(
            lookup,
            fixture.Freeze<IItemAccessService>(),
            fixture.Freeze<IUserViewCatalog>(),
            fixture.Freeze<IItemSortService>(),
            rootProvider));

        var manager = fixture.Build<Tesserafin.Server.Core.Library.LibraryManager>().Do(s => s.AddParts(
                Array.Empty<IResolverIgnoreRule>(),
                Array.Empty<IItemResolver>(),
                fixture.Create<IEnumerable<IBaseItemComparer>>()))
            .Create();

        _lookup = lookup;
        _tags = new Tesserafin.Server.Core.Library.TagVisibilityService(lookup, resolver, rootProvider, repository.Object, Mock.Of<ILocalizationManager>(), configuration.Object, new Tesserafin.Controller.LiveTv.ActiveRecordings());
        _access = new Tesserafin.Server.Core.Library.ItemAccessService(lookup, _tags);

        BaseItem.LibraryManager = manager;
        BaseItem.ConfigurationManager = configuration.Object;
        BaseItem.MediaSourceManager = Mock.Of<IMediaSourceManager>(m => m.GetPathProtocol(It.IsAny<string>()) == MediaProtocol.File);
    }

    [Fact]
    public void InheritedTags_OwnTagsOnly_InTheirOrder()
    {
        AddLibrary("Movies", [], "/media/movies");
        var movie = AddMovie(Add(new Folder { Path = "/media/movies" }), "/media/movies/a.mkv", "b", "a");

        AssertTags(movie, "b", "a");
    }

    [Fact]
    public void InheritedTags_ParentsFollowOwnTags_NearestFirst()
    {
        AddLibrary("Movies", [], "/media/movies");
        var top = Add(new Folder { Path = "/media/movies", Tags = ["top"] });
        var middle = Add(new Folder { Path = "/media/movies/saga", ParentId = top.Id, Tags = ["middle"] });
        var movie = AddMovie(middle, "/media/movies/saga/a.mkv", "own");

        AssertTags(movie, "own", "middle", "top");
    }

    [Fact]
    public void InheritedTags_EveryMemberLibrary_InRootOrder_NonMembersExcluded()
    {
        AddLibrary("A", ["a-lib"], "/media/shared");
        AddLibrary("B", ["b-lib"], "/media/other");
        AddLibrary("C", ["c-lib"], "/media/other", "/media/shared");
        var top = Add(new Folder { Path = "/media/shared", Tags = ["top"] });
        var movie = AddMovie(top, "/media/shared/a.mkv", "own");

        AssertTags(movie, "own", "top", "a-lib", "c-lib");
    }

    [Fact]
    public void InheritedTags_DuplicatesIgnoringCase_KeepTheFirstSpelling()
    {
        AddLibrary("Movies", ["DRAMA", "Library"], "/media/movies");
        var top = Add(new Folder { Path = "/media/movies", Tags = ["kids", "Drama"] });
        var movie = AddMovie(top, "/media/movies/a.mkv", "Kids", "kids");

        AssertTags(movie, "Kids", "Drama", "Library");
    }

    [Fact]
    public void InheritedTags_OwnedExtra_TakesItsOwnersLibraries_ButNotItsOwnersTags()
    {
        AddLibrary("Movies", ["lib"], "/media/movies");
        var movie = AddMovie(Add(new Folder { Path = "/media/movies", Tags = ["top"] }), "/media/movies/a.mkv", "owner");
        var extra = Add(new Movie { Path = "/media/movies/a-trailer.mkv", OwnerId = movie.Id, Tags = ["extra"] });

        AssertTags(extra, "extra", "lib");
    }

    [Fact]
    public void InheritedTags_ItemOutsideEveryLibrary_OwnAndParentsOnly()
    {
        AddLibrary("Movies", ["lib"], "/media/movies");
        var movie = AddMovie(Add(new Folder { Path = "/media/other", Tags = ["top"] }), "/media/other/a.mkv", "own");

        AssertTags(movie, "own", "top");
    }

    [Theory]
    [InlineData("own")]
    [InlineData("parent")]
    [InlineData("library")]
    public void BlockedTag_FromAnyLevel_HidesTheItem(string level)
    {
        var movie = TaggedAt(level, "Blocked");

        Assert.False(IsVisible(movie, User(blocked: ["blocked"])));
        Assert.True(IsVisible(movie, User()));
    }

    [Theory]
    [InlineData("own")]
    [InlineData("parent")]
    [InlineData("library")]
    public void AllowedTag_FromAnyLevel_AdmitsTheItem(string level)
    {
        var movie = TaggedAt(level, "Family");

        Assert.True(IsVisible(movie, User(allowed: ["family"])));
        Assert.False(IsVisible(movie, User(allowed: ["other"])));
    }

    [Fact]
    public void BlockedTag_WinsOverAnAllowedTag()
    {
        AddLibrary("Movies", ["family"], "/media/movies");
        var movie = AddMovie(Add(new Folder { Path = "/media/movies" }), "/media/movies/a.mkv", "horror");

        Assert.False(IsVisible(movie, User(blocked: ["horror"], allowed: ["family"])));
    }

    [Fact]
    public void TagsAreComparedAfterCleaning_CaseDiacriticsAndPunctuation()
    {
        AddLibrary("Movies", ["Épouvante!"], "/media/movies");
        var movie = AddMovie(Add(new Folder { Path = "/media/movies" }), "/media/movies/a.mkv");

        Assert.False(IsVisible(movie, User(blocked: ["epouvante "])));
    }

    [Fact]
    public void SkipAllowedTagsCheck_IgnoresAllowedTags_ButNotBlockedTags()
    {
        AddLibrary("Movies", ["lib"], "/media/movies");
        var movie = AddMovie(Add(new Folder { Path = "/media/movies" }), "/media/movies/a.mkv");

        foreach (var tags in new[] { null, _tags })
        {
            Assert.False(movie.IsVisible(User(allowed: ["family"]), false, tags));
            Assert.True(movie.IsVisible(User(allowed: ["family"]), true, tags));
            Assert.False(movie.IsVisible(User(blocked: ["lib"]), true, tags));
        }
    }

    [Fact]
    public void AncestorsAreCheckedWithAllowedTagsSkipped_SoAnUntaggedParentDoesNotHideAnAllowedItem()
    {
        AddLibrary("Movies", [], "/media/movies");
        var movie = AddMovie(Add(new Folder { Path = "/media/movies" }), "/media/movies/a.mkv", "family");

        Assert.True(IsVisible(movie, User(allowed: ["family"])));
    }

    [Theory]
    [InlineData(typeof(UserRootFolder))]
    [InlineData(typeof(AggregateFolder))]
    public void ChildOfARootFolder_SkipsAllowedTags_ButNotBlockedTags(Type rootType)
    {
        var root = Add((Folder)Activator.CreateInstance(rootType)!);
        var library = Add(new Folder { Path = "/media/movies", ParentId = root.Id, Tags = ["lib"] });

        foreach (var tags in new[] { null, _tags })
        {
            Assert.True(library.IsVisible(User(allowed: ["family"]), false, tags));
            Assert.False(library.IsVisible(User(blocked: ["lib"]), false, tags));
        }
    }

    [Fact]
    public void ParentlessItem_IsNotARootChild_AllowedTagsApply()
    {
        var movie = Add(new Movie { Path = "/media/other/a.mkv" });

        Assert.False(movie.IsVisible(User(allowed: ["family"])));
        Assert.False(movie.IsVisible(User(allowed: ["family"]), false, _tags));
    }

    [Fact]
    public void NoTagPreferences_EveryItemIsVisible()
    {
        AddLibrary("Movies", ["anything"], "/media/movies");
        var movie = AddMovie(Add(new Folder { Path = "/media/movies" }), "/media/movies/a.mkv", "x");

        Assert.True(IsVisible(movie, User()));
    }

    [Fact]
    public void TwoUsers_SameItems_DifferentDecisions()
    {
        AddLibrary("Kids", ["kids"], "/media/kids");
        AddLibrary("Movies", [], "/media/movies");
        var cartoon = AddMovie(Add(new Folder { Path = "/media/kids" }), "/media/kids/a.mkv");
        var thriller = AddMovie(Add(new Folder { Path = "/media/movies", Tags = ["violence"] }), "/media/movies/b.mkv");
        var plain = AddMovie(Add(new Folder { Path = "/media/movies/c" }), "/media/movies/c/c.mkv");

        var admin = User();
        var child = User(blocked: ["violence"], allowed: ["kids"]);

        Assert.Equal([true, true, true], new[] { cartoon, thriller, plain }.Select(m => IsVisible(m, admin)));
        Assert.Equal([true, false, false], new[] { cartoon, thriller, plain }.Select(m => IsVisible(m, child)));
    }

    [Fact]
    public void ServicePath_Movie_ReadsLibraryTagsThroughTheService()
    {
        var movie = AddMovie(Add(new Folder { Path = "/media/movies" }), "/media/movies/a.mkv");

        AssertServiceDecides(movie);
    }

    [Fact]
    public void ServicePath_Folder_KeepsItsOverride_AndReadsLibraryTagsThroughTheService()
    {
        var folder = Add(new Folder { Path = "/media/movies/saga", ParentId = Add(new Folder { Path = "/media/movies" }).Id });

        AssertServiceDecides(folder);
    }

    [Fact]
    public void ServicePath_BoxSet_KeepsItsOverride_AndReadsLibraryTagsThroughTheService()
    {
        var boxSet = Add(new BoxSet { ParentId = Add(new Folder { Path = "/media/movies" }).Id });

        AssertServiceDecides(boxSet);
    }

    [Fact]
    public void ServicePath_Playlist_KeepsItsOverride_AndReadsLibraryTagsThroughTheService()
    {
        var playlist = Add(new Tesserafin.Controller.Playlists.Playlist { ParentId = Add(new Folder { Path = "/media/movies" }).Id });

        AssertServiceDecides(playlist);
    }

    /// <summary>
    /// Puts the item's top folder in a library tagged "lib", blinds the static to libraries, then
    /// asserts the access path decides on that tag (blocked hides, allowed admits) while the static
    /// path, which no longer sees it, decides the other way.
    /// </summary>
    private void AssertServiceDecides(BaseItem item)
    {
        AddLibrary("Movies", ["lib"], "/media/movies");
        BlindStatic();

        Assert.False(IsVisible(item, User(blocked: ["lib"])));
        Assert.True(IsVisible(item, User(allowed: ["lib"])));

        Assert.True(item.IsVisibleStandalone(User(blocked: ["lib"])));
        Assert.False(item.IsVisibleStandalone(User(allowed: ["lib"])));
    }

    private void BlindStatic()
    {
        var blind = new Mock<ILibraryManager>();
        blind.Setup(m => m.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => _lookup.GetItemById(id));
        blind.Setup(m => m.GetCollectionFolders(It.IsAny<BaseItem>())).Returns([]);
        BaseItem.LibraryManager = blind.Object;
    }

    private static User User(string[]? blocked = null, string[]? allowed = null)
    {
        var user = new User("u", "auth", "reset") { Id = Guid.NewGuid() };
        user.SetPermission(PermissionKind.EnableAllFolders, true);
        user.SetPreference(PreferenceKind.BlockedTags, blocked ?? []);
        user.SetPreference(PreferenceKind.AllowedTags, allowed ?? []);
        return user;
    }

    private Movie TaggedAt(string level, string tag)
    {
        AddLibrary("Movies", level == "library" ? [tag] : [], "/media/movies");
        var top = Add(new Folder { Path = "/media/movies", Tags = level == "parent" ? [tag] : [] });
        return level == "own"
            ? AddMovie(top, "/media/movies/a.mkv", tag)
            : AddMovie(top, "/media/movies/a.mkv");
    }

    private bool IsVisible(BaseItem item, User user) => _access.GetVisibleItemById<BaseItem>(item.Id, user) is not null;

    private CollectionFolder AddLibrary(string name, string[] tags, params string[] locations)
    {
        var library = new CollectionFolder { Id = Guid.NewGuid(), Name = name, Path = "/config/root/" + name + "-" + Guid.NewGuid().ToString("N"), PhysicalLocationsList = locations, Tags = tags };
        _libraries.Add(library);
        return library;
    }

    private T Add<T>(T item)
        where T : BaseItem
    {
        if (item.Id.Equals(Guid.Empty))
        {
            item.Id = Guid.NewGuid();
        }

        _items[item.Id] = item;
        return item;
    }

    private Movie AddMovie(Folder parent, string path, params string[] tags) => Add(new Movie { Path = path, ParentId = parent.Id, Tags = tags });

    private void AssertTags(BaseItem item, params string[] expected)
    {
        Assert.Equal(expected, item.GetInheritedTags());
        Assert.Equal(expected, _tags.GetInheritedTags(item));
    }
}
