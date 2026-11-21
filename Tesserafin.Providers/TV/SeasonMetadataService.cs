using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
using Tesserafin.Model.Configuration;
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
    private readonly IItemLookupService _lookup;
    private readonly ICollectionFolderResolver _collections;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeasonMetadataService"/> class.
    /// </summary>
    /// <param name="writes">Explicit item write/resolution ownership.</param>
    /// <param name="peopleCommands">Credit and person metadata writes.</param>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    /// <param name="itemOptions">Effective options for the item's library.</param>
    /// <param name="itemNamingService">Instance of the <see cref="IItemNamingService"/> interface.</param>
    /// <param name="externalDataManager">Instance of the <see cref="IExternalDataManager"/> interface.</param>
    /// <param name="itemRepository">Instance of the <see cref="IItemRepository"/> interface.</param>
    /// <param name="localizationManager">Instance of the <see cref="ILocalizationManager"/> interface.</param>
    /// <param name="listing">Sort and channel services for episode listing.</param>
    /// <param name="lookup">The item identity lookup.</param>
    /// <param name="collections">The library membership resolver.</param>
    public SeasonMetadataService(
        IItemWriteCommands writes,
        IItemPeopleCommands peopleCommands,
        IServerConfigurationManager serverConfigurationManager,
        ILogger<SeasonMetadataService> logger,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILibraryItemOptions itemOptions,
        IItemNamingService itemNamingService,
        IExternalDataManager externalDataManager,
        IItemRepository itemRepository,
        ILocalizationManager localizationManager,
        IItemListingServices listing,
        IItemLookupService lookup,
        ICollectionFolderResolver collections)
        : base(writes, peopleCommands, serverConfigurationManager, logger, providerManager, fileSystem, itemOptions, itemNamingService, externalDataManager, itemRepository, localizationManager)
    {
        _listing = listing;
        _lookup = lookup;
        _collections = collections;
    }

    /// <inheritdoc />
    protected override bool EnableUpdatingPremiereDateFromChildren => true;

    /// <inheritdoc />
    protected override bool BeforeMetadataRefresh(Season item, bool replaceAllMetadata)
        => item.BeforeMetadataRefresh(replaceAllMetadata, ItemNamingService, FileSystem, ServerConfigurationManager.ApplicationPaths, _lookup);

    /// <inheritdoc />
    protected override SeasonInfo GetLookupInfo(Season item, LibraryOptions libraryOptions)
    {
        var locale = item.GetMetadataLocale(_lookup, _collections, libraryOptions, ServerConfigurationManager.Configuration);
        return item.GetLookupInfo(_lookup, locale);
    }

    /// <inheritdoc />
    protected override string CreatePresentationUniqueKey(Season item)
        => item.CreatePresentationUniqueKey(_lookup);

    /// <inheritdoc />
    protected override ItemUpdateType BeforeSaveInternal(Season item, bool isFullRefresh, ItemUpdateType updateType)
    {
        var updatedType = base.BeforeSaveInternal(item, isFullRefresh, updateType);

        if (item.IndexNumber == 0 && !item.IsLocked && !item.LockedFields.Contains(MetadataField.Name))
        {
            var seasonZeroDisplayName = ItemOptions.GetLibraryOptions(item).SeasonZeroDisplayName;

            if (!string.Equals(item.Name, seasonZeroDisplayName, StringComparison.OrdinalIgnoreCase))
            {
                item.Name = seasonZeroDisplayName;
                updatedType |= ItemUpdateType.MetadataEdit;
            }
        }

        var series = item.GetSeries(_lookup);
        var seriesName = series is null ? item.SeriesName : series.Name;
        if (!string.Equals(item.SeriesName, seriesName, StringComparison.Ordinal))
        {
            item.SeriesName = seriesName;
            updatedType |= ItemUpdateType.MetadataImport;
        }

        var seriesPresentationUniqueKey = series?.PresentationUniqueKey;
        if (!string.Equals(item.SeriesPresentationUniqueKey, seriesPresentationUniqueKey, StringComparison.Ordinal))
        {
            item.SeriesPresentationUniqueKey = seriesPresentationUniqueKey;
            updatedType |= ItemUpdateType.MetadataImport;
        }

        var seriesId = item.FindSeriesId(_lookup);
        if (!item.SeriesId.Equals(seriesId))
        {
            item.SeriesId = seriesId;
            updatedType |= ItemUpdateType.MetadataImport;
        }

        return updatedType;
    }

    /// <inheritdoc />
    protected override IReadOnlyList<BaseItem> GetChildrenForMetadataUpdates(Season item)
        => item.GetCatalogueEpisodes(new DtoOptions(true), ItemRepository, _lookup, _listing);

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
