using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.IO;

namespace Tesserafin.Server.Core.Library;

/// <summary>Prepares item/version writes, saves their graph, then publishes cache and notifications.</summary>
internal sealed class ItemWriteCommands(
    ItemVersionGraphPlanner versions,
    IItemPersistenceService persistence,
    IItemCacheStore cache,
    IItemLookupService lookup,
    ILinkedChildrenService linkedChildren,
    IItemMetadataExporter metadata,
    IFileSystem files,
    IItemChangePublisher changes,
    ActiveRecordings recordings,
    ILogger<ItemWriteCommands> logger) : IItemWriteCommands
{
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

                var alreadyLinked = parent.LinkedChildren.Any(link => link.Type == LinkedChildType.Manual && link.ItemId == toChildId);
                parent.LinkedChildren = parent.LinkedChildren
                    .Where(link => !(alreadyLinked && link.Type == LinkedChildType.Manual && link.ItemId == fromChildId))
                    .Select(link => link.Type == LinkedChildType.Manual && link.ItemId == fromChildId
                        ? new LinkedChild { ItemId = toChildId, Type = LinkedChildType.Manual }
                        : link)
                    .ToArray();
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
