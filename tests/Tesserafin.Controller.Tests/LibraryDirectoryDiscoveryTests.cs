using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.IO;
using Xunit;

namespace Tesserafin.Controller.Tests;

public sealed class LibraryDirectoryDiscoveryTests
{
    [Fact]
    public void AggregateFolder_UsesDiscoveredEntriesAndKeepsVirtualChildren()
    {
        var root = new TestAggregateFolder();
        var physical = new Folder { Id = Guid.NewGuid() };
        var virtualFolder = new Folder { Id = Guid.NewGuid() };
        root.AddVirtualChild(virtualFolder);
        FileSystemMetadata[] entries = [new()];
        string[] locations = ["/media"];
        var directories = Mock.Of<IDirectoryService>();
        var resolver = new Mock<IItemResolutionService>(MockBehavior.Strict);
        var cancellationToken = TestContext.Current.CancellationToken;
        resolver.Setup(r => r.DiscoverPhysicalRoot(root, directories)).Returns((entries, locations));
        resolver.Setup(r => r.ResolveFolderChildren(root, entries, directories, cancellationToken)).Returns([physical]);

        Assert.Equal(new[] { physical, virtualFolder }, root.Discover(resolver.Object, directories, cancellationToken));
        Assert.Equal(locations, root.PhysicalLocations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadLibraryDirectory_ResolvesShortcutsAndOnlyFlattensThePhysicalRoot(bool physicalRoot)
    {
        var root = Path.Combine(Path.GetTempPath(), "aggregate");
        var view = Path.Combine(root, "views");
        var definition = Path.Combine(view, "movies");
        var target = Path.Combine(Path.GetTempPath(), "media");
        var child = Path.Combine(target, "nested");
        var primary = Path.Combine(root, "media.mblink");
        var nested = Path.Combine(definition, "nested.mblink");
        var invalid = Path.Combine(root, "invalid.mblink");
        var unreachable = Path.Combine(root, "unreadable.mblink");
        var marker = Path.Combine(root, "movies.collection");
        var directories = new Mock<IDirectoryService>(MockBehavior.Strict);
        directories.Setup(d => d.GetFileSystemEntries(root)).Returns([Entry(view, true), Entry(primary), Entry(invalid), Entry(unreachable), Entry(marker)]);
        if (physicalRoot)
        {
            directories.Setup(d => d.GetFileSystemEntries(view)).Returns([Entry(definition, true)]);
            directories.Setup(d => d.GetFileSystemEntries(definition)).Returns([Entry(nested)]);
        }

        var files = new Mock<IFileSystem>();
        files.Setup(f => f.IsShortcut(It.IsAny<string>())).Returns((string path) => path.EndsWith(".mblink", StringComparison.Ordinal));
        files.Setup(f => f.ResolveShortcut(primary)).Returns("%MEDIA%");
        files.Setup(f => f.ResolveShortcut(nested)).Returns(child);
        files.Setup(f => f.ResolveShortcut(invalid)).Returns(string.Empty);
        files.Setup(f => f.ResolveShortcut(unreachable)).Throws<IOException>();
        files.Setup(f => f.GetDirectoryInfo(It.IsAny<string>())).Returns((string path) => Entry(path, true));
        files.Setup(f => f.ContainsSubPath(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string parent, string path) => path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        var paths = new Mock<IServerApplicationPaths>();
        paths.SetupGet(p => p.VirtualDataPath).Returns("%MEDIA%");
        paths.SetupGet(p => p.DataPath).Returns(target);
        paths.SetupGet(p => p.VirtualInternalMetadataPath).Returns("%METADATA%");
        paths.SetupGet(p => p.InternalMetadataPath).Returns(Path.Combine(Path.GetTempPath(), "metadata"));

        var discovery = FileData.ReadLibraryDirectory(Entry(root, true), directories.Object, files.Object, paths.Object, NullLogger.Instance, physicalRoot);

        Assert.NotNull(discovery.Entries);
        string[] entries = physicalRoot ? [target, marker] : [view, target, marker];
        string[] locations = physicalRoot ? [root, child, target] : [root, target];
        Assert.Equal(entries, discovery.Entries.Select(entry => entry.FullName));
        Assert.Equal(locations, discovery.PhysicalLocations);
        directories.Verify(d => d.GetFileSystemEntries(target), Times.Never);
    }

    [Fact]
    public void ReadLibraryDirectory_DistinguishesAnUnavailableDirectoryFromAnEmptyScan()
    {
        var root = Entry(Path.Combine(Path.GetTempPath(), "missing"));
        var directories = new Mock<IDirectoryService>(MockBehavior.Strict);

        var discovery = FileData.ReadLibraryDirectory(root, directories.Object, Mock.Of<IFileSystem>(), Mock.Of<IServerApplicationPaths>(), NullLogger.Instance, physicalRoot: true);

        Assert.Null(discovery.Entries);
        Assert.Equal(root.FullName, Assert.Single(discovery.PhysicalLocations));
        directories.VerifyNoOtherCalls();

        directories.Setup(d => d.GetFileSystemEntries(root.FullName)).Returns([]);
        var empty = FileData.ReadLibraryDirectory(Entry(root.FullName, true), directories.Object, Mock.Of<IFileSystem>(), Mock.Of<IServerApplicationPaths>(), NullLogger.Instance, physicalRoot: true);

        Assert.NotNull(empty.Entries);
        Assert.Empty(empty.Entries);
    }

    private static FileSystemMetadata Entry(string path, bool isDirectory = false)
        => new() { FullName = path, IsDirectory = isDirectory };

    private sealed class TestAggregateFolder : AggregateFolder
    {
        public IEnumerable<BaseItem> Discover(IItemResolutionService resolution, IDirectoryService directories, CancellationToken cancellationToken)
            => GetNonCachedChildren(resolution, directories, cancellationToken);
    }
}
