using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Api.Controllers;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Chapters;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Drawing;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Drawing;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Xunit;

namespace Tesserafin.Api.Tests.Controllers;

public static class ImageControllerTests
{
    [Theory]
    [InlineData("image/apng", ".apng")]
    [InlineData("image/avif", ".avif")]
    [InlineData("image/bmp", ".bmp")]
    [InlineData("image/gif", ".gif")]
    [InlineData("image/x-icon", ".ico")]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("image/png", ".png")]
    [InlineData("image/png; charset=utf-8", ".png")]
    [InlineData("image/svg+xml", ".svg")]
    [InlineData("image/tiff", ".tiff")]
    [InlineData("image/webp", ".webp")]
    public static void TryGetImageExtensionFromContentType_Valid_True(string contentType, string extension)
    {
        Assert.True(ImageController.TryGetImageExtensionFromContentType(contentType, out var ex));
        Assert.Equal(extension, ex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("text/html")]
    public static void TryGetImageExtensionFromContentType_InValid_False(string? contentType)
    {
        Assert.False(ImageController.TryGetImageExtensionFromContentType(contentType, out var ex));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "")]
    [InlineData(true, "chapter.jpg")]
    public static async Task GetItemImageByIndex_ReadsChapterImage(bool hasChapter, string? imageName)
    {
        var item = new Video { Id = Guid.NewGuid() };
        const int ChapterIndex = 2;
        var modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var path = string.IsNullOrEmpty(imageName) ? imageName : Path.GetFullPath(imageName);
        var library = new Mock<ILibraryManager>();
        library.Setup(m => m.GetItemById<BaseItem>(item.Id, Guid.Empty)).Returns(item);
        var chapters = new Mock<IChapterManager>(MockBehavior.Strict);
        chapters.Setup(m => m.GetChapter(item.Id, ChapterIndex))
            .Returns(hasChapter ? new ChapterInfo { ImagePath = path, ImageDateModified = modified } : null);

        var processor = new Mock<IImageProcessor>(MockBehavior.Strict);
        if (!string.IsNullOrEmpty(path))
        {
            processor.Setup(m => m.ProcessImage(It.Is<ImageProcessingOptions>(options =>
                    options.ItemId.Equals(item.Id)
                    && options.ImageIndex == ChapterIndex
                    && options.Image!.Type == ImageType.Chapter
                    && options.Image.Path == path
                    && options.Image.DateModified == modified)))
                .ReturnsAsync((path, "image/jpeg", modified));
        }

        var controller = new ImageController(
            Mock.Of<IItemWriteCommands>(),
            Mock.Of<IItemImageService>(),
            chapters.Object,
            Mock.Of<IUserManager>(),
            Mock.Of<INamedItemCatalog>(),
            library.Object,
            Mock.Of<IItemPeopleService>(),
            Mock.Of<IProviderManager>(),
            processor.Object,
            Mock.Of<IFileSystem>(),
            NullLogger<ImageController>.Instance,
            Mock.Of<IServerConfigurationManager>(),
            Mock.Of<IApplicationPaths>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.GetItemImageByIndex(
            item.Id,
            ImageType.Chapter,
            ChapterIndex,
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

        chapters.Verify(m => m.GetChapter(item.Id, ChapterIndex), Times.Once);
        if (string.IsNullOrEmpty(path))
        {
            Assert.IsType<NotFoundObjectResult>(result);
            return;
        }

        Assert.Equal(path, Assert.IsType<PhysicalFileResult>(result).FileName);
    }
}
