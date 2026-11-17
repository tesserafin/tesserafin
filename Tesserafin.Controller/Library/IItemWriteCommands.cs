#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.IO;

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

    /// <summary>Reconciles and refreshes discovered bonus items, preserving source files and non-extra owned versions.</summary>
    /// <param name="owner">The owning media item.</param>
    /// <param name="deletions">Explicit catalogue deletion operations.</param>
    /// <param name="options">Refresh options; never mutated.</param>
    /// <param name="fileSystemChildren">The owner's filesystem neighbours.</param>
    /// <param name="cancellationToken">Cancellation between discovery and mutations.</param>
    /// <returns>Whether the owner needs saving after extra reconciliation.</returns>
    Task<bool> RefreshExtrasAsync(BaseItem owner, IItemDeletionCommands deletions, MetadataRefreshOptions options, IReadOnlyList<FileSystemMetadata> fileSystemChildren, CancellationToken cancellationToken);

    /// <summary>Copies optional title metadata and refreshes one owned item.</summary>
    /// <param name="owner">The source of title metadata.</param>
    /// <param name="ownedItem">The staged item to refresh.</param>
    /// <param name="copyTitleMetadata">Whether to inherit the owner's descriptive metadata.</param>
    /// <param name="deletions">Explicit deletion operations used during refresh.</param>
    /// <param name="options">Refresh options; never mutated.</param>
    /// <param name="cancellationToken">The refresh cancellation token.</param>
    /// <returns>The owned-item refresh task.</returns>
    Task RefreshOwnedMetadataAsync(BaseItem owner, BaseItem ownedItem, bool copyTitleMetadata, IItemDeletionCommands deletions, MetadataRefreshOptions options, CancellationToken cancellationToken);

    void CreateItem(BaseItem item, BaseItem? parent);

    void CreateItems(IReadOnlyList<BaseItem> items, BaseItem? parent, CancellationToken cancellationToken);

    Task UpdateItemAsync(BaseItem item, BaseItem? parent, ItemUpdateType reason, CancellationToken cancellationToken);

    Task UpdateItemsAsync(IReadOnlyList<BaseItem> items, BaseItem? parent, ItemUpdateType reason, CancellationToken cancellationToken);

    void UpsertLinkedChild(Guid parentId, Guid childId, LinkedChildType childType, CancellationToken cancellationToken);

    Task RerouteLinkedChildReferencesAsync(Guid fromChildId, Guid toChildId, CancellationToken cancellationToken);

    Task ReattachUserDataAsync(BaseItem item, CancellationToken cancellationToken);
}
