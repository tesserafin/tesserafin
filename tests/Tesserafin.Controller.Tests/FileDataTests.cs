using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.IO;
using Xunit;

namespace Tesserafin.Controller.Tests;

public sealed class FileDataTests
{
    [Fact]
    public void GetFilteredEntries_ExpandsNestedShortcutsFromResolutionPaths()
    {
        const string root = "/libraries";
        const string definition = root + "/Movies";
        const string shortcut = definition + "/movies.mblink";
        const string unresolved = definition + "/unavailable.mblink";
        const string media = "/data/Movies";

        var paths = new Mock<IServerApplicationPaths>(MockBehavior.Strict);
        paths.SetupGet(p => p.VirtualDataPath).Returns("%data%");
        paths.SetupGet(p => p.DataPath).Returns("/data");
        paths.SetupGet(p => p.VirtualInternalMetadataPath).Returns("%metadata%");
        paths.SetupGet(p => p.InternalMetadataPath).Returns("/metadata");
        var args = new ItemResolveArgs(paths.Object, Mock.Of<IItemResolutionPolicy>())
        {
            FileInfo = new FileSystemMetadata { FullName = root, IsDirectory = true }
        };

        var directories = new Mock<IDirectoryService>(MockBehavior.Strict);
        directories.Setup(d => d.GetFileSystemEntries(root)).Returns([new FileSystemMetadata { FullName = definition, IsDirectory = true }]);
        directories.Setup(d => d.GetFileSystemEntries(definition))
            .Returns([new FileSystemMetadata { FullName = shortcut }, new FileSystemMetadata { FullName = unresolved }]);

        var files = new Mock<IFileSystem>(MockBehavior.Strict);
        files.Setup(f => f.IsShortcut(definition)).Returns(false);
        files.Setup(f => f.IsShortcut(shortcut)).Returns(true);
        files.Setup(f => f.IsShortcut(unresolved)).Returns(true);
        files.Setup(f => f.ResolveShortcut(shortcut)).Returns("%DATA%/Movies");
        files.Setup(f => f.ResolveShortcut(unresolved)).Returns((string?)null);
        var resolved = new FileSystemMetadata { FullName = media, IsDirectory = true };
        files.Setup(f => f.GetDirectoryInfo(media)).Returns(resolved);

        var result = FileData.GetFilteredFileSystemEntries(
            directories.Object,
            root,
            files.Object,
            NullLogger.Instance,
            args,
            flattenFolderDepth: 1);

        Assert.Same(resolved, Assert.Single(result));
        Assert.Equal([root, media], args.PhysicalLocations);
    }
}
