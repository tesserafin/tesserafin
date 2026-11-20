using System;
using Moq;
using Tesserafin.Common;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Chapters;
using Tesserafin.Controller.Drawing;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Trickplay;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Server.Core.Dto;
using Tesserafin.Server.Core.Playlists;
using Tesserafin.Server.Implementations.Tests.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Dto;

public class DtoServiceImageInheritanceTests
{
    [Fact]
    public void GetBaseItemDto_PlaylistsUserViewWithDisplayParentPrimary_UsesDisplayParentPrimaryImage()
    {
        var displayParent = new PlaylistsFolder
        {
            Id = Guid.NewGuid(),
            ImageInfos =
            [
                new ItemImageInfo
                {
                    Type = ImageType.Primary,
                    Path = "/images/playlists-custom.jpg",
                    DateModified = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc)
                }
            ]
        };

        var userView = new UserView
        {
            Id = Guid.NewGuid(),
            ViewType = CollectionType.playlists,
            DisplayParentId = displayParent.Id,
            ImageInfos =
            [
                new ItemImageInfo
                {
                    Type = ImageType.Primary,
                    Path = "/images/generated.png",
                    DateModified = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc)
                }
            ]
        };

        var dtoService = BuildDtoService(displayParent);

        var dto = dtoService.GetBaseItemDto(userView, new DtoOptions(false));

        Assert.NotNull(dto.ParentPrimaryImageItemId);
        Assert.Equal(displayParent.Id, dto.ParentPrimaryImageItemId);
        Assert.Equal("/images/playlists-custom.jpg", dto.ParentPrimaryImageTag);
        Assert.False(dto.ImageTags?.ContainsKey(ImageType.Primary));
    }

    [Fact]
    public void GetBaseItemDto_PlaylistsUserViewWithoutDisplayParentPrimary_KeepsOwnPrimaryImage()
    {
        var displayParent = new PlaylistsFolder
        {
            Id = Guid.NewGuid(),
            ImageInfos = []
        };

        var userView = new UserView
        {
            Id = Guid.NewGuid(),
            ViewType = CollectionType.playlists,
            DisplayParentId = displayParent.Id,
            ImageInfos =
            [
                new ItemImageInfo
                {
                    Type = ImageType.Primary,
                    Path = "/images/generated.png",
                    DateModified = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc)
                }
            ]
        };

        var dtoService = BuildDtoService(displayParent);

        var dto = dtoService.GetBaseItemDto(userView, new DtoOptions(false));

        Assert.Null(dto.ParentPrimaryImageItemId);
        Assert.Null(dto.ParentPrimaryImageTag);
        Assert.NotNull(dto.ImageTags);
        Assert.True(dto.ImageTags.ContainsKey(ImageType.Primary));
        Assert.Equal("/images/generated.png", dto.ImageTags[ImageType.Primary]);
    }

    private static DtoService BuildDtoService(BaseItem displayParent)
    {
        var libraryManager = new Mock<ILibraryManager>();
        var itemPeopleService = new Mock<IItemPeopleService>();
        var userDataManager = new Mock<IUserDataManager>();
        var imageProcessor = new Mock<IImageProcessor>();
        var providerManager = new Mock<IProviderManager>();
        var recordingsManager = new Mock<IRecordingsManager>();
        var appHost = new Mock<IApplicationHost>();
        var mediaSourceManager = new Mock<IMediaSourceManager>();
        var liveTvManager = new Mock<ILiveTvManager>();
        var trickplayManager = new Mock<ITrickplayManager>();
        var chapterManager = new Mock<IChapterManager>();
        var itemCountService = new Mock<IItemCountService>();
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<DtoService>>();

        libraryManager
            .Setup(x => x.GetItemById(displayParent.Id))
            .Returns(displayParent);

        imageProcessor
            .Setup(x => x.GetImageCacheTag(It.IsAny<BaseItem>(), It.IsAny<ItemImageInfo>()))
            .Returns<BaseItem, ItemImageInfo>((_, image) => image.Path);

        return new DtoService(
            logger.Object,
            Mock.Of<INamedItemCatalog>(),
            libraryManager.Object,
            itemPeopleService.Object,
            userDataManager.Object,
            imageProcessor.Object,
            providerManager.Object,
            recordingsManager.Object,
            appHost.Object,
            mediaSourceManager.Object,
            new Lazy<ILiveTvManager>(() => liveTvManager.Object),
            trickplayManager.Object,
            chapterManager.Object,
            itemCountService.Object,
            Mock.Of<IVideoVersionCatalog>(),
            new Lazy<IChannelManager>(() => throw new InvalidOperationException("Unexpected channel manager access.")),
            LibraryWriteFixture.CreateDeletionContext(Mock.Of<IFileSystem>(MockBehavior.Strict)));
    }
}
