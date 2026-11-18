using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Lyrics;
using Tesserafin.Controller.Persistence;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.Lyrics;
using Tesserafin.Model.Tasks;

namespace Tesserafin.Providers.Lyric;

/// <summary>
/// Task to download lyrics.
/// </summary>
public class LyricScheduledTask : IScheduledTask
{
    private const int QueryPageLimit = 100;

    private static readonly BaseItemKind[] _itemKinds = [BaseItemKind.Audio];
    private static readonly MediaType[] _mediaTypes = [MediaType.Audio];
    private static readonly SourceType[] _sourceTypes = [SourceType.Library];
    private static readonly DtoOptions _dtoOptions = new(false);

    private readonly ILibraryRootFolderProvider _roots;
    private readonly ILibraryItemOptions _libraryOptions;
    private readonly IItemQueryService _itemQueries;
    private readonly IItemCountService _itemCounts;
    private readonly ILyricManager _lyricManager;
    private readonly ILogger<LyricScheduledTask> _logger;
    private readonly ILocalizationManager _localizationManager;
    private readonly IMediaSourceManager _mediaSourceManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LyricScheduledTask"/> class.
    /// </summary>
    /// <param name="roots">Provider for the physical library root.</param>
    /// <param name="libraryOptions">Effective library settings.</param>
    /// <param name="itemQueries">Service for reading catalogue items.</param>
    /// <param name="itemCounts">Service for counting catalogue items.</param>
    /// <param name="lyricManager">Instance of the <see cref="ILyricManager"/> interface.</param>
    /// <param name="logger">Task logger.</param>
    /// <param name="localizationManager">Instance of the <see cref="ILocalizationManager"/> interface.</param>
    /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
    public LyricScheduledTask(
        ILibraryRootFolderProvider roots,
        ILibraryItemOptions libraryOptions,
        IItemQueryService itemQueries,
        IItemCountService itemCounts,
        ILyricManager lyricManager,
        ILogger<LyricScheduledTask> logger,
        ILocalizationManager localizationManager,
        IMediaSourceManager mediaSourceManager)
    {
        _roots = roots;
        _libraryOptions = libraryOptions;
        _itemQueries = itemQueries;
        _itemCounts = itemCounts;
        _lyricManager = lyricManager;
        _logger = logger;
        _localizationManager = localizationManager;
        _mediaSourceManager = mediaSourceManager;
    }

    /// <inheritdoc />
    public string Name => _localizationManager.GetLocalizedString("TaskDownloadMissingLyrics");

    /// <inheritdoc />
    public string Key => "DownloadLyrics";

    /// <inheritdoc />
    public string Description => _localizationManager.GetLocalizedString("TaskDownloadMissingLyricsDescription");

    /// <inheritdoc />
    public string Category => _localizationManager.GetLocalizedString("TasksLibraryCategory");

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var totalCount = _itemCounts.GetCount(new InternalItemsQuery
        {
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = _itemKinds,
            DtoOptions = _dtoOptions,
            MediaTypes = _mediaTypes,
            SourceTypes = _sourceTypes
        });

        var completed = 0;

        foreach (var library in _roots.GetRootFolder().Children.ToList())
        {
            var libraryOptions = _libraryOptions.GetLibraryOptions(library);
            var itemQuery = new InternalItemsQuery
            {
                Recursive = true,
                IsVirtualItem = false,
                IncludeItemTypes = _itemKinds,
                DtoOptions = _dtoOptions,
                MediaTypes = _mediaTypes,
                SourceTypes = _sourceTypes,
                Limit = QueryPageLimit,
                Parent = library
            };

            int previousCount;
            var startIndex = 0;
            do
            {
                itemQuery.StartIndex = startIndex;
                var audioItems = _itemQueries.GetItemList(itemQuery);

                foreach (var audioItem in audioItems.OfType<Audio>())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        if (audioItem.GetMediaStreams(_mediaSourceManager).All(s => s.Type != MediaStreamType.Lyric))
                        {
                            _logger.LogDebug("Searching for lyrics for {Path}", audioItem.Path);
                            var lyricResults = await _lyricManager.SearchLyricsAsync(
                                    new LyricSearchRequest
                                    {
                                        MediaPath = audioItem.Path,
                                        SongName = audioItem.Name,
                                        AlbumName = audioItem.Album,
                                        AlbumArtistsNames = audioItem.AlbumArtists,
                                        ArtistNames = audioItem.Artists,
                                        Duration = audioItem.RunTimeTicks,
                                        IsAutomated = true,
                                        DisabledLyricFetchers = libraryOptions.DisabledLyricFetchers,
                                        LyricFetcherOrder = libraryOptions.LyricFetcherOrder
                                    },
                                    cancellationToken)
                                .ConfigureAwait(false);

                            if (lyricResults.Count != 0)
                            {
                                _logger.LogDebug("Saving lyrics for {Path}", audioItem.Path);
                                await _lyricManager.DownloadLyricsAsync(
                                        audioItem,
                                        libraryOptions,
                                        lyricResults[0].Id,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error downloading lyrics for {Path}", audioItem.Path);
                    }

                    completed++;
                    progress.Report(100d * completed / totalCount);
                }

                startIndex += QueryPageLimit;
                previousCount = audioItems.Count;
            } while (previousCount > 0);
        }

        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks
            }
        ];
    }
}
