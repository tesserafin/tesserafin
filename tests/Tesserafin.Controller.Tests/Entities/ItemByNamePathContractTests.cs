using System;
using System.IO;
using Moq;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Model.IO;
using Xunit;

namespace Tesserafin.Controller.Tests.Entities;

/// <summary>
/// Item-by-name entities compose their directory from a name that originates outside the server —
/// media metadata, an NFO file, a provider response. Each composed path must be a direct child of the
/// entity's own root; before the leaf-name contract, a name made only of dots or white space was
/// reduced to the empty string and the composed path became the root itself.
/// </summary>
public sealed class ItemByNamePathContractTests : IDisposable
{
    private readonly DirectoryInfo _tmp;
    private readonly IFileSystem _fileSystem;
    private readonly IServerApplicationPaths _paths;

    public ItemByNamePathContractTests()
    {
        _tmp = Directory.CreateTempSubdirectory("ibn-contract-");

        var paths = new Mock<IServerApplicationPaths>();
        paths.SetupGet(p => p.ArtistsPath).Returns(CreateRoot("artists"));
        paths.SetupGet(p => p.GenrePath).Returns(CreateRoot("genres"));
        paths.SetupGet(p => p.MusicGenrePath).Returns(CreateRoot("musicgenres"));
        paths.SetupGet(p => p.StudioPath).Returns(CreateRoot("studios"));
        paths.SetupGet(p => p.YearPath).Returns(CreateRoot("years"));
        paths.SetupGet(p => p.PeoplePath).Returns(CreateRoot("people"));

        var fileSystem = new Mock<IFileSystem>();
        fileSystem.Setup(f => f.GetValidFilename(It.IsAny<string>()))
            .Returns<string>(s => s.Replace('/', ' ').Replace('\\', ' ').Replace(':', ' '));

        _paths = paths.Object;
        _fileSystem = fileSystem.Object;
    }

    public static TheoryData<string> HostileNames { get; } = new()
    {
        ".",
        "..",
        "...",
        "  ..  ",
        string.Empty,
        "   ",
    };

    public static TheoryData<string> LegitimateNames { get; } = new()
    {
        "Sigur Rós",
        "AC-DC",
        "Rock & Roll",
        "東京事変",
        "A.K.A",
    };

    [Theory]
    [MemberData(nameof(HostileNames))]
    public void GetPath_HostileName_Rejected(string name)
    {
        Assert.Throws<ArgumentException>(() => MusicArtist.GetPath(name, _fileSystem, _paths.ArtistsPath));
        Assert.Throws<ArgumentException>(() => Genre.GetPath(name, _fileSystem, _paths.GenrePath));
        Assert.Throws<ArgumentException>(() => MusicGenre.GetPath(name, _fileSystem, _paths.MusicGenrePath));
        Assert.Throws<ArgumentException>(() => Studio.GetPath(name, _fileSystem, _paths.StudioPath));
        Assert.Throws<ArgumentException>(() => Year.GetPath(name, _fileSystem, _paths.YearPath));
        Assert.Throws<ArgumentException>(() => Person.GetPath(name, _fileSystem, _paths.PeoplePath));
    }

