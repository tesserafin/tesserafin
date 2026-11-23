using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.IO;
using Tesserafin.Server.Core.IO;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.IO;

public sealed class FileRefresherTests
{
    [Fact]
    public async Task UnknownChangedFile_RefreshesTheFirstCatalogueAncestor()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "unindexed", "movie.mkv");
            var folder = new Folder { Id = Guid.NewGuid(), Path = directory.FullName };

            var paths = new Mock<IItemPathLookupService>(MockBehavior.Strict);
            paths.Setup(p => p.FindByPath(path, null)).Returns((BaseItem?)null);
            paths.Setup(p => p.FindByPath(Path.GetDirectoryName(path)!, null)).Returns((BaseItem?)null);
            paths.Setup(p => p.FindByPath(directory.FullName, null)).Returns(folder);
            var items = new Mock<IItemLookupService>(MockBehavior.Strict);
            var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
            var metadata = new FileSystemMetadata { Exists = true, IsDirectory = true };
            fileSystem.Setup(f => f.GetFileSystemInfo(directory.FullName)).Returns(metadata);

            var options = await QueueRefreshAsync(path, paths.Object, items.Object, fileSystem.Object, folder.Id);

            Assert.Equal(MetadataRefreshMode.Default, options.MetadataRefreshMode);
            Assert.Same(metadata, options.DirectoryService.GetDirectory(directory.FullName));
            paths.Verify(p => p.FindByPath(path, null), Times.Once);
            paths.Verify(p => p.FindByPath(Path.GetDirectoryName(path)!, null), Times.Once);
            paths.Verify(p => p.FindByPath(directory.FullName, null), Times.Once);
            paths.VerifyNoOtherCalls();
        }
        finally
        {
            directory.Delete();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletedCatalogueItem_RefreshesItsOwnerBeforeItsParent(bool ownerExists)
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var owner = new Folder { Id = Guid.NewGuid(), Path = directory.FullName };
            var parent = new Folder { Id = Guid.NewGuid(), Path = directory.FullName };
            var deleted = new Folder
            {
                Id = Guid.NewGuid(),
                Path = Path.Combine(directory.FullName, "deleted"),
                OwnerId = owner.Id,
                ParentId = parent.Id
            };
            var paths = new Mock<IItemPathLookupService>(MockBehavior.Strict);
            paths.Setup(p => p.FindByPath(deleted.Path, null)).Returns(deleted);
            var items = new Mock<IItemLookupService>(MockBehavior.Strict);
            items.Setup(i => i.GetItemById(owner.Id)).Returns(ownerExists ? owner : null);
            if (!ownerExists)
            {
                items.Setup(i => i.GetItemById(parent.Id)).Returns(parent);
            }

            await QueueRefreshAsync(deleted.Path, paths.Object, items.Object, Mock.Of<IFileSystem>(), ownerExists ? owner.Id : parent.Id);

            items.Verify(i => i.GetItemById(owner.Id), Times.Once);
            items.Verify(i => i.GetItemById(parent.Id), ownerExists ? Times.Never() : Times.Once());
        }
        finally
        {
            directory.Delete();
        }
    }

    private static async Task<MetadataRefreshOptions> QueueRefreshAsync(
        string path,
        IItemPathLookupService paths,
        IItemLookupService items,
        IFileSystem fileSystem,
        Guid expected)
    {
        var config = new Mock<IServerConfigurationManager>();
        config.SetupGet(c => c.Configuration).Returns(new ServerConfiguration { LibraryMonitorDelay = 0 });
        var queued = new TaskCompletionSource<MetadataRefreshOptions>(TaskCreationOptions.RunContinuationsAsynchronously);
        var providers = new Mock<IProviderManager>(MockBehavior.Strict);
        providers.Setup(p => p.QueueRefresh(expected, It.IsAny<MetadataRefreshOptions>(), RefreshPriority.High))
            .Callback<Guid, MetadataRefreshOptions, RefreshPriority>((_, options, _) => queued.SetResult(options));

        using var refresher = new FileRefresher(path, config.Object, paths, items, new Lazy<IProviderManager>(() => providers.Object), fileSystem, NullLogger.Instance);
        var refreshOptions = await queued.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        providers.Verify(p => p.QueueRefresh(expected, It.IsAny<MetadataRefreshOptions>(), RefreshPriority.High), Times.Once);
        return refreshOptions;
    }
}
