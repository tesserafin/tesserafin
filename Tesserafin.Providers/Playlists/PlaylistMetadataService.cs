using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Playlists;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Providers.Manager;

namespace Tesserafin.Providers.Playlists;

/// <summary>
/// Service to manage playlist metadata.
/// </summary>
public class PlaylistMetadataService : MetadataService<Playlist, ItemLookupInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PlaylistMetadataService"/> class.
    /// </summary>
    /// <param name="writes">Explicit item write/resolution ownership.</param>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="itemNamingService">Instance of the <see cref="IItemNamingService"/> interface.</param>
    /// <param name="externalDataManager">Instance of the <see cref="IExternalDataManager"/> interface.</param>
    /// <param name="itemRepository">Instance of the <see cref="IItemRepository"/> interface.</param>
    public PlaylistMetadataService(
        IItemWriteCommands writes,
        IServerConfigurationManager serverConfigurationManager,
        ILogger<PlaylistMetadataService> logger,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILibraryManager libraryManager,
        IItemNamingService itemNamingService,
        IExternalDataManager externalDataManager,
        IItemRepository itemRepository)
        : base(writes, serverConfigurationManager, logger, providerManager, fileSystem, libraryManager, itemNamingService, externalDataManager, itemRepository)
    {
    }

    /// <inheritdoc />
    protected override bool EnableUpdatingGenresFromChildren => true;

    /// <inheritdoc />
    protected override bool EnableUpdatingOfficialRatingFromChildren => true;

    /// <inheritdoc />
    protected override bool EnableUpdatingStudiosFromChildren => true;

    /// <inheritdoc />
    protected override IReadOnlyList<BaseItem> GetChildrenForMetadataUpdates(Playlist item)
        => item.GetLinkedChildren();

    /// <inheritdoc />
    protected override void MergeData(MetadataResult<Playlist> source, MetadataResult<Playlist> target, MetadataField[] lockedFields, bool replaceData, bool mergeMetadataSettings)
    {
        base.MergeData(source, target, lockedFields, replaceData, mergeMetadataSettings);

        var sourceItem = source.Item;
        var targetItem = target.Item;

        if (mergeMetadataSettings)
        {
            targetItem.PlaylistMediaType = sourceItem.PlaylistMediaType;

            // Only merge LinkedChildren from metadata for external playlists (not managed by Tesserafin).
            // For internal playlists, the database LinkedChildren table is the source of truth.
            var targetPath = targetItem.Path;
            if (!string.IsNullOrEmpty(targetPath)
                && !FileSystem.ContainsSubPath(ServerConfigurationManager.ApplicationPaths.DataPath, targetPath))
            {
                if (replaceData || targetItem.LinkedChildren.Length == 0)
                {
                    targetItem.LinkedChildren = sourceItem.LinkedChildren;
                }
                else
                {
#pragma warning disable CS0618 // Type or member is obsolete - fallback for legacy path-based dedup
                    targetItem.LinkedChildren = sourceItem.LinkedChildren.Concat(targetItem.LinkedChildren)
                        .DistinctBy(i => i.ItemId.HasValue && !i.ItemId.Value.Equals(Guid.Empty) ? i.ItemId.Value.ToString() : i.Path ?? string.Empty)
                        .ToArray();
#pragma warning restore CS0618
                }
            }

            if (replaceData || targetItem.Shares.Count == 0)
            {
                targetItem.Shares = sourceItem.Shares;
            }
            else
            {
                targetItem.Shares = sourceItem.Shares.Concat(targetItem.Shares).DistinctBy(s => s.UserId).ToArray();
            }
        }
    }
}
