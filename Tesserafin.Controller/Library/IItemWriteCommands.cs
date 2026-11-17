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
    void CreateItem(BaseItem item, BaseItem? parent);

    void CreateItems(IReadOnlyList<BaseItem> items, BaseItem? parent, CancellationToken cancellationToken);

    Task UpdateItemAsync(BaseItem item, BaseItem? parent, ItemUpdateType reason, CancellationToken cancellationToken);

    Task UpdateItemsAsync(IReadOnlyList<BaseItem> items, BaseItem? parent, ItemUpdateType reason, CancellationToken cancellationToken);

    void UpsertLinkedChild(Guid parentId, Guid childId, LinkedChildType childType, CancellationToken cancellationToken);

    Task RerouteLinkedChildReferencesAsync(Guid fromChildId, Guid toChildId, CancellationToken cancellationToken);

    Task ReattachUserDataAsync(BaseItem item, CancellationToken cancellationToken);
}
