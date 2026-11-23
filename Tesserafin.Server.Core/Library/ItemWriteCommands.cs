using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;

namespace Tesserafin.Server.Core.Library;

/// <summary>Prepares item/version writes, saves their graph, then publishes cache and notifications.</summary>
internal sealed partial class ItemWriteCommands(
    ItemVersionGraphPlanner versions,
    IItemPersistenceService persistence,
    IItemCacheStore cache,
    IItemLookupService lookup,
    ILinkedChildrenService linkedChildren,
    IItemMetadataExporter metadataExporter,
    IFileSystem files,
    IItemChangePublisher changes,
    ActiveRecordings recordings,
    INamedItemCatalog catalog,
    NamedItemIdentity namedItems,
    IItemResolutionService resolution,
    IItemRepository repository,
    ILibraryItemOptions itemOptions,
    FolderChildReconciler childReconciler,
    CollectionFolderMembershipResolver collectionMembership,
    ILogger<ItemWriteCommands> logger) : IItemWriteCommands
{
    private readonly IItemResolutionService _resolution = resolution;
    private readonly IItemRepository _repository = repository;

    private readonly object _namedItemGate = new();

    public async Task<bool> RefreshCollectionFolderMembershipAsync(CollectionFolder owner, IDirectoryService directoryService, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (owner.Id.Equals(Guid.Empty))
        {
            throw new ArgumentException("Library must have a catalogue identity.", nameof(owner));
        }

        var (locations, physicalFolders) = collectionMembership.Resolve(owner, directoryService, cancellationToken);
        var ids = physicalFolders.Select(folder => folder.Id).ToArray();
        var links = physicalFolders.SelectMany(folder => folder.LinkedChildren).Select(CopyFolderLink).ToArray();
        var saved = lookup.GetItemById(owner.Id) as CollectionFolder ?? owner;
        cancellationToken.ThrowIfCancellationRequested();
        if (MatchesMembership(saved))
        {
            if (!MatchesMembership(owner))
            {
                PublishMembership(owner);
            }

            return false;
        }

        var staged = (CollectionFolder)saved.CopyForWrite();
        PublishMembership(staged);
        await UpdateItemAsync(staged, saved.GetParent(lookup), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        PublishMembership(owner);
        owner.DateLastSaved = staged.DateLastSaved;
        return true;

        bool MatchesMembership(CollectionFolder folder)
            => locations.SequenceEqual(folder.PhysicalLocations)
                && ids.SequenceEqual(folder.PhysicalFolderIds)
                && links.Length == folder.LinkedChildren.Length
                && links.Zip(folder.LinkedChildren, SameFolderLink).All(equal => equal);

        void PublishMembership(CollectionFolder folder)
        {
            folder.PhysicalLocationsList = locations;
            folder.PhysicalFolderIds = ids;
            folder.LinkedChildren = links;
        }
    }

    public async Task<bool> RefreshShortcutLinksAsync(Folder owner, IReadOnlyList<FileSystemMetadata> fileSystemChildren, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (owner.Id.Equals(Guid.Empty))
        {
            throw new ArgumentException("Shortcut owner must have an identity.", nameof(owner));
        }

        // Finish every file read before examining or changing the catalogue. One unreadable
        // shortcut must not become an authoritative removal of the remaining links.
        var targets = _resolution.ResolveShortcutTargets(owner, fileSystemChildren, cancellationToken);
        var saved = lookup.GetItemById(owner.Id) as Folder;
        var current = saved is null ? owner.LinkedChildren : linkedChildren.GetLinkedChildren(owner.Id);
        var manual = current.Where(link => link.Type != LinkedChildType.Shortcut).Select(CopyFolderLink).ToArray();
        var shortcuts = new List<LinkedChild>();
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = ItemPathQuery.Find(_repository, target.TargetPath, null);
            if ((item is not null && item.Id.Equals(owner.Id)) || files.AreEqual(target.TargetPath, owner.Path))
            {
                throw new InvalidOperationException("A folder cannot link to itself through a shortcut.");
            }

            // A path is current discovery state while its target is not scanned yet. Keep it
            // without materializing an item; known targets use their persisted identity.
#pragma warning disable CS0618 // Unscanned shortcut targets still require their discovery path.
            var candidate = new LinkedChild { ItemId = item?.Id, Path = target.TargetPath, Type = LinkedChildType.Shortcut };
#pragma warning restore CS0618
            if (!manual.Any(link => SameFolderTarget(link, candidate)) && !shortcuts.Any(link => SameFolderTarget(link, candidate)))
            {
                shortcuts.Add(candidate);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var desired = shortcuts.Concat(manual).ToArray();
        if (desired.Length == current.Count && desired.All(link => current.Any(existing => SameFolderLink(link, existing))))
        {
            // Repair a stale caller from links that are already committed, without resaving
            // the item or letting a later metadata save overwrite canonical manual links.
            if (owner.LinkedChildren.Length != desired.Length || !owner.LinkedChildren.All(link => desired.Any(existing => SameFolderLink(link, existing))))
            {
                owner.LinkedChildren = desired;
            }

            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var staged = (Folder)owner.CopyForWrite();
        staged.LinkedChildren = desired;
        logger.LogInformation("Saving shortcut links for {Path}", owner.Path);
        await UpdateItemAsync(staged, owner.GetParent(lookup), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        owner.LinkedChildren = staged.LinkedChildren;
        owner.DateLastSaved = staged.DateLastSaved;
        return true;
    }

    private bool SameFolderLink(LinkedChild left, LinkedChild right)
    {
        if (left.Type != right.Type)
        {
            return false;
        }

        var leftId = left.ItemId.GetValueOrDefault();
        var rightId = right.ItemId.GetValueOrDefault();
        if (!leftId.Equals(Guid.Empty) || !rightId.Equals(Guid.Empty))
        {
            return !leftId.Equals(Guid.Empty) && leftId.Equals(rightId);
        }

        return SameFolderTarget(left, right);
    }

#pragma warning disable CS0618 // Preserve discovery paths until their shortcut targets are scanned.
    private bool SameFolderTarget(LinkedChild left, LinkedChild right)
    {
        if (left.ItemId.HasValue && right.ItemId.HasValue && !left.ItemId.Value.Equals(Guid.Empty) && !right.ItemId.Value.Equals(Guid.Empty))
        {
            return left.ItemId.Value.Equals(right.ItemId.Value);
        }

        return !string.IsNullOrEmpty(left.Path) && !string.IsNullOrEmpty(right.Path) && files.AreEqual(left.Path, right.Path);
    }

    private static LinkedChild CopyFolderLink(LinkedChild source)
        => new() { ItemId = source.ItemId, Type = source.Type, Path = source.Path, LibraryItemId = source.LibraryItemId };
#pragma warning restore CS0618

    public Task<FolderChildReconciliationResult> ReconcileFolderChildrenAsync(Folder parent, IReadOnlyList<BaseItem> currentChildren, IReadOnlyList<BaseItem> discovered, IDirectoryService directoryService, bool allowRemoveRoot, IItemDeletionCommands deletions, IItemImageService images, IChannelManager channels, CancellationToken cancellationToken)
        => childReconciler.ReconcileAsync(parent, currentChildren, discovered, directoryService, allowRemoveRoot, this, deletions, images, channels, cancellationToken);

    public T GetOrCreateNamedItem<T>(string name, CancellationToken cancellationToken)
        where T : BaseItem, new()
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = namedItems.GetPath<T>(name);
        // Admission is limited to named materialization; this is not a lock on general item writes.
        lock (_namedItemGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = catalog.Find<T>(name);
            if (existing is not null)
            {
                return existing;
            }

            var directory = Directory.CreateDirectory(path);
            var item = new T
            {
                Id = namedItems.GetId<T>(name),
                Name = NamedItemIdentity.NormalizeName<T>(name),
                Path = path,
                DateCreated = directory.CreationTimeUtc,
                DateModified = directory.LastWriteTimeUtc
            };
            CreateItems([item], null, cancellationToken);
            return item;
        }
    }

    public void CreateItem(BaseItem item, BaseItem? parent) => CreateItems([item], parent, CancellationToken.None);

    public void CreateItems(IReadOnlyList<BaseItem> items, BaseItem? parent, CancellationToken cancellationToken)
    {
        var graph = versions.Prepare(items, parent, cancellationToken);
        persistence.SaveItems(graph, cancellationToken);
        PublishCache(graph, [parent]);
        foreach (var item in items)
        {
            if (IsLibraryItem(item))
            {
                try
                {
                    changes.Added(item, parent ?? item.GetParent(lookup));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unable to prepare creation notification for item {ItemId}", item.Id);
                }
            }
        }
    }

    public Task UpdateItemAsync(BaseItem item, BaseItem? parent, ItemUpdateType reason, CancellationToken cancellationToken)
        => UpdateItemsCoreAsync([item], parent, reason, cancellationToken, true);

    public Task UpdateItemsAsync(IReadOnlyList<BaseItem> items, BaseItem? parent, ItemUpdateType reason, CancellationToken cancellationToken)
        => UpdateItemsCoreAsync(items, parent, reason, cancellationToken, false);

    private async Task UpdateItemsCoreAsync(IReadOnlyList<BaseItem> items, BaseItem? parent, ItemUpdateType reason, CancellationToken cancellationToken, bool propagateVersions)
    {
        var prepared = versions.Prepare(items, parent, cancellationToken);
        var originals = prepared.ToDictionary(i => i.Id);
        var drafts = prepared.ToDictionary(i => i.Id, i => i.CopyForWrite());
        var propagated = new HashSet<Guid>();
        var queue = new Queue<Video>(propagateVersions ? drafts.Values.OfType<Video>() : []);
        var visited = new HashSet<Guid>();
        while (queue.TryDequeue(out var video))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(video.Id))
            {
                continue;
            }

            var ids = linkedChildren.GetLinkedChildrenIds(video.Id, (int)LinkedChildType.LocalAlternateVersion)
                .Concat(versions.GetLocalVersionIds(video))
                .Concat(drafts.Values.OfType<Video>().Where(v => v.OwnerId.Equals(video.Id)).Select(v => v.Id)).Distinct().ToArray();
            foreach (var id in ids)
            {
                if (visited.Contains(id))
                {
                    continue;
                }

                if (!drafts.TryGetValue(id, out var alternate))
                {
                    var original = lookup.GetItemById(id);
                    if (original is null)
                    {
                        continue;
                    }

                    originals.Add(id, original);
                    alternate = original.CopyForWrite();
                    drafts.Add(id, alternate);
                }

                CopyVersionMetadata(video, alternate);
                propagated.Add(id);
                if (alternate is Video next)
                {
                    queue.Enqueue(next);
                }
            }
        }

        var parents = originals.ToDictionary(p => p.Key, p => items.Contains(p.Value) && parent is not null ? parent : p.Value.GetParent(lookup));
        foreach (var draft in drafts.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            draft.DateLastSaved = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(draft.Path) && files.IsPathFile(draft.Path))
            {
                await metadataExporter.SaveAsync(draft, propagated.Contains(draft.Id) ? ItemUpdateType.MetadataDownload : reason, cancellationToken).ConfigureAwait(false);
            }

            draft.DateLastSaved = DateTime.UtcNow;
        }

        cancellationToken.ThrowIfCancellationRequested();
        persistence.SaveItems(drafts.Values.ToArray(), cancellationToken);
        foreach (var (id, draft) in drafts)
        {
            var original = originals[id];
            original.DateLastSaved = draft.DateLastSaved;
            if (propagated.Contains(id))
            {
                CopyVersionMetadata(draft, original);
            }
        }

        PublishCache(originals.Values.ToArray(), parents.Values);
        foreach (var item in originals.Values)
        {
            if (IsLibraryItem(item))
            {
                changes.Updated(item, parents[item.Id], propagated.Contains(item.Id) ? ItemUpdateType.MetadataDownload : reason);
            }
        }
    }

    public async Task ReplacePrimaryVersionAsync(Video previous, Video replacement, Folder parent, IItemDeletionCommands deletions, CancellationToken cancellationToken)
    {
        var plan = versions.PreparePrimaryTransfer(previous, replacement, true, cancellationToken);
        if (plan is null)
        {
            return;
        }

        // Finish reference rerouting first. A failure leaves the previous primary discoverable
        // by a later scan, including when replacement creation has already committed.
        await RerouteLinkedChildReferencesAsync(previous.Id, replacement.Id, cancellationToken).ConfigureAwait(false);
        await CommitPrimaryTransferAsync(plan, previous, replacement, cancellationToken).ConfigureAwait(false);
        // The committed old row has no outgoing version links, so deletion cannot promote an
        // obsolete alternate. Keep the scan's captured parent and preserve every media source.
        await deletions.DeleteAsync(plan.Previous, new DeleteOptions { DeleteFileLocation = false }, parent, cancellationToken).ConfigureAwait(false);
    }

    public async Task DemotePrimaryVersionAsync(Video previous, Video replacement, CancellationToken cancellationToken)
    {
        var plan = versions.PreparePrimaryTransfer(previous, replacement, false, cancellationToken)
            ?? throw new InvalidOperationException("Previous primary must already be saved.");
        // Finish reference rerouting first. A failure leaves the previous primary discoverable
        // by a later scan, including when replacement creation has already committed.
        await RerouteLinkedChildReferencesAsync(previous.Id, replacement.Id, cancellationToken).ConfigureAwait(false);
        await CommitPrimaryTransferAsync(plan, previous, replacement, cancellationToken).ConfigureAwait(false);
    }

    private async Task CommitPrimaryTransferAsync(PrimaryVersionTransferPlan plan, Video previous, Video replacement, CancellationToken cancellationToken)
    {
        // One graph save moves ownership before the persistence layer removes the old links.
        // Exported sidecars remain outside that database transaction, as for all item updates.
        await UpdateItemsAsync(plan.Items, null, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        CopyPrimaryState(plan.Previous, previous);
        CopyPrimaryState(plan.Replacement, replacement);
    }

    private static void CopyPrimaryState(Video source, Video target)
    {
        target.LocalAlternateVersions = source.LocalAlternateVersions;
        target.LinkedAlternateVersions = source.LinkedAlternateVersions;
        target.OwnerId = source.OwnerId;
        target.PrimaryVersionId = source.PrimaryVersionId;
        target.PresentationUniqueKey = source.PresentationUniqueKey;
        target.DateLastSaved = source.DateLastSaved;
    }

    public async Task<bool> RefreshOwnedVideosAsync(Video owner, IItemMetadataDispatcher metadata, IItemDeletionCommands deletions, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (owner.ParentId.Equals(Guid.Empty) || !files.IsPathFile(owner.Path))
        {
            return false;
        }

        // Materialize admission before starting any asynchronous work. Duplicate paths must not
        // race creation or refresh of the same catalogue item.
        var parts = owner.AdditionalParts.Where(path => !string.IsNullOrEmpty(path))
            .DistinctBy(path => _resolution.GetNewItemId(path, typeof(Video))).ToArray();
        var paths = owner.LocalAlternateVersions.Where(path => !string.IsNullOrEmpty(path) && files.FileExists(path))
            .DistinctBy(path => _resolution.GetNewItemId(path, owner.GetType())).ToArray();
        var existingCount = linkedChildren.GetLinkedChildrenIds(owner.Id, (int)LinkedChildType.LocalAlternateVersion).Count;

        await Task.WhenAll(parts.Select(path => RefreshOwnedVideoAsync(owner, path, false, metadata, deletions, options, cancellationToken))).ConfigureAwait(false);
        await Task.WhenAll(paths.Select(path => RefreshOwnedVideoAsync(owner, path, true, metadata, deletions, options, cancellationToken))).ConfigureAwait(false);

        var changed = !owner.LocalAlternateVersions.SequenceEqual(paths, StringComparer.Ordinal);
        // Publish the pruned paths only after all admitted refreshes complete successfully.
        if (changed)
        {
            owner.LocalAlternateVersions = paths;
        }

        return changed || existingCount != paths.Length;
    }

    private async Task RefreshOwnedVideoAsync(Video owner, string path, bool alternate, IItemMetadataDispatcher metadata, IItemDeletionCommands deletions, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var type = alternate ? owner.GetType() : typeof(Video);
        var id = _resolution.GetNewItemId(path, type);
        if (id.Equals(owner.Id) || files.AreEqual(path, owner.Path))
        {
            throw new InvalidOperationException("A video cannot own itself as a part or alternate.");
        }

        var existing = lookup.GetItemById(id);
        if (existing is not null && (existing is not Video || existing.GetType() != type))
        {
            throw new InvalidOperationException("Owned video identity/type does not match its owner.");
        }

        if (existing is Video saved)
        {
            EnsureOwnedVideoCanAttach(owner, saved);
        }

        if (!files.FileExists(path))
        {
            if (existing is Video orphan && orphan.OwnerId.Equals(owner.Id))
            {
                logger.LogInformation("Owned video file no longer exists, removing orphaned item: {Path}", path);
                await deletions.DeleteAsync(orphan, new DeleteOptions { DeleteFileLocation = false }, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var parent = owner.GetParent(lookup) as Folder;
        Video candidate;
        if (existing is Video video)
        {
            candidate = video;
        }
        else
        {
            if (alternate)
            {
                var genericId = _resolution.GetNewItemId(path, typeof(Video));
                if (!genericId.Equals(id) && lookup.GetItemById(genericId) is Video obsolete)
                {
                    EnsureOwnedVideoCanAttach(owner, obsolete);
                }

                await deletions.DeleteObsoleteAlternateAsync(path, type, cancellationToken).ConfigureAwait(false);
            }

            var resolved = _resolution.ResolveAlternateVersion(path, type, parent, itemOptions.GetContentType(owner));
            if (resolved is null)
            {
                return;
            }

            if (!resolved.Id.Equals(id) || resolved.GetType() != type)
            {
                throw new InvalidOperationException("Resolved owned video identity/type does not match its owner.");
            }

            EnsureOwnedVideoCanAttach(owner, resolved);
            candidate = resolved;
        }

        var staged = (Video)candidate.CopyForWrite();
        var refresh = new MetadataRefreshOptions(options);
        if (staged.OwnerId.Equals(Guid.Empty))
        {
            staged.OwnerId = owner.Id;
            refresh.ForceSave = true;
        }

        if (alternate && !staged.PrimaryVersionId.HasValue)
        {
            staged.SetPrimaryVersionId(owner.Id);
            refresh.ForceSave = true;
        }

        if (existing is null)
        {
            CreateItems([staged], parent, cancellationToken);
            // Creation publishes its candidate. Refresh a new copy so provider edits cannot
            // mutate that published record before the next successful save.
            staged = (Video)staged.CopyForWrite();
            refresh.ForceSave = true;
        }

        await RefreshOwnedMetadataAsync(owner, staged, !alternate, metadata, deletions, refresh, cancellationToken).ConfigureAwait(false);
        if (alternate)
        {
            UpsertLinkedChild(owner.Id, staged.Id, LinkedChildType.LocalAlternateVersion, cancellationToken);
        }
    }

    private static void EnsureOwnedVideoCanAttach(Video owner, Video candidate)
    {
        if ((!candidate.OwnerId.Equals(Guid.Empty) && !candidate.OwnerId.Equals(owner.Id))
            || (candidate.PrimaryVersionId.HasValue && !candidate.PrimaryVersionId.Value.Equals(owner.Id)))
        {
            throw new InvalidOperationException("Owned video already belongs to another primary.");
        }
    }

    public void UpsertLinkedChild(Guid parentId, Guid childId, LinkedChildType childType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (parentId.Equals(Guid.Empty) || childId.Equals(Guid.Empty))
        {
            throw new ArgumentException("Linked items must have nonempty identities.");
        }

        if (!Enum.IsDefined(childType))
        {
            throw new ArgumentOutOfRangeException(nameof(childType));
        }

        linkedChildren.UpsertLinkedChild(parentId, childId, childType);
        cache.Remove(parentId);
    }

    public async Task RerouteLinkedChildReferencesAsync(Guid fromChildId, Guid toChildId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (fromChildId.Equals(Guid.Empty) || toChildId.Equals(Guid.Empty))
        {
            throw new ArgumentException("Linked items must have nonempty identities.");
        }

        if (fromChildId.Equals(toChildId))
        {
            return;
        }

        var affectedParentIds = linkedChildren.RerouteLinkedChildren(fromChildId, toChildId);
        var parents = new List<Folder>();
        // Complete cache reconciliation for every parent before any cancellable sidecar export.
        try
        {
            foreach (var id in affectedParentIds.Distinct())
            {
                if (lookup.GetItemById(id) is not Folder parent)
                {
                    cache.Remove(id);
                    continue;
                }

                parent.LinkedChildren = linkedChildren.GetLinkedChildren(id).ToArray();
                parent.Children = null;
                parent.UserData = null;
                parents.Add(parent);
            }
        }
        catch
        {
            cache.RemoveRange(affectedParentIds);
            throw;
        }

        foreach (var parent in parents)
        {
            await metadataExporter.SaveAsync(parent, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task ReattachUserDataAsync(BaseItem item, CancellationToken cancellationToken)
        => persistence.ReattachUserDataAsync(item, cancellationToken);

    private static void CopyVersionMetadata(BaseItem source, BaseItem target)
    {
        target.ImageInfos = source.ImageInfos;
        target.Overview = source.Overview;
        target.ProductionYear = source.ProductionYear;
        target.PremiereDate = source.PremiereDate;
        target.CommunityRating = source.CommunityRating;
        target.OfficialRating = source.OfficialRating;
        target.Genres = source.Genres;
        target.ProviderIds = source.ProviderIds;
    }

    private void PublishCache(IReadOnlyCollection<BaseItem> graph, IEnumerable<BaseItem?> parents)
    {
        foreach (var folder in parents.OfType<Folder>())
        {
            folder.Children = null;
            folder.UserData = null;
        }

        try
        {
            foreach (var item in graph)
            {
                cache.Register(item);
            }
        }
        catch
        {
            cache.RemoveRange(graph.Select(i => i.Id));
            throw;
        }
    }

    private bool IsLibraryItem(BaseItem item)
        => item is not LiveTvProgram and not LiveTvChannel
            && item.ChannelId.Equals(Guid.Empty)
            && (item is not Video || recordings.GetActiveRecordingInfo(item.Path) is null);
}
