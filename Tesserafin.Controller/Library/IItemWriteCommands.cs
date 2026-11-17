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
    /// <summary>Persists a completed shortcut-link snapshot before updating the caller's folder.</summary>
    /// <param name="owner">The shortcut-enabled folder.</param>
    /// <param name="fileSystemChildren">Completed discovery inputs.</param>
    /// <param name="cancellationToken">Cancels reads, admission and persistence.</param>
    /// <returns>Whether shortcut links changed and were saved.</returns>
    Task<bool> RefreshShortcutLinksAsync(Folder owner, IReadOnlyList<FileSystemMetadata> fileSystemChildren, CancellationToken cancellationToken);

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

    /// <summary>Refreshes typed local versions and multipart videos through explicit write ports.</summary>
    /// <param name="owner">The owning file-backed video in the library tree.</param>
    /// <param name="deletions">Explicit catalogue-only cleanup operations.</param>
    /// <param name="options">Refresh options; never mutated.</param>
    /// <param name="cancellationToken">Cancels admission and refresh work.</param>
    /// <returns>Whether the owner's version paths or link count changed.</returns>
    Task<bool> RefreshOwnedVideosAsync(Video owner, IItemDeletionCommands deletions, MetadataRefreshOptions options, CancellationToken cancellationToken);

    /// <summary>Transfers a removed primary's versions before deleting only its catalogue entry.</summary>
    /// <param name="previous">The previous primary identity.</param>
    /// <param name="replacement">The saved replacement primary identity.</param>
    /// <param name="parent">The scan's captured parent.</param>
    /// <param name="deletions">Explicit catalogue-only deletion.</param>
    /// <param name="cancellationToken">Cancels admission and staged work.</param>
    /// <returns>Version transfer, reference rerouting and previous-primary deletion.</returns>
    Task ReplacePrimaryVersionAsync(Video previous, Video replacement, Folder parent, IItemDeletionCommands deletions, CancellationToken cancellationToken);

    /// <summary>Transfers a primary's versions and retains it as a local alternate.</summary>
    /// <param name="previous">The previous primary identity.</param>
    /// <param name="replacement">The saved replacement primary identity.</param>
    /// <param name="cancellationToken">Cancels admission and staged work.</param>
    /// <returns>Version transfer and reference rerouting.</returns>
    Task DemotePrimaryVersionAsync(Video previous, Video replacement, CancellationToken cancellationToken);

    /// <summary>Reconciles one completed discovery snapshot with the folder's current catalogue.</summary>
    /// <param name="parent">The scanned folder.</param>
    /// <param name="currentChildren">The current catalogue snapshot.</param>
    /// <param name="discovered">A completed filesystem discovery snapshot.</param>
    /// <param name="directoryService">Storage accessibility and image directory access.</param>
    /// <param name="allowRemoveRoot">Whether collection folders are checked for removal.</param>
    /// <param name="deletions">Explicit catalogue deletion operations.</param>
    /// <param name="images">Explicit image refresh preparation and commit.</param>
    /// <param name="cancellationToken">Cancels admission and reconciliation operations.</param>
    /// <returns>Retained and refreshable children, or an inaccessible-storage skip.</returns>
    Task<FolderChildReconciliationResult> ReconcileFolderChildrenAsync(Folder parent, IReadOnlyList<BaseItem> currentChildren, IReadOnlyList<BaseItem> discovered, IDirectoryService directoryService, bool allowRemoveRoot, IItemDeletionCommands deletions, IItemImageService images, CancellationToken cancellationToken);

    void CreateItem(BaseItem item, BaseItem? parent);

    void CreateItems(IReadOnlyList<BaseItem> items, BaseItem? parent, CancellationToken cancellationToken);

    Task UpdateItemAsync(BaseItem item, BaseItem? parent, ItemUpdateType reason, CancellationToken cancellationToken);

    Task UpdateItemsAsync(IReadOnlyList<BaseItem> items, BaseItem? parent, ItemUpdateType reason, CancellationToken cancellationToken);

    void UpsertLinkedChild(Guid parentId, Guid childId, LinkedChildType childType, CancellationToken cancellationToken);

    Task RerouteLinkedChildReferencesAsync(Guid fromChildId, Guid toChildId, CancellationToken cancellationToken);

    Task ReattachUserDataAsync(BaseItem item, CancellationToken cancellationToken);
}
