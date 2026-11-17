using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Providers.Manager;

namespace Tesserafin.Providers.BoxSets;

/// <summary>
/// Service to manage boxset metadata.
/// </summary>
public class BoxSetMetadataService : MetadataService<BoxSet, BoxSetInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BoxSetMetadataService"/> class.
    /// </summary>
    /// <param name="writes">Explicit item write/resolution ownership.</param>
    /// <param name="peopleCommands">Credit and person metadata writes.</param>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="itemNamingService">Instance of the <see cref="IItemNamingService"/> interface.</param>
    /// <param name="externalDataManager">Instance of the <see cref="IExternalDataManager"/> interface.</param>
    /// <param name="itemRepository">Instance of the <see cref="IItemRepository"/> interface.</param>
    public BoxSetMetadataService(
        IItemWriteCommands writes,
        IItemPeopleCommands peopleCommands,
        IServerConfigurationManager serverConfigurationManager,
        ILogger<BoxSetMetadataService> logger,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILibraryManager libraryManager,
        IItemNamingService itemNamingService,
        IExternalDataManager externalDataManager,
        IItemRepository itemRepository)
        : base(writes, peopleCommands, serverConfigurationManager, logger, providerManager, fileSystem, libraryManager, itemNamingService, externalDataManager, itemRepository)
    {
    }

    /// <inheritdoc />
    protected override bool EnableUpdatingGenresFromChildren => true;

    /// <inheritdoc />
    protected override bool EnableUpdatingOfficialRatingFromChildren => true;

    /// <inheritdoc />
    protected override bool EnableUpdatingStudiosFromChildren => true;

    /// <inheritdoc />
    protected override bool EnableUpdatingPremiereDateFromChildren => true;

    /// <inheritdoc />
    protected override IReadOnlyList<BaseItem> GetChildrenForMetadataUpdates(BoxSet item)
    {
        return item.GetLinkedChildren();
    }

    /// <inheritdoc />
    protected override void MergeData(MetadataResult<BoxSet> source, MetadataResult<BoxSet> target, MetadataField[] lockedFields, bool replaceData, bool mergeMetadataSettings)
    {
        base.MergeData(source, target, lockedFields, replaceData, mergeMetadataSettings);

        var sourceItem = source.Item;
        var targetItem = target.Item;

        if (mergeMetadataSettings)
        {
            // Only merge LinkedChildren from metadata for external collections (not managed by Tesserafin).
            // For internal collections, the database LinkedChildren table is the source of truth.
            var targetPath = targetItem.Path;
            if (!string.IsNullOrEmpty(targetPath)
                && !FileSystem.ContainsSubPath(ServerConfigurationManager.ApplicationPaths.DataPath, targetPath))
            {
#pragma warning disable CS0618 // Type or member is obsolete - fallback for legacy path-based dedup
                targetItem.LinkedChildren = sourceItem.LinkedChildren.Concat(targetItem.LinkedChildren)
                    .DistinctBy(i => i.ItemId.HasValue && !i.ItemId.Value.Equals(Guid.Empty) ? i.ItemId.Value.ToString() : i.Path ?? string.Empty)
                    .ToArray();
#pragma warning restore CS0618
            }
        }
    }

    /// <inheritdoc />
    protected override ItemUpdateType BeforeSaveInternal(BoxSet item, bool isFullRefresh, ItemUpdateType updateType)
    {
        var updatedType = base.BeforeSaveInternal(item, isFullRefresh, updateType);

        var libraryFolderIds = item.GetLibraryFolderIds();

        var itemLibraryFolderIds = item.LibraryFolderIds;
        if (itemLibraryFolderIds is null || !libraryFolderIds.SequenceEqual(itemLibraryFolderIds))
        {
            item.LibraryFolderIds = libraryFolderIds;
            updatedType |= ItemUpdateType.MetadataImport;
        }

        return updatedType;
    }
}
