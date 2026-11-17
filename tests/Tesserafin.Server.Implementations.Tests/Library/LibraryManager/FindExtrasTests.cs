using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutoFixture;
using AutoFixture.AutoMoq;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Resolvers;
using Tesserafin.Controller.Sorting;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Naming.Common;
using Tesserafin.Server.Core.Library.Resolvers.Audio;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library.LibraryManager;

public class FindExtrasTests
{
    private readonly IItemResolutionService _resolution;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<IItemRepository> _itemRepositoryMock;

    public FindExtrasTests()
    {
        var fixture = new Fixture().Customize(new AutoMoqCustomization());
        fixture.Register(() => new NamingOptions());
        var configMock = fixture.Freeze<Mock<IServerConfigurationManager>>();
        configMock.Setup(c => c.ApplicationPaths.ProgramDataPath).Returns("/data");
        configMock.Setup(c => c.Configuration).Returns(new Tesserafin.Model.Configuration.ServerConfiguration());
        var itemRepository = fixture.Freeze<Mock<IItemRepository>>();
        _itemRepositoryMock = itemRepository;
        itemRepository.Setup(i => i.RetrieveItem(It.IsAny<Guid>())).Returns<BaseItem>(null);
        _fileSystemMock = fixture.Freeze<Mock<IFileSystem>>();
        _fileSystemMock.Setup(f => f.GetFileInfo(It.IsAny<string>())).Returns<string>(path => new FileSystemMetadata { FullName = path });

        // PR76: LibraryManager depends on IItemLookupService + IItemCacheStore (both satisfied by
        // the single internal ItemLookupService in production). Mirror that wiring with one real
        // instance - IItemCacheStore is internal, so AutoMoq could not proxy it anyway.
        var itemLookupService = new Tesserafin.Server.Core.Library.ItemLookupService(itemRepository.Object, configMock.Object);
        fixture.Register<IItemLookupService>(() => itemLookupService);
        fixture.Register<Tesserafin.Server.Core.Library.IItemCacheStore>(() => itemLookupService);
        Tesserafin.Server.Implementations.Tests.Library.LibraryWriteFixture.Register(fixture);

        _resolution = fixture.Create<IItemResolutionService>();
        _resolution.AddParts(
            fixture.Create<IEnumerable<IResolverIgnoreRule>>(),
            new List<IItemResolver> { new AudioResolver(fixture.Create<NamingOptions>()) });

        // This is pretty terrible but unavoidable
        BaseItem.FileSystem ??= fixture.Create<IFileSystem>();
    }

    [Fact]
    public void FindExtras_ReturnsDetachedMetadata_WithoutChangingPublishedTypeOrMixedFolderFlag()
    {
        const string path = "/movies/Up/Up - trailer.mkv";
        var owner = new Movie { Name = "Up", Path = "/movies/Up/Up.mkv" };
        var saved = new Trailer
        {
            Id = _resolution.GetNewItemId(path, typeof(Trailer)),
            Name = "Saved trailer",
            Path = path,
            ExtraType = ExtraType.Unknown,
            IsInMixedFolder = true
        };
        _itemRepositoryMock.Setup(r => r.RetrieveItem(saved.Id)).Returns(saved);

        var result = Assert.Single(_resolution.FindExtras(owner, [new FileSystemMetadata { FullName = path }], new DirectoryService(_fileSystemMock.Object)));

        Assert.NotSame(saved, result);
        Assert.Equal(saved.Id, result.Id);
        Assert.Equal("Saved trailer", result.Name);
        Assert.Equal(ExtraType.Trailer, result.ExtraType);
        Assert.False(result.IsInMixedFolder);
        Assert.Equal(ExtraType.Unknown, saved.ExtraType);
        Assert.True(saved.IsInMixedFolder);
    }

    [Fact]
    public void FindExtras_SeparateMovieFolder_FindsCorrectExtras()
    {
        var owner = new Movie { Name = "Up", Path = "/movies/Up/Up.mkv" };
        var paths = new List<string>
        {
            "/movies/Up/Up.mkv",
            "/movies/Up/Up - trailer.mkv",
            "/movies/Up/Up - sample.mkv",
            "/movies/Up/Up something else.mkv",
            "/movies/Up/Up-extra.mkv"
        };

        var files = paths.Select(p => new FileSystemMetadata
        {
            FullName = p,
            IsDirectory = false
        }).ToList();

        var extras = _resolution.FindExtras(owner, files, new DirectoryService(_fileSystemMock.Object)).OrderBy(e => e.ExtraType).ToList();

        Assert.Equal(3, extras.Count);
        Assert.Equal(ExtraType.Unknown, extras[0].ExtraType);
        Assert.Equal(ExtraType.Trailer, extras[1].ExtraType);
        Assert.Equal(typeof(Trailer), extras[1].GetType());
        Assert.Equal(ExtraType.Sample, extras[2].ExtraType);
    }

