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
using Tesserafin.Model.MediaInfo;
using Tesserafin.Model.Serialization;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library.LibraryManager;

/// <summary>
/// Characterisation of library membership resolution (<c>GetCollectionFolders</c>): which top-level
/// libraries an item belongs to, and the visibility and deletion rules that read it through the
/// static <see cref="BaseItem.LibraryManager"/>. Every expectation is hand-derived and was green on
/// the unmodified <c>LibraryManager</c>; membership is now asserted through both the
/// <see cref="ILibraryManager"/> facade and <see cref="ICollectionFolderResolver"/> (DEBT-SERVER-2).
/// </summary>
/// <remarks>
/// The item graph is real entities over a real <c>ItemLookupService</c>; only the repository
/// (id to item) and the user root folder (its library children) are stubbed.
/// </remarks>
[Collection(LibraryManagerStaticStateFixture.Name)]
public sealed class CollectionFolderMembershipTests
{
    private readonly Dictionary<Guid, BaseItem> _items = [];
    private readonly List<Folder> _libraries = [];
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionFolderResolver _resolver;

    public CollectionFolderMembershipTests()
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

        _resolver = new Tesserafin.Server.Core.Library.CollectionFolderResolver(lookup, rootProvider);
        fixture.Register<ICollectionFolderResolver>(() => _resolver);

        Tesserafin.Server.Implementations.Tests.Library.LibraryWriteFixture.Register(fixture);

        var manager = fixture.Build<Tesserafin.Server.Core.Library.LibraryManager>().Do(s => s.AddParts(
                Array.Empty<IResolverIgnoreRule>(),
                Array.Empty<IItemResolver>(),
                fixture.Create<IEnumerable<IBaseItemComparer>>()))
            .Create();
        _libraryManager = manager;

