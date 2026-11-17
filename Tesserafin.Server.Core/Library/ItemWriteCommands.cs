using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
    IItemMetadataExporter metadata,
    IFileSystem files,
    IItemChangePublisher changes,
    ActiveRecordings recordings,
    INamedItemCatalog catalog,
    NamedItemIdentity namedItems,
    IItemResolutionService resolution,
    IItemRepository repository,
    ILibraryItemOptions itemOptions,
    ILogger<ItemWriteCommands> logger) : IItemWriteCommands
{
    private readonly IItemResolutionService _resolution = resolution;
    private readonly IItemRepository _repository = repository;

    private readonly object _namedItemGate = new();

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
                await metadata.SaveAsync(draft, propagated.Contains(draft.Id) ? ItemUpdateType.MetadataDownload : reason, cancellationToken).ConfigureAwait(false);
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

    public async Task<bool> RefreshOwnedVideosAsync(Video owner, IItemDeletionCommands deletions, MetadataRefreshOptions options, CancellationToken cancellationToken)
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

        await Task.WhenAll(parts.Select(path => RefreshOwnedVideoAsync(owner, path, false, deletions, options, cancellationToken))).ConfigureAwait(false);
        await Task.WhenAll(paths.Select(path => RefreshOwnedVideoAsync(owner, path, true, deletions, options, cancellationToken))).ConfigureAwait(false);

        var changed = !owner.LocalAlternateVersions.SequenceEqual(paths, StringComparer.Ordinal);
        // Publish the pruned paths only after all admitted refreshes complete successfully.
        if (changed)
        {
            owner.LocalAlternateVersions = paths;
        }

        return changed || existingCount != paths.Length;
    }

    private async Task RefreshOwnedVideoAsync(Video owner, string path, bool alternate, IItemDeletionCommands deletions, MetadataRefreshOptions options, CancellationToken cancellationToken)
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

        await RefreshOwnedMetadataAsync(owner, staged, !alternate, deletions, refresh, cancellationToken).ConfigureAwait(false);
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
            await metadata.SaveAsync(parent, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
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
