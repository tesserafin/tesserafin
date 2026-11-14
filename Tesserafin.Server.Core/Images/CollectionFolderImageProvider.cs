#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using Tesserafin.Api.Extensions;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Drawing;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;

namespace Tesserafin.Server.Core.Images
{
    public class CollectionFolderImageProvider : BaseDynamicImageProvider<CollectionFolder>
    {
        private readonly IItemQueryService _itemQueryService;

        public CollectionFolderImageProvider(IFileSystem fileSystem, IProviderManager providerManager, IApplicationPaths applicationPaths, IImageProcessor imageProcessor, IItemQueryService itemQueryService) : base(fileSystem, providerManager, applicationPaths, imageProcessor)
        {
            _itemQueryService = itemQueryService;
        }

        protected override IReadOnlyList<BaseItem> GetItemsWithImages(BaseItem item)
        {
            var view = (CollectionFolder)item;
            var viewType = view.CollectionType;
            var includeItemTypes = DtoExtensions.GetBaseItemKindsForCollectionType(viewType);
            var recursive = viewType != CollectionType.playlists;

            return _itemQueryService.GetItemList(
                view,
                new InternalItemsQuery
                {
                    CollapseBoxSetItems = false,
                    Recursive = recursive,
                    DtoOptions = new DtoOptions(false),
                    ImageTypes = [ImageType.Primary],
                    Limit = 8,
                    OrderBy = [(ItemSortBy.Random, SortOrder.Ascending)],
                    IncludeItemTypes = includeItemTypes
                });
        }

        protected override bool Supports(BaseItem item)
        {
            return item is CollectionFolder;
        }

        protected override string CreateImage(BaseItem item, IReadOnlyCollection<BaseItem> itemsWithImages, string outputPathWithoutExtension, ImageType imageType, int imageIndex)
        {
            var outputPath = Path.ChangeExtension(outputPathWithoutExtension, ".png");

            if (imageType == ImageType.Primary)
            {
                if (itemsWithImages.Count == 0)
                {
                    return null;
                }

                return CreateThumbCollage(item, itemsWithImages, outputPath, 960, 540);
            }

            return base.CreateImage(item, itemsWithImages, outputPath, imageType, imageIndex);
        }

        protected override bool HasChangedByDate(BaseItem item, ItemImageInfo image)
        {
            var age = DateTime.UtcNow - image.DateModified;
            return age.TotalDays > 7;
        }
    }
}
