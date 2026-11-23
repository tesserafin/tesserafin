using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Extensions;
using Tesserafin.Model.Querying;

namespace Tesserafin.Server.Core.Library;

/// <inheritdoc />
internal sealed class ItemCatalogueQuery(
    IItemLookupService lookup,
    IItemRepository repository,
    IItemQueryScopeService scope,
    IItemAccessService access,
    IItemCountService counts) : IItemCatalogueQuery
{
    public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery query)
    {
        PrepareGlobalQuery(query);
        return repository.GetItemList(query);
    }

    public QueryResult<BaseItem> GetItems(InternalItemsQuery query)
    {
        PrepareGlobalQuery(query);
        return query.EnableTotalRecordCount
            ? repository.GetItems(query)
            : new QueryResult<BaseItem>(query.StartIndex, null, repository.GetItemList(query));
    }

    public int GetCount(InternalItemsQuery query)
    {
        PrepareGlobalQuery(query);
        return counts.GetCount(query);
    }

    private void PrepareGlobalQuery(InternalItemsQuery query)
    {
        if (query.Recursive && !query.ParentId.IsEmpty())
        {
            var parent = lookup.GetItemById(query.ParentId);
            if (parent is not null)
            {
                scope.SetTopParentIdsOrAncestors(query, [parent]);
            }
        }

        if (query.User is not null)
        {
            scope.AddUserToQuery(query, query.User);
            ItemQueryScopeService.ExcludeRefusedCollections(query, query.User, repository, access);
        }
    }
}
