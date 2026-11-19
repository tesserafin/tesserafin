using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Api.Controllers;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Drawing;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Drawing;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Xunit;

namespace Tesserafin.Api.Tests.Controllers;

public sealed class ChapterImageControllerTests
{
    private readonly Video _item = new() { Id = Guid.NewGuid() };
    private readonly Mock<IChapterRepository> _chapters = new(MockBehavior.Strict);
    private readonly Mock<IItemWriteCommands> _writes = new(MockBehavior.Strict);
    private readonly Mock<IImageProcessor> _processor = new(MockBehavior.Strict);
    private readonly Mock<IFileSystem> _files = new(MockBehavior.Strict);

    [Fact]
    public async Task GetChapterImage_ProcessesTheChapterThumbnailWithItsTimestamp()
    {
        const string path = "/chapters/second.jpg";
        var modified = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        _chapters.Setup(c => c.GetChapter(_item.Id, 1)).Returns(new ChapterInfo { ImagePath = path, ImageDateModified = modified });
        _processor.Setup(p => p.ProcessImage(It.Is<ImageProcessingOptions>(options =>
            options.ImageIndex == 1
            && options.Image.Type == ImageType.Chapter
            && options.Image.Path == path
            && options.Image.DateModified == modified)))
            .ReturnsAsync((path, "image/jpeg", modified));

        var result = await GetChapterImageAsync(1);

        Assert.Equal(path, Assert.IsType<PhysicalFileResult>(result).FileName);
    }

    [Fact]
    public async Task GetChapterImage_WhenItsPathIsMissing_ReturnsNotFound()
    {
        _chapters.Setup(c => c.GetChapter(_item.Id, 1)).Returns(new ChapterInfo());

        Assert.IsType<NotFoundObjectResult>(await GetChapterImageAsync(1));
        _processor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DeleteChapterImage_UsesTheInjectedFileSystem()
    {
        const string path = "/chapters/first.jpg";
        _chapters.Setup(c => c.GetChapter(_item.Id, 0)).Returns(new ChapterInfo { ImagePath = path });
        _files.Setup(f => f.DeleteFile(path));
        _writes.Setup(w => w.UpdateItemAsync(_item, null, ItemUpdateType.ImageUpdate, CancellationToken.None)).Returns(Task.CompletedTask);

        var result = await CreateController().DeleteItemImage(_item.Id, ImageType.Chapter, null);

        Assert.IsType<NoContentResult>(result);
        _files.Verify(f => f.DeleteFile(path), Times.Once);
        _writes.Verify(w => w.UpdateItemAsync(_item, null, ItemUpdateType.ImageUpdate, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task UpdateChapterImageIndex_SwapsTheTwoChapterThumbnails()
    {
        const string source = "/chapters/first.jpg";
        const string destination = "/chapters/third.jpg";
        _chapters.Setup(c => c.GetChapter(_item.Id, 0)).Returns(new ChapterInfo { ImagePath = source });
        _chapters.Setup(c => c.GetChapter(_item.Id, 2)).Returns(new ChapterInfo { ImagePath = destination });
        _files.Setup(f => f.SwapFiles(source, destination));
        _files.Setup(f => f.GetLastWriteTimeUtc(source)).Returns(DateTime.UnixEpoch);
        _files.Setup(f => f.GetLastWriteTimeUtc(destination)).Returns(DateTime.UnixEpoch);
        _writes.Setup(w => w.UpdateItemAsync(_item, null, ItemUpdateType.ImageUpdate, CancellationToken.None)).Returns(Task.CompletedTask);

        var result = await CreateController().UpdateItemImageIndex(_item.Id, ImageType.Chapter, 0, 2);

        Assert.IsType<NoContentResult>(result);
        _files.Verify(f => f.SwapFiles(source, destination), Times.Once);
        _writes.Verify(w => w.UpdateItemAsync(_item, null, ItemUpdateType.ImageUpdate, CancellationToken.None), Times.Once);
    }

    private Task<ActionResult> GetChapterImageAsync(int index)
        => CreateController().GetItemImageByIndex(
            _item.Id,
            ImageType.Chapter,
            index,
            maxWidth: null,
            maxHeight: null,
            width: null,
            height: null,
            quality: null,
            fillWidth: null,
            fillHeight: null,
            tag: null,
            format: ImageFormat.Jpg,
            percentPlayed: null,
            unplayedCount: null,
            blur: null,
            backgroundColor: null,
            foregroundLayer: null);

    private ImageController CreateController()
    {
        var library = new Mock<ILibraryManager>(MockBehavior.Strict);
        library.Setup(l => l.GetItemById<BaseItem>(_item.Id, Guid.Empty)).Returns(_item);
        return new ImageController(
            _writes.Object,
            Mock.Of<IItemImageService>(MockBehavior.Strict),
            _chapters.Object,
            Mock.Of<IUserManager>(),
            Mock.Of<INamedItemCatalog>(),
            library.Object,
            Mock.Of<IItemPeopleService>(),
            Mock.Of<IProviderManager>(),
            _processor.Object,
            _files.Object,
            NullLogger<ImageController>.Instance,
            Mock.Of<IServerConfigurationManager>(),
            Mock.Of<IApplicationPaths>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }
}
