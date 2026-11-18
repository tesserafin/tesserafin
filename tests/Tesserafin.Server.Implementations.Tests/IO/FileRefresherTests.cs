using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Model.Configuration;
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
            var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var folder = new Mock<Folder>();
            folder.Object.Id = Guid.NewGuid();
            folder.SetupGet(f => f.Path).Returns(directory.FullName);
            folder.Setup(f => f.ChangedExternally()).Callback(refreshed.SetResult);

            var paths = new Mock<IItemPathLookupService>(MockBehavior.Strict);
            paths.Setup(p => p.FindByPath(path, null)).Returns((BaseItem?)null);
            paths.Setup(p => p.FindByPath(Path.GetDirectoryName(path)!, null)).Returns((BaseItem?)null);
            paths.Setup(p => p.FindByPath(directory.FullName, null)).Returns(folder.Object);
            var config = new Mock<IServerConfigurationManager>();
            config.SetupGet(c => c.Configuration).Returns(new ServerConfiguration { LibraryMonitorDelay = 0 });

            using var refresher = new FileRefresher(path, config.Object, paths.Object, NullLogger.Instance);
            await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

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
}
