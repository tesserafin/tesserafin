using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Extensions;

namespace Tesserafin.Server.Core.Library;

/// <summary>Prepares the typed local-version graph without saving or publishing anything.</summary>
internal sealed class ItemVersionGraphPlanner(IItemResolutionService resolution, ILibraryItemOptions options, IItemLookupService lookup, ILinkedChildrenService linked)
{
    public PrimaryVersionTransferPlan? PreparePrimaryTransfer(Video previous, Video replacement, bool removePrevious, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (previous.Id.IsEmpty() || replacement.Id.IsEmpty() || previous.Id.Equals(replacement.Id))
        {
            throw new ArgumentException("Primary transfer requires distinct, nonempty identities.");
        }

        var savedReplacement = lookup.GetItemById(replacement.Id) as Video
            ?? throw new InvalidOperationException("Replacement primary must already be saved.");
        var savedPrevious = lookup.GetItemById(previous.Id) as Video;
        if (savedPrevious is null)
        {
            return removePrevious ? null : throw new InvalidOperationException("Previous primary must already be saved.");
        }

        if (savedPrevious.GetType() != savedReplacement.GetType())
        {
            throw new InvalidOperationException("Primary transfer requires matching concrete video types.");
        }

        EnsureTransferable(savedPrevious);
        EnsureTransferable(savedReplacement);
        var oldDraft = (Video)savedPrevious.CopyForWrite();
        var newDraft = (Video)savedReplacement.CopyForWrite();
        var drafts = new Dictionary<Guid, BaseItem> { [oldDraft.Id] = oldDraft, [newDraft.Id] = newDraft };
        var localIds = linked.GetLinkedChildrenIds(previous.Id, (int)LinkedChildType.LocalAlternateVersion)
            .Concat(linked.GetLinkedChildrenIds(replacement.Id, (int)LinkedChildType.LocalAlternateVersion)).ToHashSet();
        if (!removePrevious)
        {
            localIds.Add(previous.Id);
        }

        var linkedIds = linked.GetLinkedChildrenIds(previous.Id, (int)LinkedChildType.LinkedAlternateVersion)
            .Concat(linked.GetLinkedChildrenIds(replacement.Id, (int)LinkedChildType.LinkedAlternateVersion))
            .Concat(savedReplacement.LinkedAlternateVersions.Where(child => child.ItemId.HasValue).Select(child => child.ItemId!.Value))
            .Distinct().ToArray();
        var localPaths = new List<string>();
        var retainedLinkedIds = new List<Guid>();
        foreach (var id in localIds.Concat(linkedIds).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (id.Equals(previous.Id) || id.Equals(replacement.Id) || lookup.GetItemById(id) is not Video video)
            {
                continue;
            }

            EnsureTransferable(video);
            var isLocal = localIds.Contains(id);
            if (isLocal && (video.GetType() != newDraft.GetType() || string.IsNullOrEmpty(video.Path)))
            {
                throw new InvalidOperationException("Local alternate must have the primary's type and a source path.");
            }

            var draft = (Video)video.CopyForWrite();
            draft.SetPrimaryVersionId(newDraft.Id);
            draft.OwnerId = isLocal ? newDraft.Id : Guid.Empty;
            drafts.Add(draft.Id, draft);
            if (isLocal)
            {
                localPaths.Add(draft.Path);
            }
            else
            {
                retainedLinkedIds.Add(draft.Id);
            }
        }

        oldDraft.LocalAlternateVersions = [];
        oldDraft.LinkedAlternateVersions = [];
        oldDraft.SetPrimaryVersionId(removePrevious ? null : newDraft.Id);
        oldDraft.OwnerId = removePrevious ? Guid.Empty : newDraft.Id;
        newDraft.SetPrimaryVersionId(null);
        newDraft.OwnerId = Guid.Empty;
        if (!removePrevious)
        {
            localPaths.Add(oldDraft.Path);
        }

        newDraft.LocalAlternateVersions = savedReplacement.LocalAlternateVersions.Concat(localPaths)
            .Where(path => !string.IsNullOrEmpty(path)
                && !string.Equals(path, newDraft.Path, StringComparison.OrdinalIgnoreCase)
                && (!removePrevious || !string.Equals(path, oldDraft.Path, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        newDraft.LinkedAlternateVersions = retainedLinkedIds.Select(id => new LinkedChild { ItemId = id, Type = LinkedChildType.LinkedAlternateVersion }).ToArray();
        return new PrimaryVersionTransferPlan(oldDraft, newDraft, drafts.Values.ToArray());

        void EnsureTransferable(Video video)
        {
            if ((!video.OwnerId.IsEmpty() && !video.OwnerId.Equals(previous.Id) && !video.OwnerId.Equals(replacement.Id))
                || (video.PrimaryVersionId.HasValue && !video.PrimaryVersionId.Value.Equals(previous.Id) && !video.PrimaryVersionId.Value.Equals(replacement.Id)))
            {
                throw new InvalidOperationException("Version belongs to an unrelated primary.");
            }
        }
    }

    public IEnumerable<Guid> GetLocalVersionIds(Video video)
        => video.LocalAlternateVersions.Where(path => !string.IsNullOrEmpty(path)).Select(path => resolution.GetNewItemId(path, video.GetType()));

    public List<BaseItem> Prepare(IReadOnlyList<BaseItem> items, BaseItem? parent, CancellationToken cancellationToken)
    {
        var ids = new HashSet<Guid>();
        var result = new List<BaseItem>(items.Count);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(item);
            if (item.Id.IsEmpty() || !ids.Add(item.Id))
            {
                throw new ArgumentException("Write items must have distinct, nonempty identities.", nameof(items));
            }

            result.Add(item);
        }

        foreach (var item in items)
        {
            if (item is not Video video || video.LocalAlternateVersions.Length == 0)
            {
                continue;
            }

            var collectionType = options.GetTopFolderContentType(parent);
            foreach (var path in video.LocalAlternateVersions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                var id = resolution.GetNewItemId(path, video.GetType());
                if (ids.Contains(id) || lookup.GetItemById(id) is not null)
                {
                    continue;
                }

                var alternate = resolution.ResolveAlternateVersion(path, video.GetType(), parent as Folder, collectionType);
                if (alternate is not null)
                {
                    if (!alternate.Id.Equals(id) || alternate.GetType() != video.GetType())
                    {
                        throw new InvalidOperationException("Resolved alternate identity/type does not match the primary.");
                    }

                    alternate.OwnerId = video.Id;
                    alternate.SetPrimaryVersionId(video.Id);
                    ids.Add(id);
                    result.Add(alternate);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}

internal sealed record PrimaryVersionTransferPlan(Video Previous, Video Replacement, IReadOnlyList<BaseItem> Items);