        BaseItem.LibraryManager = manager;
        BaseItem.ConfigurationManager = configuration.Object;
        BaseItem.MediaSourceManager = Mock.Of<IMediaSourceManager>(m => m.GetPathProtocol(It.IsAny<string>()) == MediaProtocol.File);
        Video.RecordingsManager = Mock.Of<IRecordingsManager>();
    }

    [Fact]
    public void ItemUnderLibraryFolder_BelongsToThatLibrary()
    {
        var movies = AddLibrary("Movies", "/media/movies");
        AddLibrary("Shows", "/media/shows");
        var movie = AddMovie(Add(new Folder { Path = "/media/movies" }), "/media/movies/a.mkv");

        AssertFolders(movie, movies);
    }

    [Fact]
    public void SecondPhysicalLocation_MatchesIgnoringCase()
    {
        var movies = AddLibrary("Movies", "/media/movies", "/MNT/Extra");
        var movie = AddMovie(Add(new Folder { Path = "/mnt/extra" }), "/mnt/extra/b.mkv");

        AssertFolders(movie, movies);
    }

    [Fact]
    public void MembershipIsDecidedByTheTopAncestor_NotByTheItemPath()
    {
        var movies = AddLibrary("Movies", "/media/movies");
        AddLibrary("Elsewhere", "/media/elsewhere");
        var top = Add(new Folder { Path = "/media/movies" });
        var nested = Add(new Folder { Path = "/media/elsewhere", ParentId = top.Id });
        var movie = AddMovie(nested, "/media/elsewhere/c.mkv");

        AssertFolders(movie, movies);
    }

    [Fact]
    public void AncestorWalkStopsBelowAnAggregateFolder()
    {
        var movies = AddLibrary("Movies", "/media/movies");
        var aggregate = Add(new AggregateFolder { Path = "/media" });
        var top = Add(new Folder { Path = "/media/movies", ParentId = aggregate.Id });
        var movie = AddMovie(top, "/media/movies/d.mkv");

        AssertFolders(movie, movies);
    }

    [Fact]
    public void ParentlessItem_FollowsItsOwner()
    {
        var movies = AddLibrary("Movies", "/media/movies");
        var movie = AddMovie(Add(new Folder { Path = "/media/movies" }), "/media/movies/e.mkv");
        var extra = Add(new Movie { Path = "/media/movies/e-trailer.mkv", OwnerId = movie.Id });

        AssertFolders(extra, movies);
    }

    [Fact]
    public void ParentlessOwnerlessItem_IsMatchedOnItsOwnPath()
    {
        var movies = AddLibrary("Movies", "/media/movies");
        var top = Add(new Folder { Path = "/media/movies" });

        AssertFolders(top, movies);
    }

    [Fact]
    public void ParentThatNoLongerResolves_FallsBackToTheItemItself()
    {
        AddLibrary("Movies", "/media/movies");
        var orphan = Add(new Movie { Path = "/media/movies/f.mkv", ParentId = Guid.NewGuid() });

        AssertFolders(orphan);
    }

    [Fact]
    public void NoMatchingLibrary_IsEmpty()
    {
        AddLibrary("Movies", "/media/movies");
        var movie = AddMovie(Add(new Folder { Path = "/media/other" }), "/media/other/g.mkv");

        AssertFolders(movie);
    }

    [Fact]
    public void EveryMatchingLibrary_IsReturnedInRootOrder()
    {
        var first = AddLibrary("A", "/media/shared");
        AddLibrary("B", "/media/other");
        var third = AddLibrary("C", "/media/other", "/media/shared");
        var movie = AddMovie(Add(new Folder { Path = "/media/shared" }), "/media/shared/h.mkv");

        AssertFolders(movie, first, third);
    }

    [Fact]
    public void NullItem_IsEmpty()
    {
        AddLibrary("Movies", "/media/movies");

        Assert.Empty(_libraryManager.GetCollectionFolders(null!));
        Assert.Empty(_libraryManager.GetCollectionFolders(null!, _libraries));
        Assert.Empty(_resolver.GetCollectionFolders(null!));
        Assert.Empty(_resolver.GetCollectionFolders(null!, _libraries));
    }

    [Fact]
    public void ExplicitRootChildren_OnlyThoseAreConsidered()
    {
        var movies = AddLibrary("Movies", "/media/movies");
        var alsoMovies = new CollectionFolder { Id = Guid.NewGuid(), Path = "/cfg/AlsoMovies", PhysicalLocationsList = ["/media/movies"] };
        var movie = AddMovie(Add(new Folder { Path = "/media/movies" }), "/media/movies/i.mkv");

        Assert.Equal([alsoMovies.Id], _libraryManager.GetCollectionFolders(movie, [alsoMovies]).Select(f => f.Id));
        Assert.Equal([movies.Id], _libraryManager.GetCollectionFolders(movie).Select(f => f.Id));
        Assert.Equal([alsoMovies.Id], _resolver.GetCollectionFolders(movie, [alsoMovies]).Select(f => f.Id));
        Assert.Equal([movies.Id], _resolver.GetCollectionFolders(movie).Select(f => f.Id));
    }

    [Fact]
    public void LibraryOptions_ComeFromTheMemberLibrary()
    {
        var movies = AddLibrary("Movies", "/media/movies");
        var options = new LibraryOptions { PreferredMetadataLanguage = "fr" };
        CollectionFolder.XmlSerializer = Mock.Of<IXmlSerializer>(x =>
            x.DeserializeFromFile(typeof(LibraryOptions), System.IO.Path.Combine(movies.Path, "options.xml")) == options);
        var movie = AddMovie(Add(new Folder { Path = "/media/movies" }), "/media/movies/j.mkv");

        Assert.Same(options, _libraryManager.GetLibraryOptions(movie));
        Assert.Equal("fr", movie.GetPreferredMetadataLanguage());
    }

    [Fact]
    public void Visibility_EnabledFolders_AdmitOnlyTheirLibraries()
    {
        var movies = AddLibrary("Movies", "/media/movies");
        var shows = AddLibrary("Shows", "/media/shows");
        var movie = AddMovie(Add(new Folder { Path = "/media/movies" }), "/media/movies/k.mkv");

        Assert.True(movie.IsVisibleStandalone(UserWithFolders(PreferenceKind.EnabledFolders, movies.Id)));
        Assert.False(movie.IsVisibleStandalone(UserWithFolders(PreferenceKind.EnabledFolders, shows.Id)));
    }

    [Fact]
    public void Visibility_BlockedFolders_ExcludeTheirLibraries()
    {
        var movies = AddLibrary("Movies", "/media/movies");
        var shows = AddLibrary("Shows", "/media/shows");
        var movie = AddMovie(Add(new Folder { Path = "/media/movies" }), "/media/movies/l.mkv");

        Assert.False(movie.IsVisibleStandalone(UserWithFolders(PreferenceKind.BlockedMediaFolders, movies.Id)));
        Assert.True(movie.IsVisibleStandalone(UserWithFolders(PreferenceKind.BlockedMediaFolders, shows.Id)));
    }

    [Fact]
    public void Visibility_ItemOutsideEveryLibrary_IsNotFolderRestricted()
    {
        var shows = AddLibrary("Shows", "/media/shows");
        var movie = AddMovie(Add(new Folder { Path = "/media/other" }), "/media/other/m.mkv");

        Assert.True(movie.IsVisibleStandalone(UserWithFolders(PreferenceKind.EnabledFolders, shows.Id)));
    }

    [Fact]
    public void Deletion_FromFolders_FollowsLibraryMembership()
    {
        var movies = AddLibrary("Movies", "/media/movies");
        var shows = AddLibrary("Shows", "/media/shows");
        var movie = AddMovie(Add(new Folder { Path = "/media/movies" }), "/media/movies/n.mkv");

        Assert.True(movie.CanDelete(UserWithFolders(PreferenceKind.EnableContentDeletionFromFolders, movies.Id), _libraries));
        Assert.False(movie.CanDelete(UserWithFolders(PreferenceKind.EnableContentDeletionFromFolders, shows.Id), _libraries));
    }

    private static User UserWithFolders(PreferenceKind kind, Guid folderId)
    {
        var user = new User("u", "auth", "reset") { Id = Guid.NewGuid() };
        user.SetPermission(PermissionKind.EnableAllFolders, false);
        user.SetPermission(PermissionKind.EnableContentDeletion, false);
        user.SetPreference(kind, [folderId]);
        return user;
    }

    private CollectionFolder AddLibrary(string name, params string[] locations)
    {
        var library = new CollectionFolder { Id = Guid.NewGuid(), Name = name, Path = "/config/root/" + name + "-" + Guid.NewGuid().ToString("N"), PhysicalLocationsList = locations };
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

    private Movie AddMovie(Folder parent, string path) => Add(new Movie { Path = path, ParentId = parent.Id });

    private void AssertFolders(BaseItem item, params Folder[] expected)
    {
        var ids = expected.Select(f => f.Id).ToArray();
        Assert.Equal(ids, _libraryManager.GetCollectionFolders(item).Select(f => f.Id));
        Assert.Equal(ids, _libraryManager.GetCollectionFolders(item, _libraries).Select(f => f.Id));
        Assert.Equal(ids, _resolver.GetCollectionFolders(item).Select(f => f.Id));
        Assert.Equal(ids, _resolver.GetCollectionFolders(item, _libraries).Select(f => f.Id));
    }
}
