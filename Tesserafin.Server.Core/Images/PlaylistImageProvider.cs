#nullable disable

#pragma warning disable CS1591

using System.Collections.Generic;
using System.Linq;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Drawing;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Playlists;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;

namespace Tesserafin.Server.Core.Images
{
    public class PlaylistImageProvider : BaseDynamicImageProvider<Playlist>
    {
        private readonly LinkedChildResolver _linkedChildren;

        public PlaylistImageProvider(
            IFileSystem fileSystem,
            IProviderManager providerManager,
            IApplicationPaths applicationPaths,
            IImageProcessor imageProcessor,
            LinkedChildResolver linkedChildren)
            : base(fileSystem, providerManager, applicationPaths, imageProcessor)
        {
            _linkedChildren = linkedChildren;
        }

        protected override IReadOnlyList<BaseItem> GetItemsWithImages(BaseItem item)
        {
            var playlist = (Playlist)item;

            return _linkedChildren.GetItems(playlist)
                .Select(subItem =>
                {

                    if (subItem is Episode episode)
                    {
                        var series = episode.Series;
                        if (series is not null && series.HasImage(ImageType.Primary))
                        {
                            return series;
                        }
                    }

                    if (subItem.HasImage(ImageType.Primary))
                    {
                        return subItem;
                    }

                    var parent = subItem.GetOwner() ?? subItem.GetParent();

                    if (parent is not null && parent.HasImage(ImageType.Primary))
                    {
                        if (parent is MusicAlbum)
                        {
                            return parent;
                        }
                    }

                    return null;
                })
                .Where(i => i is not null)
                .DistinctBy(x => x.Id)
                .ToList();
        }
    }
}
