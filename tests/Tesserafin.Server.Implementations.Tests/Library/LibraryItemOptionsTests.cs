using System;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dto;
using Tesserafin.Model.IO;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

public sealed class LibraryItemOptionsTests
{
    [Fact]
    public void EffectiveType_PrefersExactThenInheritedConfiguration_ThenTopLibraryThenConfiguredParents()
    {
        var configuration = new ServerConfiguration();
        var manager = new Mock<IServerConfigurationManager>();
        manager.SetupGet(c => c.Configuration).Returns(configuration);
        var parent = new CollectionFolder { Id = Guid.NewGuid(), Path = "/parent", CollectionType = CollectionType.tvshows };
        var item = new Movie { Path = "/library/feature/film.mkv", ParentId = parent.Id };
        var lookup = new Mock<IItemLookupService>();
        lookup.Setup(l => l.GetItemById(parent.Id)).Returns(parent);
        var root = new Mock<Folder>();
        root.SetupGet(r => r.Children).Returns([new CollectionFolder { Path = parent.Path, CollectionType = CollectionType.homevideos }]);
        var roots = new Mock<IUserRootFolderProvider>();
        roots.Setup(r => r.GetUserRootFolder()).Returns(root.Object);
        var files = new Mock<IFileSystem>();
        files.Setup(f => f.AreEqual(It.IsAny<string>(), It.IsAny<string>())).Returns((string a, string b) => string.Equals(a, b, StringComparison.Ordinal));
        files.Setup(f => f.ContainsSubPath(It.IsAny<string>(), It.IsAny<string>())).Returns((string a, string b) => b.StartsWith(a + "/", StringComparison.Ordinal));
        var options = new LibraryItemOptions(Mock.Of<ICollectionFolderResolver>(), lookup.Object, roots.Object, manager.Object, files.Object);
        configuration.ContentTypes = [new NameValuePair { Name = "/library/feature", Value = "movies" }];
        Assert.Equal(CollectionType.movies, options.GetContentType(item));
        configuration.ContentTypes = [new NameValuePair { Name = "/library", Value = "musicvideos" }];
        Assert.Equal(CollectionType.musicvideos, options.GetContentType(item));
        configuration.ContentTypes = [];
        Assert.Equal(CollectionType.homevideos, options.GetContentType(item));
        root.SetupGet(r => r.Children).Returns([]);
        Assert.Equal(CollectionType.tvshows, options.GetContentType(item));
    }
}
