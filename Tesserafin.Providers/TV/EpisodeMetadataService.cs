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
    private readonly IMediaSourceManager _mediaSourceManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="EpisodeMetadataService"/> class.
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
    /// <param name="mediaEncoder">Reads the episode information embedded in the file.</param>
    /// <param name="mediaSourceManager">Supplies the episode's media source to read.</param>
    public EpisodeMetadataService(
        IItemWriteCommands writes,
        IServerConfigurationManager serverConfigurationManager,
        ILogger<EpisodeMetadataService> logger,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILibraryManager libraryManager,
        IItemNamingService itemNamingService,
        IExternalDataManager externalDataManager,
        IItemRepository itemRepository,
        IMediaEncoder mediaEncoder,
        IMediaSourceManager mediaSourceManager)
        : base(writes, serverConfigurationManager, logger, providerManager, fileSystem, libraryManager, itemNamingService, externalDataManager, itemRepository)
    {
        _mediaEncoder = mediaEncoder;
        _mediaSourceManager = mediaSourceManager;
    }

    /// <inheritdoc />
    /// <remarks>
    /// With <see cref="LibraryOptions.EnableEmbeddedEpisodeInfos"/>, an unlocked library or Live TV mp4 episode
    /// takes its season, episode and show names from the file's own tags. A probe failure is logged and leaves
    /// the episode unchanged; cancellation propagates.
    /// </remarks>
    protected override async Task<bool> BeforeMetadataRefreshAsync(Episode item, LibraryOptions libraryOptions, MetadataRefreshOptions refreshOptions, CancellationToken cancellationToken)
    {
        if (item.IsLocked
            || item.SourceType is not (SourceType.Library or SourceType.LiveTV)
            || !libraryOptions.EnableEmbeddedEpisodeInfos
            || !string.Equals(item.Container, "mp4", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Tesserafin.Model.MediaInfo.MediaInfo mediaInfo;
        try
        {
            mediaInfo = await _mediaEncoder.GetMediaInfo(
                new MediaInfoRequest
                {
                    MediaSource = _mediaSourceManager.GetStaticMediaSources(item, false)[0],
                    MediaType = DlnaProfileType.Video
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Error reading the episode information with ffprobe. Episode: {EpisodeInfo}", item.Path);
            return false;
        }

        var changed = false;
        if (mediaInfo.ParentIndexNumber > 0 && item.ParentIndexNumber != mediaInfo.ParentIndexNumber)
        {
            item.ParentIndexNumber = mediaInfo.ParentIndexNumber;
            changed = true;
        }

        if (mediaInfo.IndexNumber > 0 && item.IndexNumber != mediaInfo.IndexNumber)
        {
            item.IndexNumber = mediaInfo.IndexNumber;
            changed = true;
        }

        if (!string.IsNullOrEmpty(mediaInfo.ShowName) && !string.Equals(item.SeriesName, mediaInfo.ShowName, StringComparison.Ordinal))
        {
            item.SeriesName = mediaInfo.ShowName;
            changed = true;
        }

        return changed;
    }

    /// <inheritdoc />
    protected override ItemUpdateType BeforeSaveInternal(Episode item, bool isFullRefresh, ItemUpdateType updateType)
    {
        var updatedType = base.BeforeSaveInternal(item, isFullRefresh, updateType);

        var seriesName = item.FindSeriesName();
        if (!string.Equals(item.SeriesName, seriesName, StringComparison.Ordinal))
        {
            item.SeriesName = seriesName;
            updatedType |= ItemUpdateType.MetadataImport;
        }

        var seasonName = item.FindSeasonName();
        if (!string.Equals(item.SeasonName, seasonName, StringComparison.Ordinal))
        {
            item.SeasonName = seasonName;
            updatedType |= ItemUpdateType.MetadataImport;
        }

        var seriesId = item.FindSeriesId();
        if (!item.SeriesId.Equals(seriesId))
        {
            item.SeriesId = seriesId;
            updatedType |= ItemUpdateType.MetadataImport;
        }

        var seasonId = item.FindSeasonId();
        if (!item.SeasonId.Equals(seasonId))
        {
            item.SeasonId = seasonId;
            updatedType |= ItemUpdateType.MetadataImport;
        }

        var seriesPresentationUniqueKey = item.FindSeriesPresentationUniqueKey();
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
}