    [Theory]
    [MemberData(nameof(HostileNames))]
    public void GetPath_HostileName_NeverResolvesToTheRoot(string name)
    {
        // The pre-fix failure mode: the composed path equalled the item-by-name root, so a later
        // Directory.CreateDirectory / delete operated on every item under that root at once.
        foreach (var (root, act) in Routes())
        {
            string? composed = null;
            try
            {
                composed = act(name);
            }
            catch (ArgumentException)
            {
                continue;
            }

            Assert.NotEqual(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(composed)));
        }
    }

    [Theory]
    [MemberData(nameof(LegitimateNames))]
    public void GetPath_LegitimateName_IsPreservedAsDirectChild(string name)
    {
        Assert.Equal(name, Path.GetFileName(MusicArtist.GetPath(name, _fileSystem, _paths.ArtistsPath)));
        Assert.Equal(name, Path.GetFileName(Genre.GetPath(name, _fileSystem, _paths.GenrePath)));
        Assert.Equal(name, Path.GetFileName(MusicGenre.GetPath(name, _fileSystem, _paths.MusicGenrePath)));
        Assert.Equal(name, Path.GetFileName(Studio.GetPath(name, _fileSystem, _paths.StudioPath)));
        Assert.Equal(name, Path.GetFileName(Year.GetPath(name, _fileSystem, _paths.YearPath)));

        var paths = _paths;
        Assert.Equal(Path.Combine(paths.ArtistsPath, name), MusicArtist.GetPath(name, _fileSystem, _paths.ArtistsPath));
        Assert.Equal(Path.Combine(paths.GenrePath, name), Genre.GetPath(name, _fileSystem, _paths.GenrePath));
        Assert.Equal(Path.Combine(paths.MusicGenrePath, name), MusicGenre.GetPath(name, _fileSystem, _paths.MusicGenrePath));
        Assert.Equal(Path.Combine(paths.StudioPath, name), Studio.GetPath(name, _fileSystem, _paths.StudioPath));
        Assert.Equal(Path.Combine(paths.YearPath, name), Year.GetPath(name, _fileSystem, _paths.YearPath));
    }

    [Theory]
    [InlineData("../../etc/shadow")]
    [InlineData("/etc/shadow")]
    [InlineData("a\\..\\..\\b")]
    public void GetPath_TraversalAttempt_StaysUnderRoot(string name)
    {
        var artistsRoot = Path.GetFullPath(_paths.ArtistsPath);

        var path = Path.GetFullPath(MusicArtist.GetPath(name, _fileSystem, _paths.ArtistsPath));

        Assert.StartsWith(artistsRoot + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(artistsRoot),
            Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void GetPath_Person_UsesPrefixSubdirectoryUnderRoot()
    {
        var peopleRoot = Path.GetFullPath(_paths.PeoplePath);

        var path = Path.GetFullPath(Person.GetPath("Zoe Saldana", _fileSystem, _paths.PeoplePath));

        Assert.Equal(Path.Combine(peopleRoot, "Z", "Zoe Saldana"), path);
    }

    /// <summary>
    /// Databases written before the contract existed can hold an item whose <c>Path</c> is the
    /// item-by-name root itself. Refreshing such a row re-derives its path through
    /// <c>GetPath(name, normalizeName: false)</c>, which passes the persisted value straight into
    /// the contract. That must resolve to a direct child rather than back onto the root.
    /// </summary>
    [Fact]
    public void Rebase_PersistedPathEqualToTheRoot_ResolvesToADirectChild()
    {
        var artistsRoot = _paths.ArtistsPath;

        // A row written before the contract existed: Path is the root itself.
        var persisted = new MusicArtist { Name = "..", Path = artistsRoot };

        var rebased = MusicArtist.GetPath(Path.GetFileName(persisted.Path), _fileSystem, artistsRoot, normalizeName: false);

        Assert.Equal(Path.Combine(artistsRoot, Path.GetFileName(artistsRoot)), rebased);
        Assert.NotEqual(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(artistsRoot)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(rebased)));
    }

    [Fact]
    public void Rebase_PersistedOrdinaryPath_IsUnchanged()
    {
        var artistsRoot = _paths.ArtistsPath;
        var persisted = new MusicArtist { Name = "Sigur Rós", Path = Path.Combine(artistsRoot, "Sigur Rós") };

        Assert.Equal(persisted.Path, MusicArtist.GetPath(Path.GetFileName(persisted.Path), _fileSystem, artistsRoot, normalizeName: false));
    }

    [Theory]
    [InlineData("with:colon")]
    [InlineData("with\\backslash")]
    public void GetPath_PersistedLeafRejectedByTheContract_ThrowsRatherThanEscaping(string persistedLeaf)
    {
        // normalizeName: false is the rebasing overload — it does not run GetValidFilename, so a
        // leaf that a pre-contract server wrote to disk reaches the contract unchanged. Rejection is
        // the documented outcome; the caller (a metadata refresh) logs and continues.
        Assert.Throws<ArgumentException>(() => MusicArtist.GetPath(persistedLeaf, _fileSystem, _paths.ArtistsPath, normalizeName: false));
    }

    public void Dispose()
    {
        _tmp.Delete(true);
    }

    private (string Root, Func<string, string> Act)[] Routes()
    {
        var paths = _paths;
        return
        [
            (paths.ArtistsPath, name => MusicArtist.GetPath(name, _fileSystem, paths.ArtistsPath)),
            (paths.GenrePath, name => Genre.GetPath(name, _fileSystem, paths.GenrePath)),
            (paths.MusicGenrePath, name => MusicGenre.GetPath(name, _fileSystem, paths.MusicGenrePath)),
            (paths.StudioPath, name => Studio.GetPath(name, _fileSystem, paths.StudioPath)),
            (paths.YearPath, name => Year.GetPath(name, _fileSystem, paths.YearPath)),
            (paths.PeoplePath, name => Person.GetPath(name, _fileSystem, paths.PeoplePath)),
        ];
    }

    private string CreateRoot(string name)
        => Directory.CreateDirectory(Path.Combine(_tmp.FullName, name)).FullName;
}
