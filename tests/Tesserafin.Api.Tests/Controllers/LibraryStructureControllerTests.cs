using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Tesserafin.Api.Controllers;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;
using Xunit;

namespace Tesserafin.Api.Tests.Controllers;

public sealed class LibraryStructureControllerTests
{
    [Fact]
    public void GetVirtualFolders_ProjectsActiveQueuedAndIdleFromInjectedProvider_AndPreservesUnscannedDefinitions()
    {
        var active = Guid.NewGuid();
        var queued = Guid.NewGuid();
        var idle = Guid.NewGuid();
        var folders = new List<VirtualFolderInfo>
        {
            new() { Name = "active", ItemId = active.ToString("N") },
            new() { Name = "queued", ItemId = queued.ToString("N") },
            new() { Name = "idle", ItemId = idle.ToString("N") },
            new() { Name = "unscanned" }
        };
        var reader = new Mock<ILibraryStructureReader>();
        reader.Setup(r => r.GetVirtualFolders()).Returns(folders);
        var providers = new Mock<IProviderManager>();
        providers.Setup(p => p.GetRefreshQueue()).Returns(new HashSet<Guid> { active, queued });
        providers.Setup(p => p.GetRefreshProgress(active)).Returns(42.5);
        providers.Setup(p => p.GetRefreshProgress(queued)).Returns((double?)null);
        providers.Setup(p => p.GetRefreshProgress(idle)).Returns((double?)null);
        var controller = new LibraryStructureController(reader.Object, providers.Object, Mock.Of<ILibraryStructureCommands>());

        var result = controller.GetVirtualFolders();

        Assert.Same(folders, Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Equal("Active", folders[0].RefreshStatus);
        Assert.Equal(42.5, folders[0].RefreshProgress);
        Assert.Equal("Queued", folders[1].RefreshStatus);
        Assert.Equal("Idle", folders[2].RefreshStatus);
        Assert.Null(folders[3].RefreshStatus);
        providers.Verify(p => p.GetRefreshQueue(), Times.Once);
        providers.Verify(p => p.GetRefreshProgress(It.IsAny<Guid>()), Times.Exactly(3));
    }
}
