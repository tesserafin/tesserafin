using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;

namespace Tesserafin.Server.Core.Library;

/// <inheritdoc />
internal sealed class VideoVersionCatalog(IItemLookupService items, ILinkedChildrenService links) : IVideoVersionCatalog
{
    public IReadOnlyList<Video> GetVersions(Video video) => video.GetCatalogueVersions(items, links);
}