    [Fact]
    public void FindExtras_SeparateMovieFolder_CleanExtraNames()
    {
        var owner = new Movie { Name = "Up", Path = "/movies/Up/Up.mkv" };
        var paths = new List<string>
        {
            "/movies/Up/Up.mkv",
            "/movies/Up/Recording the audio[Bluray]-behindthescenes.mkv",
            "/movies/Up/Interview with the dog-interview.mkv",
            "/movies/Up/shorts/Balloons[1080p].mkv"
        };

        var files = paths.Select(p => new FileSystemMetadata
        {
            FullName = p,
            IsDirectory = false
        }).ToList();

        var extras = _resolution.FindExtras(owner, files, new DirectoryService(_fileSystemMock.Object)).OrderBy(e => e.ExtraType).ToList();

        Assert.Equal(3, extras.Count);
        Assert.Equal(ExtraType.BehindTheScenes, extras[0].ExtraType);
        Assert.Equal("Recording the audio", extras[0].Name);
        Assert.Equal(ExtraType.Interview, extras[1].ExtraType);
        Assert.Equal("Interview with the dog", extras[1].Name);
        Assert.Equal(ExtraType.Short, extras[2].ExtraType);
        Assert.Equal("Balloons", extras[2].Name);
    }

    [Fact]
    public void FindExtras_SeparateMovieFolderWithMixedExtras_FindsCorrectExtras()
    {
        var owner = new Movie { Name = "Up", Path = "/movies/Up/Up.mkv" };
        var paths = new List<string>
        {
            "/movies/Up/Up.mkv",
            "/movies/Up/Up - trailer.mkv",
            "/movies/Up/trailers",
            "/movies/Up/theme-music",
            "/movies/Up/theme.mp3",
            "/movies/Up/not a theme.mp3",
            "/movies/Up/behind the scenes",
            "/movies/Up/behind the scenes.mkv",
            "/movies/Up/Up - sample.mkv",
            "/movies/Up/Up something else.mkv",
            "/movies/Up/extras"
        };

        _fileSystemMock.Setup(f => f.GetFiles(
                "/movies/Up/trailers",
                It.IsAny<string[]>(),
                false,
                false))
            .Returns(new List<FileSystemMetadata>
            {
                new()
                {
                    FullName = "/movies/Up/trailers/some trailer.mkv",
                    Name = "some trailer.mkv",
                    IsDirectory = false
                }
            }).Verifiable();

        _fileSystemMock.Setup(f => f.GetFiles(
                "/movies/Up/behind the scenes",
                It.IsAny<string[]>(),
                false,
                false))
            .Returns(new List<FileSystemMetadata>
            {
                new()
                {
                    FullName = "/movies/Up/behind the scenes/the making of Up.mkv",
                    Name = "the making of Up.mkv",
                    IsDirectory = false
                }
            }).Verifiable();

        _fileSystemMock.Setup(f => f.GetFiles(
                "/movies/Up/theme-music",
                It.IsAny<string[]>(),
                false,
                false))
            .Returns(new List<FileSystemMetadata>
            {
                new()
                {
                    FullName = "/movies/Up/theme-music/theme2.mp3",
                    Name = "theme2.mp3",
                    IsDirectory = false
                }
            }).Verifiable();

        _fileSystemMock.Setup(f => f.GetFiles(
                "/movies/Up/extras",
                It.IsAny<string[]>(),
                false,
                false))
            .Returns(new List<FileSystemMetadata>
            {
                new()
                {
                    FullName = "/movies/Up/extras/Honest Trailer.mkv",
                    Name = "Honest Trailer.mkv",
                    IsDirectory = false
                }
            }).Verifiable();

        var files = paths.Select(p => new FileSystemMetadata
        {
            FullName = p,
            Name = Path.GetFileName(p),
            IsDirectory = !Path.HasExtension(p)
        }).ToList();

        var extras = _resolution.FindExtras(owner, files, new DirectoryService(_fileSystemMock.Object)).OrderBy(e => e.ExtraType).ToList();

        _fileSystemMock.Verify();
        Assert.Equal(7, extras.Count);
        Assert.Equal(ExtraType.Unknown, extras[0].ExtraType);
        Assert.Equal(typeof(Video), extras[0].GetType());
        Assert.Equal(ExtraType.Trailer, extras[1].ExtraType);
        Assert.Equal(typeof(Trailer), extras[1].GetType());
        Assert.Equal(ExtraType.Trailer, extras[2].ExtraType);
        Assert.Equal(typeof(Trailer), extras[2].GetType());
        Assert.Equal(ExtraType.BehindTheScenes, extras[3].ExtraType);
        Assert.Equal(ExtraType.Sample, extras[4].ExtraType);
        Assert.Equal(ExtraType.ThemeSong, extras[5].ExtraType);
        Assert.Equal(typeof(Audio), extras[5].GetType());
        Assert.Equal(ExtraType.ThemeSong, extras[6].ExtraType);
        Assert.Equal(typeof(Audio), extras[6].GetType());
    }

