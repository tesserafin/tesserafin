#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tesserafin.Controller;
using Tesserafin.Controller.Drawing;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;

namespace Tesserafin.Server.Core.Images
{
    public class DynamicImageProvider : BaseDynamicImageProvider<UserView>
    {
        private readonly IUserManager _userManager;
        private readonly IItemQueryService _itemQueryService;
        private readonly IItemLookupService _itemLookupService;

        public DynamicImageProvider(IFileSystem fileSystem, IProviderManager providerManager, IServerApplicationPaths applicationPaths, IImageProcessor imageProcessor, IUserManager userManager, IItemQueryService itemQueryService, IItemLookupService itemLookupService)
            : base(fileSystem, providerManager, applicationPaths, imageProcessor)
        {
            _userManager = userManager;
            _itemQueryService = itemQueryService;
            _itemLookupService = itemLookupService;
        }

        protected override IReadOnlyList<BaseItem> GetItemsWithImages(BaseItem item)
        {
            var view = (UserView)item;

            var isUsingCollectionStrip = IsUsingCollectionStrip(view);
            var recursive = isUsingCollectionStrip && view?.ViewType is not null && view.ViewType != CollectionType.boxsets && view.ViewType != CollectionType.playlists;

            var result = _itemQueryService.GetItemList(
                view,
                new InternalItemsQuery
                {
                    User = view.UserId.HasValue ? _userManager.GetUserById(view.UserId.Value) : null,
                    CollapseBoxSetItems = false,
                    Recursive = recursive,
                    ExcludeItemTypes = new[] { BaseItemKind.UserView, BaseItemKind.CollectionFolder, BaseItemKind.Person },
                    DtoOptions = new DtoOptions(false)
                });

            var items = result.Select(i =>
            {
                if (i is Episode episode)
                {
                    var series = episode.GetSeries(_itemLookupService);
                    if (series is not null)
                    {
                        return series;
                    }

                    return episode;
                }

                if (i is Season season)
                {
                    var series = season.GetSeries(_itemLookupService);
                    if (series is not null)
                    {
                        return series;
                    }

                    return season;
                }

                if (i is Audio audio)
                {
                    var album = audio.AlbumEntity;
                    if (album is not null && album.HasImage(ImageType.Primary))
                    {
                        return album;
                    }
                }

                return i;
            }).DistinctBy(x => x.Id);

            List<BaseItem> returnItems;
            if (isUsingCollectionStrip)
            {
                returnItems = items
                    .Where(i => i.HasImage(ImageType.Primary) || i.HasImage(ImageType.Thumb))
                    .ToList();
                returnItems.Shuffle();
                return returnItems;
            }

            returnItems = items
                .Where(i => i.HasImage(ImageType.Primary))
                .ToList();
            returnItems.Shuffle();
            return returnItems;
        }

        protected override bool Supports(BaseItem item)
        {
            if (item is UserView view)
            {
                return IsUsingCollectionStrip(view);
            }

            return false;
        }

        private static bool IsUsingCollectionStrip(UserView view)
        {
            CollectionType[] collectionStripViewTypes =
            {
                CollectionType.movies,
                CollectionType.tvshows,
                CollectionType.playlists
            };

            return view?.ViewType is not null && collectionStripViewTypes.Contains(view.ViewType.Value);
        }

        protected override string CreateImage(BaseItem item, IReadOnlyCollection<BaseItem> itemsWithImages, string outputPathWithoutExtension, ImageType imageType, int imageIndex)
        {
            if (itemsWithImages.Count == 0)
            {
                return null;
            }

            var outputPath = Path.ChangeExtension(outputPathWithoutExtension, ".png");

            return CreateThumbCollage(item, itemsWithImages, outputPath, 960, 540);
        }
    }
}
