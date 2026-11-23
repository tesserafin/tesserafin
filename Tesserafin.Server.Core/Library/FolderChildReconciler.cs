using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Extensions;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;

namespace Tesserafin.Server.Core.Library;

/// <summary>Reconciles one completed discovery snapshot through explicit lifecycle commands.</summary>
internal sealed class FolderChildReconciler(IItemLookupService lookup, IFileSystem files, ILogger<FolderChildReconciler> logger)
{
    public async Task<FolderChildReconciliationResult> ReconcileAsync(Folder parent, IReadOnlyList<BaseItem> currentSnapshot, IReadOnlyList<BaseItem> discovered, IDirectoryService directoryService, bool allowRemoveRoot, IItemWriteCommands writes, IItemDeletionCommands deletions, IItemImageService images, IChannelManager channels, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsLibraryFolderAccessible(directoryService, parent, allowRemoveRoot))
        {
            return new FolderChildReconciliationResult([], [], true);
        }

        var resolvedChildren = Admit(discovered);
        var validChildren = new List<BaseItem>();
        var accessibleChildren = new List<BaseItem>();
        // Build a dictionary of the current children we have now by Id so we can compare quickly and easily
        var currentChildren = BuildCurrentChildren(currentSnapshot);

        // Create a list for our validated children
        var newItems = new List<BaseItem>();
        var actuallyRemoved = new List<BaseItem>();