    [Fact]
    public void FindExtras_SeparateMovieFolderWithMixedExtras_FindsOnlyExtrasInMovieFolder()
    {
        var owner = new Movie { Name = "Up", Path = "/movies/Up/Up.mkv" };
        var paths = new List<string>
        {
            "/movies/Up/Up.mkv",
            "/movies/Up/trailer.mkv",
            "/movies/Another Movie/trailer.mkv"
        };

        var files = paths.Select(p => new FileSystemMetadata
        {
            FullName = p,
            IsDirectory = false
        }).ToList();

        var extras = _resolution.FindExtras(owner, files, new DirectoryService(_fileSystemMock.Object)).OrderBy(e => e.ExtraType).ToList();

        Assert.Single(extras);
        Assert.Equal(ExtraType.Trailer, extras[0].ExtraType);
        Assert.Equal(typeof(Trailer), extras[0].GetType());
        Assert.Equal("trailer", extras[0].FileNameWithoutExtension);
        Assert.Equal("/movies/Up/trailer.mkv", extras[0].Path);
    }

    [Fact]
    public void FindExtras_SeparateMovieFolderWithParts_FindsCorrectExtras()
    {
        var owner = new Movie { Name = "Up", Path = "/movies/Up/Up - part1.mkv" };
        var paths = new List<string>
        {
            "/movies/Up/Up - part1.mkv",
            "/movies/Up/Up - part2.mkv",
            "/movies/Up/trailer.mkv",
            "/movies/Another Movie/trailer.mkv"
        };

        var files = paths.Select(p => new FileSystemMetadata
        {
            FullName = p,
            IsDirectory = false
        }).ToList();

        var extras = _resolution.FindExtras(owner, files, new DirectoryService(_fileSystemMock.Object)).OrderBy(e => e.ExtraType).ToList();

        Assert.Single(extras);
        Assert.Equal(ExtraType.Trailer, extras[0].ExtraType);
        Assert.Equal(typeof(Trailer), extras[0].GetType());
        Assert.Equal("trailer", extras[0].FileNameWithoutExtension);
        Assert.Equal("/movies/Up/trailer.mkv", extras[0].Path);
    }

    [Fact]
    public void FindExtras_WrongExtensions_FindsNoExtras()
    {
        var owner = new Movie { Name = "Up", Path = "/movies/Up/Up.mkv" };
        var paths = new List<string>
        {
            "/movies/Up/Up.mkv",
            "/movies/Up/trailer.noext",
            "/movies/Up/theme.png",
            "/movies/Up/trailers"
        };

        var files = paths.Select(p => new FileSystemMetadata
        {
            FullName = p,
            Name = Path.GetFileName(p),
            IsDirectory = !Path.HasExtension(p)
        }).ToList();

        _fileSystemMock.Setup(f => f.GetFiles(
                "/movies/Up/trailers",
                It.IsAny<string[]>(),
                false,
                false))
            .Returns(new List<FileSystemMetadata>
            {
                new()
                {
                    FullName = "/movies/Up/trailers/trailer.jpg",
                    Name = "trailer.jpg",
                    IsDirectory = false
                }
            }).Verifiable();

        var extras = _resolution.FindExtras(owner, files, new DirectoryService(_fileSystemMock.Object)).OrderBy(e => e.ExtraType).ToList();

        _fileSystemMock.Verify();
        Assert.Empty(extras);
    }

    [Fact]
    public void FindExtras_SeriesWithTrailers_FindsCorrectExtras()
    {
        var owner = new Series { Name = "Dexter", Path = "/series/Dexter" };
        var paths = new List<string>
        {
            "/series/Dexter/Season 1/S01E01.mkv",
            "/series/Dexter/trailer.mkv",
            "/series/Dexter/trailers/trailer2.mkv",
        };

        var files = paths.Select(p => new FileSystemMetadata
        {
            FullName = p,
            IsDirectory = string.IsNullOrEmpty(Path.GetExtension(p))
        }).ToList();

        var extras = _resolution.FindExtras(owner, files, new DirectoryService(_fileSystemMock.Object)).OrderBy(e => e.ExtraType).ToList();

        Assert.Equal(2, extras.Count);
        Assert.Equal(ExtraType.Trailer, extras[0].ExtraType);
        Assert.Equal(typeof(Trailer), extras[0].GetType());
        Assert.Equal("trailer", extras[0].FileNameWithoutExtension);
        Assert.Equal("/series/Dexter/trailer.mkv", extras[0].Path);
        Assert.Equal("/series/Dexter/trailers/trailer2.mkv", extras[1].Path);
    }
}
