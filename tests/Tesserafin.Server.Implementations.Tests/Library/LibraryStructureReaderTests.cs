using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

public sealed class LibraryStructureReaderTests
{
    [Fact]
    public void Read_ExpandsAndSortsLocations_IsolatesBrokenShortcuts_AndKeepsUnmatchedDefinitions()
    {
        var definition = Path.Combine(Path.GetTempPath(), "libraries", "Films");
        var orphan = Path.Combine(Path.GetTempPath(), "libraries", "Unscanned");
        var id = Guid.NewGuid();
        var library = new ThrowingProgressFolder { Id = id, Path = definition };
        library.ImageInfos = [new ItemImageInfo { Path = "primary.png", Type = ImageType.Primary }];
        var root = new Mock<Folder>();
        root.SetupGet(f => f.Children).Returns([library]);
        var roots = new Mock<IUserRootFolderProvider>();
        roots.Setup(r => r.GetUserRootFolder()).Returns(root.Object);
        var configuration = new Mock<IServerConfigurationManager>();
        configuration.SetupGet(c => c.ApplicationPaths.DefaultUserViewsPath).Returns(Path.GetDirectoryName(definition)!);
        var files = new Mock<IFileSystem>();
        files.Setup(f => f.GetDirectoryPaths(It.IsAny<string>(), false)).Returns([definition, orphan]);
        files.Setup(f => f.GetFilePaths(definition, false)).Returns(["z.mblink", "bad.mblink", "a.MBLINK", "options.xml"]);
        files.Setup(f => f.GetFilePaths(orphan, false)).Returns(Array.Empty<string>());
        files.Setup(f => f.ResolveShortcut("z.mblink")).Returns("z");
        files.Setup(f => f.ResolveShortcut("a.MBLINK")).Returns("a");
        files.Setup(f => f.ResolveShortcut("bad.mblink")).Throws(new IOException("broken shortcut"));
        files.Setup(f => f.GetFilePaths(definition, It.IsAny<string[]>(), true, false)).Returns(["unknown.collection", "movies.collection"]);
        files.Setup(f => f.GetFilePaths(orphan, It.IsAny<string[]>(), true, false)).Returns(Array.Empty<string>());
        var host = new Mock<IServerApplicationHost>();
        host.Setup(h => h.ExpandVirtualPath(It.IsAny<string>())).Returns((string p) => "expanded/" + p);
        var options = new LibraryOptions { SaveLocalMetadata = true };
        var policy = new Mock<ILibraryItemOptions>();
        policy.Setup(p => p.GetLibraryOptions(library)).Returns(options);
        var reader = new LibraryStructureReader(configuration.Object, files.Object, host.Object, roots.Object, Mock.Of<IItemLookupService>(), policy.Object, NullLogger<LibraryStructureReader>.Instance);

        var results = reader.GetVirtualFolders();

        var film = results.Single(f => f.Name == "Films");
        Assert.Equal(id.ToString("N"), film.ItemId);
        Assert.Equal(film.ItemId, film.PrimaryImageItemId);
        Assert.Equal(CollectionTypeOptions.movies, film.CollectionType);
        Assert.Equal(new[] { "expanded/a", "expanded/z" }, film.Locations);
        Assert.Same(options, film.LibraryOptions);
        Assert.Null(film.RefreshProgress);
        Assert.Null(film.RefreshStatus);
        var missing = results.Single(f => f.Name == "Unscanned");
        Assert.Null(missing.ItemId);
        Assert.Null(missing.CollectionType);
        Assert.Empty(missing.Locations);
        policy.Verify(p => p.GetLibraryOptions(It.IsAny<BaseItem>()), Times.Once);
    }

    [Fact]
    public void Ownership_RemovesManagerEntryPointsAndProviderEdges_AndReaderCannotWrite()
    {
        var manager = typeof(Tesserafin.Server.Core.Library.LibraryManager);
        Assert.Null(manager.GetMethod("GetVirtualFolders", Type.EmptyTypes));
        Assert.DoesNotContain(typeof(ILibraryManager).GetMethods(), m => m.Name == "GetVirtualFolders");
        var managerDependencies = manager.GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.DoesNotContain(typeof(IServerApplicationHost), managerDependencies);
        var dependencies = typeof(LibraryStructureReader).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();
        foreach (var forbidden in new[] { typeof(ILibraryManager), typeof(IProviderManager), typeof(Lazy<IProviderManager>), typeof(IItemWriteCommands), typeof(ILibraryStructureCommands), typeof(IServiceProvider) })
        {
            Assert.DoesNotContain(forbidden, dependencies);
        }
    }

    private sealed class ThrowingProgressFolder : Folder
    {
        public override double? GetRefreshProgress(IProviderManager providerManager) => throw new InvalidOperationException("Readers must not consult provider statics.");
    }
}
