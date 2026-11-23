using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Extensions;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;

namespace Tesserafin.Server.Core.Library;

internal sealed partial class ItemWriteCommands
{
    public async Task<bool> RefreshExtrasAsync(BaseItem owner, IItemMetadataDispatcher metadata, IItemDeletionCommands deletions, MetadataRefreshOptions options, IReadOnlyList<FileSystemMetadata> fileSystemChildren, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extras = _resolution.FindExtras(owner, fileSystemChildren, options.DirectoryService).DistinctBy(item => item.Id).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var current = _repository.GetItemList(new InternalItemsQuery { OwnerIds = [owner.Id] })
            .Where(item => item.ExtraType.HasValue).ToArray();
        var ids = extras.Select(item => item.Id).ToHashSet();
        if (current.Select(item => item.Id).ToHashSet().SetEquals(ids)
            && !options.ReplaceAllMetadata && options.MetadataRefreshMode != MetadataRefreshMode.FullRefresh)
        {
            return false;
        }

        foreach (var removed in current.Where(item => !ids.Contains(item.Id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await deletions.DeleteAsync(removed, new DeleteOptions { DeleteFileLocation = false }, cancellationToken).ConfigureAwait(false);
        }

        var prepared = new List<(BaseItem Item, MetadataRefreshOptions Options)>();
        foreach (var extra in extras)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var staged = extra.CopyForWrite();
            var refresh = new MetadataRefreshOptions(options);
            if (!staged.OwnerId.Equals(owner.Id) || !staged.ParentId.IsEmpty())
            {
                refresh.ForceSave = true;
            }

            staged.OwnerId = owner.Id;
            staged.ParentId = Guid.Empty;
            prepared.Add((staged, refresh));
        }

        await Task.WhenAll(prepared.Select(item => RefreshOwnedMetadataAsync(owner, item.Item, true, metadata, deletions, item.Options, cancellationToken))).ConfigureAwait(false);
        return true;
    }

    public async Task RefreshOwnedMetadataAsync(BaseItem owner, BaseItem ownedItem, bool copyTitleMetadata, IItemMetadataDispatcher metadata, IItemDeletionCommands deletions, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        var newOptions = new MetadataRefreshOptions(options)
        {
            SearchResult = null
        };

        cancellationToken.ThrowIfCancellationRequested();
        var item = owner;

        if (copyTitleMetadata)
        {
            // Take some data from the main item, for querying purposes
            if (!item.Genres.SequenceEqual(ownedItem.Genres, StringComparer.Ordinal))
            {
                newOptions.ForceSave = true;
                ownedItem.Genres = item.Genres.ToArray();
            }

            if (!item.Studios.SequenceEqual(ownedItem.Studios, StringComparer.Ordinal))
            {
                newOptions.ForceSave = true;
                ownedItem.Studios = item.Studios.ToArray();
            }

            if (!item.ProductionLocations.SequenceEqual(ownedItem.ProductionLocations, StringComparer.Ordinal))
            {
                newOptions.ForceSave = true;
                ownedItem.ProductionLocations = item.ProductionLocations.ToArray();
            }

            if (item.CommunityRating != ownedItem.CommunityRating)
            {
                ownedItem.CommunityRating = item.CommunityRating;
                newOptions.ForceSave = true;
            }

            if (item.CriticRating != ownedItem.CriticRating)
            {
                ownedItem.CriticRating = item.CriticRating;
                newOptions.ForceSave = true;
            }

            if (!string.Equals(item.Overview, ownedItem.Overview, StringComparison.Ordinal))
            {
                ownedItem.Overview = item.Overview;
                newOptions.ForceSave = true;
            }

            if (!string.Equals(item.OfficialRating, ownedItem.OfficialRating, StringComparison.Ordinal))
            {
                ownedItem.OfficialRating = item.OfficialRating;
                newOptions.ForceSave = true;
            }

            if (!string.Equals(item.CustomRating, ownedItem.CustomRating, StringComparison.Ordinal))
            {
                ownedItem.CustomRating = item.CustomRating;
                newOptions.ForceSave = true;
            }
        }

        await ownedItem.RefreshMetadata(deletions, this, metadata, newOptions, cancellationToken).ConfigureAwait(false);
    }
}
