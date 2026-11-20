using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Providers.Manager;

namespace Tesserafin.Providers.TV;

/// <summary>
/// Service to manage episode metadata.
/// </summary>
public class EpisodeMetadataService : MetadataService<Episode, EpisodeInfo>
{
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IMediaSourceManager _mediaSources;
    private readonly IItemLookupService _lookup;
    private readonly ICollectionFolderResolver _collections;

    /// <summary>
    /// Initializes a new instance of the <see cref="EpisodeMetadataService"/> class.
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
    /// <param name="mediaEncoder">Reads embedded metadata from the episode file.</param>
    /// <param name="mediaSources">Resolves the episode's media sources.</param>
    /// <param name="lookup">The item identity lookup.</param>
    /// <param name="collections">The library membership resolver.</param>
    public EpisodeMetadataService(
        IItemWriteCommands writes,
        IItemPeopleCommands peopleCommands,
        IServerConfigurationManager serverConfigurationManager,
        ILogger<EpisodeMetadataService> logger,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILibraryItemOptions itemOptions,
        IItemNamingService itemNamingService,
        IExternalDataManager externalDataManager,
        IItemRepository itemRepository,
        IMediaEncoder mediaEncoder,
        IMediaSourceManager mediaSources,
        IItemLookupService lookup,
        ICollectionFolderResolver collections)
        : base(writes, peopleCommands, serverConfigurationManager, logger, providerManager, fileSystem, itemOptions, itemNamingService, externalDataManager, itemRepository)
    {
        _mediaEncoder = mediaEncoder;
        _mediaSources = mediaSources;
        _lookup = lookup;
        _collections = collections;
    }

    /// <inheritdoc />
    protected override async ValueTask<bool> BeforeMetadataRefreshAsync(Episode item, LibraryOptions libraryOptions, bool replaceAllMetadata, CancellationToken cancellationToken)
    {
        var hasChanges = await base.BeforeMetadataRefreshAsync(item, libraryOptions, replaceAllMetadata, cancellationToken).ConfigureAwait(false);
        if (item.IsLocked || item.SourceType is not (SourceType.Library or SourceType.LiveTV))
        {
            return hasChanges;
        }

        if (libraryOptions.EnableEmbeddedEpisodeInfos && string.Equals(item.Container, "mp4", StringComparison.OrdinalIgnoreCase))
        {
            await ReadEmbeddedEpisodeInfoAsync(item, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            hasChanges |= ItemNamingService.FillMissingEpisodeNumbersFromPath(item, replaceAllMetadata);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in FillMissingEpisodeNumbersFromPath. Episode: {Episode}", item.Path ?? item.Name ?? item.Id.ToString());
        }

        return hasChanges;
    }

    /// <inheritdoc />
    protected override EpisodeInfo GetLookupInfo(Episode item, LibraryOptions libraryOptions)
    {
        var locale = item.GetMetadataLocale(_lookup, _collections, libraryOptions, ServerConfigurationManager.Configuration);
        return item.GetLookupInfo(_lookup, ItemRepository, locale);
    }

    /// <inheritdoc />
    protected override ItemUpdateType BeforeSaveInternal(Episode item, bool isFullRefresh, ItemUpdateType updateType)
    {
        var updatedType = base.BeforeSaveInternal(item, isFullRefresh, updateType);

        var series = item.GetSeries(_lookup);
        var seriesName = series is null ? item.SeriesName : series.Name;
        if (!string.Equals(item.SeriesName, seriesName, StringComparison.Ordinal))
        {
            item.SeriesName = seriesName;
            updatedType |= ItemUpdateType.MetadataImport;
        }

        var seasonName = item.FindSeasonName(_lookup, ItemRepository);
        if (!string.Equals(item.SeasonName, seasonName, StringComparison.Ordinal))
        {
            item.SeasonName = seasonName;
            updatedType |= ItemUpdateType.MetadataImport;
        }

        var seriesId = item.FindSeriesId(_lookup);
        if (!item.SeriesId.Equals(seriesId))
        {
            item.SeriesId = seriesId;
            updatedType |= ItemUpdateType.MetadataImport;
        }

        var seasonId = item.FindSeasonId(_lookup, ItemRepository);
        if (!item.SeasonId.Equals(seasonId))
        {
            item.SeasonId = seasonId;
            updatedType |= ItemUpdateType.MetadataImport;
        }

        var seriesPresentationUniqueKey = item.GetSeries(_lookup)?.PresentationUniqueKey;
        if (!string.Equals(item.SeriesPresentationUniqueKey, seriesPresentationUniqueKey, StringComparison.Ordinal))
        {
            item.SeriesPresentationUniqueKey = seriesPresentationUniqueKey;
            updatedType |= ItemUpdateType.MetadataImport;
        }

        return updatedType;
    }

    /// <inheritdoc />
    protected override void MergeData(MetadataResult<Episode> source, MetadataResult<Episode> target, MetadataField[] lockedFields, bool replaceData, bool mergeMetadataSettings)
    {
        base.MergeData(source, target, lockedFields, replaceData, mergeMetadataSettings);

        var sourceItem = source.Item;
        var targetItem = target.Item;

        if (replaceData || !targetItem.AirsBeforeSeasonNumber.HasValue)
        {
            targetItem.AirsBeforeSeasonNumber = sourceItem.AirsBeforeSeasonNumber;
        }

        if (replaceData || !targetItem.AirsAfterSeasonNumber.HasValue)
        {
            targetItem.AirsAfterSeasonNumber = sourceItem.AirsAfterSeasonNumber;
        }

        if (replaceData || !targetItem.AirsBeforeEpisodeNumber.HasValue)
        {
            targetItem.AirsBeforeEpisodeNumber = sourceItem.AirsBeforeEpisodeNumber;
        }

        if (replaceData || !targetItem.IndexNumberEnd.HasValue)
        {
            targetItem.IndexNumberEnd = sourceItem.IndexNumberEnd;
        }

        // Episode season numbers can be set from path parsing before local metadata is merged.
        // When a provider supplies an explicit season, prefer it during provider->temp and temp->item merges,
        // but avoid clobbering provider data when existing metadata is backfilled into temp.
        if (mergeMetadataSettings
            && sourceItem.ParentIndexNumber.HasValue
            && targetItem.ParentIndexNumber != sourceItem.ParentIndexNumber)
        {
            targetItem.ParentIndexNumber = sourceItem.ParentIndexNumber;
        }
    }

    private async Task ReadEmbeddedEpisodeInfoAsync(Episode episode, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new MediaInfoRequest
            {
                MediaSource = _mediaSources.GetStaticMediaSources(episode, false)[0],
                MediaType = DlnaProfileType.Video
            };
            var mediaInfo = await _mediaEncoder.GetMediaInfo(request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (mediaInfo.ParentIndexNumber > 0)
            {
                episode.ParentIndexNumber = mediaInfo.ParentIndexNumber;
            }

            if (mediaInfo.IndexNumber > 0)
            {
                episode.IndexNumber = mediaInfo.IndexNumber;
            }

            if (!string.IsNullOrEmpty(mediaInfo.ShowName))
            {
                episode.SeriesName = mediaInfo.ShowName;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error reading the episode information with ffprobe. Episode: {EpisodeInfo}", episode.Path);
        }
    }
}
