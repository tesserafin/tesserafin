#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Entities;

namespace Tesserafin.Controller.Library;

/// <summary>Item graph writes, staged metadata and post-persistence publication.</summary>
public interface IItemWriteCommands
{
    /// <summary>Explicitly materializes a supported named catalogue item, or returns its existing record.</summary>
    /// <param name="name">The catalogue name to materialize.</param>
    /// <param name="cancellationToken">Cancels admission and persistence.</param>
    /// <typeparam name="T">A supported named catalogue item type.</typeparam>
    /// <returns>The existing or newly persisted item.</returns>
    T GetOrCreateNamedItem<T>(string name, CancellationToken cancellationToken)
        where T : BaseItem, new();

    void CreateItem(BaseItem item, BaseItem? parent);

    void CreateItems(IReadOnlyList<BaseItem> items, BaseItem? parent, CancellationToken cancellationToken);

    Task UpdateItemAsync(BaseItem item, BaseItem? parent, ItemUpdateType reason, CancellationToken cancellationToken);

    Task UpdateItemsAsync(IReadOnlyList<BaseItem> items, BaseItem? parent, ItemUpdateType reason, CancellationToken cancellationToken);

    void UpsertLinkedChild(Guid parentId, Guid childId, LinkedChildType childType, CancellationToken cancellationToken);

    Task RerouteLinkedChildReferencesAsync(Guid fromChildId, Guid toChildId, CancellationToken cancellationToken);

    Task ReattachUserDataAsync(BaseItem item, CancellationToken cancellationToken);
}
