using System;
using Moq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Xunit;

namespace Tesserafin.Controller.Tests.Entities;

/// <summary>
/// Entities read the catalogue through the services attached to them, never through statics.
/// </summary>
public sealed class ItemReadServicesAttachmentTests
{
    [Fact]
    public void Folder_LoadsItsChildrenThroughItsReadServices()
    {
        var folder = new Folder { Id = Guid.NewGuid() };
        var child = new Folder { Id = Guid.NewGuid() };
        var reads = new Mock<IItemReadServices>();
        reads.Setup(r => r.GetChildren(folder)).Returns([child]);
        folder.ReadServices = reads.Object;

        Assert.Same(child, Assert.Single(folder.Children));
    }

    [Fact]
    public void Folder_WithoutReadServices_FailsLoudly()
    {
        var folder = new Folder { Id = Guid.NewGuid() };

        var failure = Assert.Throws<InvalidOperationException>(() => folder.Children);
        Assert.Contains("no catalogue read services", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Video_IsLiveTvWhileItsPathIsRecording()
    {
        var video = new Video { Id = Guid.NewGuid(), Path = "/rec/a.ts" };
        video.ReadServices = Mock.Of<IItemReadServices>(r => r.IsActiveRecording("/rec/a.ts"));

        Assert.Equal(SourceType.LiveTV, video.SourceType);
    }

    [Fact]
    public void Video_NotYetInTheCatalogue_IsALibraryItem()
        => Assert.Equal(SourceType.Library, new Video { Id = Guid.NewGuid(), Path = "/rec/a.ts" }.SourceType);
}
