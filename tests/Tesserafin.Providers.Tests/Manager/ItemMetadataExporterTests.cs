using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Model.Configuration;
using Tesserafin.Providers.Manager;
using Xunit;

namespace Tesserafin.Providers.Tests.Manager;

public sealed class ItemMetadataExporterTests
{
    [Fact]
    public async Task SelectedNames_IgnoreCase_AndNonFileExportsLeaveTheMonitorDeferred()
    {
        var item = new Folder { Name = "metadata", Path = "/media/movie.mkv" };
        var chosen = Saver("chosen");
        var other = Saver("other");
        var monitor = new Lazy<ILibraryMonitor>(() => throw new InvalidOperationException("monitor must remain deferred"));
        var service = Service(monitor, "chosen", "other");
        service.AddParts([chosen.Object, other.Object]);

        await service.SaveAsync(item, ItemUpdateType.MetadataEdit, TestContext.Current.CancellationToken, ["CHOSEN"]);

        chosen.Verify(s => s.SaveAsync(item, TestContext.Current.CancellationToken), Times.Once);
        other.Verify(s => s.SaveAsync(It.IsAny<BaseItem>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(monitor.IsValueCreated);
        Assert.NotEqual(default, item.DateLastSaved);
    }

    [Fact]
    public async Task FailedFileExport_CompletesMonitoring_AndContinuesWithTheNextSaver()
    {
        var calls = new List<string>();
        var monitor = new Mock<ILibraryMonitor>();
        monitor.Setup(m => m.ReportFileSystemChangeBeginning("/media/item.nfo")).Callback(() => calls.Add("begin"));
        monitor.Setup(m => m.ReportFileSystemChangeComplete("/media/item.nfo", false)).Callback(() => calls.Add("complete"));
        var file = new Mock<IMetadataFileSaver>();
        file.SetupGet(s => s.Name).Returns("file");
        file.Setup(s => s.IsEnabledFor(It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>())).Returns(true);
        file.Setup(s => s.GetSavePath(It.IsAny<BaseItem>())).Returns("/media/item.nfo");
        file.Setup(s => s.SaveAsync(It.IsAny<BaseItem>(), It.IsAny<CancellationToken>())).Callback(() => calls.Add("file")).ThrowsAsync(new IOException("sidecar is locked"));
        var next = Saver("next");
        next.Setup(s => s.SaveAsync(It.IsAny<BaseItem>(), It.IsAny<CancellationToken>())).Callback(() => calls.Add("next")).Returns(Task.CompletedTask);
        var service = Service(new Lazy<ILibraryMonitor>(() => monitor.Object), "file", "next");
        service.AddParts([file.Object, next.Object]);

        await service.SaveAsync(new Folder { Name = "metadata" }, ItemUpdateType.MetadataEdit, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "begin", "file", "complete", "next" }, calls);
    }

    [Fact]
    public async Task Cancellation_StopsTheBatch_AndCompletesFileMonitoring()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var monitor = new Mock<ILibraryMonitor>();
        var file = new Mock<IMetadataFileSaver>();
        file.SetupGet(s => s.Name).Returns("file");
        file.Setup(s => s.IsEnabledFor(It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>())).Returns(true);
        file.Setup(s => s.GetSavePath(It.IsAny<BaseItem>())).Returns("/media/item.nfo");
        file.Setup(s => s.SaveAsync(It.IsAny<BaseItem>(), It.IsAny<CancellationToken>())).Returns((BaseItem _, CancellationToken token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(token);
        });
        var next = Saver("next");
        var service = Service(new Lazy<ILibraryMonitor>(() => monitor.Object), "file", "next");
        service.AddParts([file.Object, next.Object]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveAsync(new Folder { Name = "metadata" }, ItemUpdateType.MetadataEdit, cancellation.Token));

        monitor.Verify(m => m.ReportFileSystemChangeComplete("/media/item.nfo", false), Times.Once);
        next.Verify(s => s.SaveAsync(It.IsAny<BaseItem>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static ItemMetadataExporter Service(Lazy<ILibraryMonitor> monitor, params string[] enabled)
    {
        var configuration = new Mock<IServerConfigurationManager>();
        configuration.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        var options = new Mock<ILibraryItemOptions>();
        options.Setup(o => o.GetLibraryOptions(It.IsAny<BaseItem>())).Returns(new LibraryOptions { MetadataSavers = enabled });
        return new ItemMetadataExporter(configuration.Object, options.Object, monitor, NullLogger<ItemMetadataExporter>.Instance);
    }

    private static Mock<IMetadataSaver> Saver(string name)
    {
        var saver = new Mock<IMetadataSaver>();
        saver.SetupGet(s => s.Name).Returns(name);
        saver.Setup(s => s.IsEnabledFor(It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>())).Returns(true);
        saver.Setup(s => s.SaveAsync(It.IsAny<BaseItem>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return saver;
    }
}
