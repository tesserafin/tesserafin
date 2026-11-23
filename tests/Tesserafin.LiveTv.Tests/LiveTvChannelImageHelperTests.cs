using System;
using Moq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.LiveTv;
using Tesserafin.LiveTv;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Xunit;

namespace Tesserafin.LiveTv.Tests;

public class LiveTvChannelImageHelperTests
{
    [Fact]
    public void UpdateChannelImageIfNeeded_NoSource_DoesNotUpdate()
    {
        var channel = new LiveTvChannel { Name = "Test Channel" };

        var updated = LiveTvChannelImageHelper.UpdateChannelImageIfNeeded(channel, null, null, Mock.Of<IFileSystem>());

        Assert.False(updated);
        Assert.False(channel.HasImage(ImageType.Primary));
    }

    [Fact]
    public void UpdateChannelImageIfNeeded_WithUrl_AppliesUrl()
    {
        var channel = new LiveTvChannel { Name = "Test Channel" };

        var updated = LiveTvChannelImageHelper.UpdateChannelImageIfNeeded(
            channel,
            null,
            "https://example.com/icon.png",
            Mock.Of<IFileSystem>());

        Assert.True(updated);
        Assert.True(channel.HasImage(ImageType.Primary));
        Assert.Equal("https://example.com/icon.png", channel.GetImagePath(ImageType.Primary));
    }

    [Fact]
    public void UpdateChannelImageIfNeeded_WithLocalPath_UsesSuppliedFileSystem()
    {
        const string Path = "/icons/channel.png";
        var modified = new DateTime(2026, 11, 20, 18, 0, 0, DateTimeKind.Utc);
        var channel = new LiveTvChannel { Name = "Test Channel" };
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        fileSystem.Setup(fs => fs.GetFileInfo(Path))
            .Returns(new FileSystemMetadata { FullName = Path, LastWriteTimeUtc = modified });

        var updated = LiveTvChannelImageHelper.UpdateChannelImageIfNeeded(channel, Path, "https://example.com/icon.png", fileSystem.Object);

        Assert.True(updated);
        var image = channel.GetImageInfo(ImageType.Primary, 0);
        Assert.NotNull(image);
        Assert.Equal(Path, image.Path);
        Assert.Equal(modified, image.DateModified);
    }

    [Fact]
    public void UpdateChannelImageIfNeeded_SameUrl_StillUpdates()
    {
        var channel = new LiveTvChannel { Name = "Test Channel" };
        LiveTvChannelImageHelper.UpdateChannelImageIfNeeded(channel, null, "https://example.com/icon.png", Mock.Of<IFileSystem>());

        var updated = LiveTvChannelImageHelper.UpdateChannelImageIfNeeded(
            channel,
            null,
            "https://example.com/icon.png",
            Mock.Of<IFileSystem>());

        Assert.True(updated);
        Assert.Equal("https://example.com/icon.png", channel.GetImagePath(ImageType.Primary));
    }
}
