#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Subtitles;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.Tasks;

namespace Tesserafin.Providers.MediaInfo
{
    public class SubtitleScheduledTask : IScheduledTask
    {
        private readonly IItemWriteCommands _writes;
        private readonly IItemDeletionCommands _deletions;
        private readonly ILibraryRootFolderProvider _roots;
        private readonly ILibraryItemOptions _libraryOptions;
        private readonly IItemQueryService _itemQueries;
        private readonly ISubtitleManager _subtitleManager;
        private readonly ILogger<SubtitleScheduledTask> _logger;
        private readonly ILocalizationManager _localization;
        private readonly ISubtitleProvider[] _subtitleProviders;
        private readonly IMediaSourceManager _mediaSourceManager;

        public SubtitleScheduledTask(
            IItemWriteCommands writes,
            IItemDeletionCommands deletions,
            ILibraryRootFolderProvider roots,
            ILibraryItemOptions libraryOptions,
            IItemQueryService itemQueries,
            ISubtitleManager subtitleManager,
            ILogger<SubtitleScheduledTask> logger,
            ILocalizationManager localization,
            IEnumerable<ISubtitleProvider> subtitleProviders,
            IMediaSourceManager mediaSourceManager)
        {
            _writes = writes;
            _deletions = deletions;
            _roots = roots;
            _libraryOptions = libraryOptions;
            _itemQueries = itemQueries;
            _subtitleManager = subtitleManager;
            _logger = logger;
            _localization = localization;
            _subtitleProviders = subtitleProviders
                .OrderBy(i => i is IHasOrder hasOrder ? hasOrder.Order : 0)
                .ToArray();
            _mediaSourceManager = mediaSourceManager;
        }

        public string Name => _localization.GetLocalizedString("TaskDownloadMissingSubtitles");

        public string Description => _localization.GetLocalizedString("TaskDownloadMissingSubtitlesDescription");

        public string Category => _localization.GetLocalizedString("TasksLibraryCategory");

        public string Key => "DownloadSubtitles";

        public bool IsHidden => false;

        public bool IsEnabled => true;

        public bool IsLogged => true;

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var types = new[] { BaseItemKind.Episode, BaseItemKind.Movie };

            var dict = new Dictionary<Guid, BaseItem>();

            foreach (var library in _roots.GetRootFolder().Children.ToList())
            {
                var libraryOptions = _libraryOptions.GetLibraryOptions(library);

                string[] subtitleDownloadLanguages;
                bool skipIfEmbeddedSubtitlesPresent;
                bool skipIfAudioTrackMatches;

                if (libraryOptions.SubtitleDownloadLanguages is null)
                {
                    // Skip this library if subtitle download languages are not configured
                    continue;
                }

                if (_subtitleProviders.All(provider => libraryOptions.DisabledSubtitleFetchers.Contains(provider.Name, StringComparer.OrdinalIgnoreCase)))
                {
                    // Skip this library if all subtitle providers are disabled
                    continue;
                }

                subtitleDownloadLanguages = libraryOptions.SubtitleDownloadLanguages;
                skipIfEmbeddedSubtitlesPresent = libraryOptions.SkipSubtitlesIfEmbeddedSubtitlesPresent;
                skipIfAudioTrackMatches = libraryOptions.SkipSubtitlesIfAudioTrackMatches;

                foreach (var lang in subtitleDownloadLanguages)
                {
                    var query = new InternalItemsQuery
                    {
                        MediaTypes = new[] { MediaType.Video },
                        IsVirtualItem = false,
                        IncludeItemTypes = types,
                        DtoOptions = new DtoOptions(true),
                        SourceTypes = new[] { SourceType.Library },
                        Parent = library,
                        Recursive = true,
                        IncludeOwnedItems = true
                    };

                    if (skipIfAudioTrackMatches)
                    {
                        query.HasNoAudioTrackWithLanguage = lang;
                    }

                    if (skipIfEmbeddedSubtitlesPresent)
                    {
                        // Exclude if it already has any subtitles of the same language
                        query.HasNoSubtitleTrackWithLanguage = lang;
                    }
                    else
                    {
                        // Exclude if it already has external subtitles of the same language
                        query.HasNoExternalSubtitleTrackWithLanguage = lang;
                    }

                    var videosByLanguage = _itemQueries.GetItemList(query);

                    foreach (var video in videosByLanguage)
                    {
                        dict[video.Id] = video;
                    }
                }
            }

            var videos = dict.Values.ToList();
            if (videos.Count == 0)
            {
                return;
            }

            var numComplete = 0;

            foreach (var video in videos)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await DownloadSubtitles(video as Video, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error downloading subtitles for {Path}", video.Path);
                }

                // Update progress
                numComplete++;
                double percent = numComplete;
                percent /= videos.Count;

                progress.Report(100 * percent);
            }
        }

        private async Task<bool> DownloadSubtitles(Video video, CancellationToken cancellationToken)
        {
            var mediaStreams = video.GetMediaStreams(_mediaSourceManager);

            var libraryOptions = _libraryOptions.GetLibraryOptions(video);

            var downloadedLanguages = await new SubtitleDownloader(
                _logger,
                _subtitleManager).DownloadSubtitles(
                    video,
                    mediaStreams,
                    libraryOptions,
                    true,
                    cancellationToken).ConfigureAwait(false);

            // Rescan
            if (downloadedLanguages.Count > 0)
            {
                await video.RefreshMetadata(_deletions, _writes, cancellationToken).ConfigureAwait(false);
                return false;
            }

            return true;
        }

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                // Every so often
                new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(24).Ticks }
            };
        }
    }
}