        // Build a reverse path→item lookup for detecting type changes
        var currentChildrenByPath = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in currentChildren)
        {
            if (!string.IsNullOrEmpty(kvp.Value.Path))
            {
                currentChildrenByPath.TryAdd(kvp.Value.Path, kvp.Value);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        foreach (var child in resolvedChildren)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsLibraryFolderAccessible(directoryService, child, allowRemoveRoot))
            {
                // Preserve inaccessible items so they aren't treated as removed.
                if (currentChildren.TryGetValue(child.Id, out var childrenToKeep)
                    || (!string.IsNullOrEmpty(child.Path) && currentChildrenByPath.TryGetValue(child.Path, out childrenToKeep)))
                {
                    validChildren.Add(childrenToKeep);
                }

                continue;
            }

            if (currentChildren.TryGetValue(child.Id, out var currentChild))
            {
                var staged = currentChild.CopyWithResolvedMetadata(child, out var updateType);
                if (updateType > ItemUpdateType.None)
                {
                    await writes.UpdateItemAsync(staged, currentChild.GetParent(lookup), ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);
                    validChildren.Add(staged);
                    accessibleChildren.Add(staged);
                }
                else
                {
                    // metadata is up-to-date; make sure DB has correct images dimensions and hash
                    var imageUpdate = await images.PrepareRefreshAsync(currentChild, false, true, cancellationToken).ConfigureAwait(false);
                    await using (imageUpdate.ConfigureAwait(false))
                    {
                        await imageUpdate.CommitAsync(cancellationToken).ConfigureAwait(false);
                    }

                    validChildren.Add(currentChild);
                    accessibleChildren.Add(currentChild);
                }

                continue;
            }

            // Check if an existing item occupies the same path with different type/ID
            if (!string.IsNullOrEmpty(child.Path)
                && currentChildrenByPath.TryGetValue(child.Path, out var staleItem)
                && !staleItem.Id.Equals(child.Id))
            {
                logger.LogInformation(
                    "Item type changed at {Path}: {OldType} -> {NewType}, removing stale entry",
                    child.Path,
                    staleItem.GetType().Name,
                    child.GetType().Name);

                currentChildren.Remove(staleItem.Id);
                currentChildrenByPath.Remove(child.Path);
                await deletions.DeleteAsync(staleItem.CopyForWrite(), new DeleteOptions { DeleteFileLocation = false }, parent, cancellationToken).ConfigureAwait(false);
                actuallyRemoved.Add(staleItem);
            }

            // Brand new item - needs to be added
            var created = child.CopyForWrite();
            created.SetParent(parent);
            newItems.Add(created);
            validChildren.Add(created);
            accessibleChildren.Add(created);
        }

        // That's all the new and changed ones - now see if any have been removed and need cleanup
        var itemsRemoved = currentChildren.Values.Except(validChildren).ToList();

        // If it's an AggregateFolder, don't remove
        // Collect replaced primaries for deferred deletion (after CreateItems)
        var replacedPrimaries = new List<(Video OldPrimary, Video NewPrimary)>();

        // Build a set of paths that are alternate versions of valid children
        // These items should not be deleted - they're managed by their primary video
        var alternateVersionPaths = validChildren
            .OfType<Video>()
            .SelectMany(v => v.LocalAlternateVersions ?? [])
            .Where(p => !string.IsNullOrEmpty(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (itemsRemoved.Count > 0)
        {
            foreach (var item in itemsRemoved)
            {
                if (!item.CanDelete(channels))
                {
                    logger.LogDebug("Item marked as non-removable, skipping: {Path}", item.Path ?? item.Name);
                    continue;
                }

                // Skip items that are alternate versions of another video
                if (item is Video video)
                {
                    // Check if path is in LocalAlternateVersions of any valid child
                    if (!string.IsNullOrEmpty(item.Path) && alternateVersionPaths.Contains(item.Path))
                    {
                        logger.LogDebug("Item path matches an alternate version, skipping deletion: {Path}", item.Path);
                        continue;
                    }
                }

                // Defer deletion if this primary video is being replaced by a new primary
                // that takes over its alternates. Deleting now would trigger premature
                // promotion inside DeleteItem and write stale paths to collection NFOs.
                if (item is Video primaryVideo
                    && !primaryVideo.PrimaryVersionId.HasValue
                    && primaryVideo.OwnerId.IsEmpty()
                    && (primaryVideo.LocalAlternateVersions ?? []).Any(p => alternateVersionPaths.Contains(p)))
                {
                    var newPrimary = validChildren
                        .OfType<Video>()
                        .FirstOrDefault(v => (v.LocalAlternateVersions ?? [])
                            .Any(p => (primaryVideo.LocalAlternateVersions ?? [])
                                .Any(op => string.Equals(op, p, StringComparison.OrdinalIgnoreCase))));
                    if (newPrimary is not null)
                    {
                        logger.LogDebug("Deferring deletion of replaced primary: {Path}", item.Path);
                        replacedPrimaries.Add((primaryVideo, newPrimary));
                        actuallyRemoved.Add(item);
                        continue;
                    }
                }

                if (!string.IsNullOrEmpty(item.Path) && files.IsPathFile(item.Path))
                {
                    logger.LogDebug("Removed item: {Path}", item.Path);

                    actuallyRemoved.Add(item);
                    await deletions.DeleteAsync(item.CopyForWrite(), new DeleteOptions { DeleteFileLocation = false }, parent, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (newItems.Count > 0)
        {
            writes.CreateItems(newItems, parent, cancellationToken);
        }

        // Process deferred replaced-primary deletions now that new primaries exist in DB/cache.
        // This avoids the premature promotion that would occur if DeleteItem ran before CreateItems.
        foreach (var (oldPrimary, newPrimary) in replacedPrimaries)
        {
            logger.LogInformation(
                "Processing deferred deletion of replaced primary {OldName} ({OldId}), new primary {NewName} ({NewId})",
                oldPrimary.Name,
                oldPrimary.Id,
                newPrimary.Name,
                newPrimary.Id);

            await writes.ReplacePrimaryVersionAsync(oldPrimary, newPrimary, parent, deletions, cancellationToken).ConfigureAwait(false);
        }

        // Demote old primaries that are now alternate versions of the selected primaries.
        // This handles the case where a new file is added that becomes the new primary
        // (e.g. movie-2 added, movie-3 was primary → movie-3 needs demotion).
        // Items in replacedPrimaries are excluded (already in actuallyRemoved).
        var oldPrimariesToDemote = new List<(Video OldPrimary, Video NewPrimary)>();
        foreach (var item in itemsRemoved.Except(actuallyRemoved))
        {
            if (item is Video video
                && video.OwnerId.IsEmpty()
                && !string.IsNullOrEmpty(item.Path)
                && alternateVersionPaths.Contains(item.Path))
            {
                var newPrimary = validChildren
                    .OfType<Video>()
                    .FirstOrDefault(v => (v.LocalAlternateVersions ?? [])
                        .Any(p => string.Equals(p, item.Path, StringComparison.OrdinalIgnoreCase)));
                if (newPrimary is not null)
                {
                    oldPrimariesToDemote.Add((video, newPrimary));
                }
            }
        }

        foreach (var (oldPrimary, newPrimary) in oldPrimariesToDemote)
        {
            logger.LogInformation(
                "Demoting old primary {OldName} ({OldId}) to alternate of new primary {NewName} ({NewId})",
                oldPrimary.Name,
                oldPrimary.Id,
                newPrimary.Name,
                newPrimary.Id);

            await writes.DemotePrimaryVersionAsync(oldPrimary, newPrimary, cancellationToken).ConfigureAwait(false);
        }

        // After removing items, reattach any detached user data to remaining children
        // that share the same user data keys (eg. same episode replaced with a new file).
        if (actuallyRemoved.Count > 0)
        {
            var removedKeys = actuallyRemoved.SelectMany(i => i.GetUserDataKeys()).ToHashSet();
            foreach (var child in validChildren)
            {
                if (child.GetUserDataKeys().Any(removedKeys.Contains))
                {
                    await writes.ReattachUserDataAsync(child, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return new FolderChildReconciliationResult(validChildren.ToArray(), accessibleChildren.ToArray());
    }

    private static BaseItem[] Admit(IReadOnlyList<BaseItem> discovered)
    {
        var identities = new Dictionary<Guid, BaseItem>();
        var paths = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var child in discovered)
        {
            if (child.Id.IsEmpty())
            {
                throw new ArgumentException("Discovered children must have nonempty identities.", nameof(discovered));
            }

            if (identities.TryGetValue(child.Id, out var earlier))
            {
                if (earlier.GetType() != child.GetType() || !string.Equals(earlier.Path, child.Path, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Discovered identity has conflicting type or path.", nameof(discovered));
                }

                continue;
            }

            if (!string.IsNullOrEmpty(child.Path) && paths.TryGetValue(child.Path, out var otherId) && !otherId.Equals(child.Id))
            {
                throw new ArgumentException("Discovered path has conflicting identities.", nameof(discovered));
            }

            identities.Add(child.Id, child);
            if (!string.IsNullOrEmpty(child.Path))
            {
                paths[child.Path] = child.Id;
            }
        }

        return identities.Values.ToArray();
    }

    private Dictionary<Guid, BaseItem> BuildCurrentChildren(IReadOnlyList<BaseItem> snapshot)
    {
        var dictionary = new Dictionary<Guid, BaseItem>();

        foreach (var child in snapshot)
        {
            var id = child.Id;
            if (dictionary.ContainsKey(id))
            {
                logger.LogError(
                    "Found duplicate catalogue child {ChildId} at {Path}",
                    child.Id,
                    child.Path ?? child.Name);
            }
            else
            {
                dictionary[id] = child;
            }
        }

        return dictionary;
    }

    private bool IsLibraryFolderAccessible(IDirectoryService directoryService, BaseItem item, bool checkCollection)
    {
        if (!checkCollection && (item is BoxSet || (!string.IsNullOrEmpty(item.Path) && files.IsPathFile(item.Path) && string.Equals(Path.GetFileNameWithoutExtension(item.Path), "collections", StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }

        // For top parents i.e. Library folders, skip the validation if it's empty or inaccessible
        if (item.IsTopParentVia(lookup) && !directoryService.IsAccessible(item.ContainingFolderPath))
        {
            logger.LogWarning("Library folder {LibraryFolderPath} is inaccessible or empty, skipping", item.ContainingFolderPath);
            return false;
        }

        return true;
    }
}
