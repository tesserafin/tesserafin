using System.Collections.Generic;
using System.Linq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Entities;

namespace Tesserafin.Server.Core.Library;

/// <inheritdoc />
internal sealed class ItemExtrasService(IItemCatalogueQuery query) : IItemExtrasService
{
    public IReadOnlyList<BaseItem> GetExtras(BaseItem item, User? user = null, IReadOnlyCollection<ExtraType>? extraTypes = null)
        => query.GetItemList(new InternalItemsQuery(user)
        {
            OwnerIds = item is Video video ? video.GetAllVersions().Select(version => version.Id).ToArray() : [item.Id],
            ExtraTypes = extraTypes?.ToArray() ?? [],
            OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)]
        });
}
