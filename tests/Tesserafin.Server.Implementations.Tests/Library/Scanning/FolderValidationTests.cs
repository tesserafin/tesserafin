using System;
using System.Threading.Tasks;
using Moq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library.Scanning;

public sealed class FolderValidationTests
{
    [Fact]
    public async Task ValidateChildren_RejectsAncestorCycleAndCompletesProgress()
    {
        var first = new Folder { Id = Guid.NewGuid() };
        var second = new Folder { Id = Guid.NewGuid(), ParentId = first.Id };
        first.ParentId = second.Id;
        var folder = new Folder { Id = Guid.NewGuid(), ParentId = first.Id };

        var lookup = new Mock<IItemLookupService>(MockBehavior.Strict);
        lookup.SetupSequence(service => service.GetItemById(first.Id)).Returns(first).Returns(first);
        lookup.SetupSequence(service => service.GetItemById(second.Id)).Returns(second);

        var refreshProgress = new Mock<ILibraryRefreshProgress>(MockBehavior.Strict);
        refreshProgress.Setup(progress => progress.OnRefreshStart(folder));
        refreshProgress.Setup(progress => progress.OnRefreshComplete(folder));
        var context = new Mock<IFolderValidationServices>(MockBehavior.Strict);
        context.SetupGet(services => services.Lookup).Returns(lookup.Object);
        context.SetupGet(services => services.RefreshProgress).Returns(refreshProgress.Object);
        var options = new MetadataRefreshOptions(Mock.Of<IDirectoryService>(MockBehavior.Strict));

        await Assert.ThrowsAsync<InvalidOperationException>(() => folder.ValidateChildren(
            context.Object, new Progress<double>(), options, cancellationToken: TestContext.Current.CancellationToken));

        lookup.Verify(service => service.GetItemById(first.Id), Times.Exactly(2));
        lookup.Verify(service => service.GetItemById(second.Id), Times.Once);
        refreshProgress.Verify(progress => progress.OnRefreshStart(folder), Times.Once);
        refreshProgress.Verify(progress => progress.OnRefreshComplete(folder), Times.Once);
    }
}
