using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Sorting;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.IO;
using Tesserafin.Providers.Manager;

namespace Tesserafin.Providers.TV;

/// <summary>
/// Service to manage season metadata.
/// </summary>
public class SeasonMetadataService : MetadataService<Season, SeasonInfo>
{
    private readonly IItemListingServices _listing;
    private readonly IItemSortService _itemSortService;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeasonMetadataService"/> class.
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
    /// <param name="itemSortService">Instance of the <see cref="IItemSortService"/> interface.</param>
    /// <param name="localizationManager">Instance of the <see cref="ILocalizationManager"/> interface.</param>
    /// <param name="listing">Sort and channel services for episode listing.</param>
    public SeasonMetadataService(
        IItemWriteCommands writes,
        IItemPeopleCommands peopleCommands,
        IServerConfigurationManager serverConfigurationManager,
        ILogger<SeasonMetadataService> logger,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILibraryManager libraryManager,
        IItemNamingService itemNamingService,
        IExternalDataManager externalDataManager,
        IItemRepository itemRepository,
        IItemSortService itemSortService,
        ILocalizationManager localizationManager,
        IItemListingServices listing)
        : base(writes, peopleCommands, serverConfigurationManager, logger, providerManager, fileSystem, libraryManager, itemNamingService, externalDataManager, itemRepository, localizationManager)
    {
        _listing = listing;
        _itemSortService = itemSortService;
    }

    /// <inheritdoc />
    protected override bool EnableUpdatingPremiereDateFromChildren => true;

    /// <inheritdoc />
    protected override ItemUpdateType BeforeSaveInternal(Season item, bool isFullRefresh, ItemUpdateType updateType)
    {
        var updatedType = base.BeforeSaveInternal(item, isFullRefresh, updateType);

        if (item.IndexNumber == 0 && !item.IsLocked && !item.LockedFields.Contains(MetadataField.Name))
        {
            var seasonZeroDisplayName = LibraryManager.GetLibraryOptions(item).SeasonZeroDisplayName;

            if (!string.Equals(item.Name, seasonZeroDisplayName, StringComparison.OrdinalIgnoreCase))
            {
                item.Name = seasonZeroDisplayName;
                updatedType |= ItemUpdateType.MetadataEdit;
            }
        }

        var seriesName = item.FindSeriesName();
        if (!string.Equals(item.SeriesName, seriesName, StringComparison.Ordinal))
        {
            item.SeriesName = seriesName;
            updatedType |= ItemUpdateType.MetadataImport;
        }

        var seriesPresentationUniqueKey = item.FindSeriesPresentationUniqueKey();
        if (!string.Equals(item.SeriesPresentationUniqueKey, seriesPresentationUniqueKey, StringComparison.Ordinal))
        {
            item.SeriesPresentationUniqueKey = seriesPresentationUniqueKey;
            updatedType |= ItemUpdateType.MetadataImport;
        }

        var seriesId = item.FindSeriesId();
        if (!item.SeriesId.Equals(seriesId))
        {
            item.SeriesId = seriesId;
            updatedType |= ItemUpdateType.MetadataImport;
        }

        return updatedType;
    }

    /// <inheritdoc />
    protected override IReadOnlyList<BaseItem> GetChildrenForMetadataUpdates(Season item)
        => item.GetEpisodes(null, new DtoOptions(true), true, _listing);

    /// <inheritdoc />
    protected override ItemUpdateType UpdateMetadataFromChildren(Season item, IReadOnlyList<BaseItem> children, bool isFullRefresh, ItemUpdateType currentUpdateType)
    {
        var updateType = base.UpdateMetadataFromChildren(item, children, isFullRefresh, currentUpdateType);

        if (isFullRefresh || currentUpdateType > ItemUpdateType.None)
        {
            updateType |= SaveIsVirtualItem(item, children);
        }

        return updateType;
    }

    private ItemUpdateType SaveIsVirtualItem(Season item, IReadOnlyList<BaseItem> episodes)
    {
        var isVirtualItem = item.LocationType == LocationType.Virtual && (episodes.Count == 0 || episodes.All(i => i.LocationType == LocationType.Virtual));

        if (item.IsVirtualItem != isVirtualItem)
        {
            item.IsVirtualItem = isVirtualItem;
            return ItemUpdateType.MetadataEdit;
        }

        return ItemUpdateType.None;
    }
}
