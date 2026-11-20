#nullable disable

#pragma warning disable CA1002, CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Common.Extensions;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Subtitles;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;

namespace Tesserafin.Providers.MediaInfo
{
    public class SubtitleDownloader
    {
        private readonly ILogger _logger;
        private readonly ISubtitleManager _subtitleManager;
        private readonly ActiveRecordings _recordings;

        public SubtitleDownloader(ILogger logger, ISubtitleManager subtitleManager, ActiveRecordings recordings)
        {
            _logger = logger;
            _subtitleManager = subtitleManager;
            _recordings = recordings;
        }

        public async Task<List<string>> DownloadSubtitles(
            Video video,
            IReadOnlyList<MediaStream> mediaStreams,
            LibraryOptions libraryOptions,
            bool isAutomated,
            CancellationToken cancellationToken)
        {
            var languages = libraryOptions.SubtitleDownloadLanguages;
            if (languages is null)
            {
                return [];
            }

            VideoContentType? mediaType = video switch
            {
                Episode => VideoContentType.Episode,
                Movie => VideoContentType.Movie,
                _ => null
            };
            if (!mediaType.HasValue)
            {
                return [];
            }

            var downloadedLanguages = new List<string>();

            foreach (var language in languages)
            {
                if (video.VideoType != VideoType.VideoFile || !video.IsCompleteMedia(_recordings))
                {
                    continue;
                }

                if (await DownloadLanguageAsync(video, mediaStreams, libraryOptions, language, mediaType.Value, isAutomated, cancellationToken).ConfigureAwait(false))
                {
                    downloadedLanguages.Add(language);
                }
            }

            return downloadedLanguages;
        }

        private async Task<bool> DownloadLanguageAsync(
            Video video,
            IReadOnlyList<MediaStream> mediaStreams,
            LibraryOptions libraryOptions,
            string language,
            VideoContentType mediaType,
            bool isAutomated,
            CancellationToken cancellationToken)
        {
            // There's already subtitles for this language
            if (mediaStreams.Any(i => i.Type == MediaStreamType.Subtitle && i.IsTextSubtitleStream && string.Equals(i.Language, language, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var audioStreams = mediaStreams.Where(i => i.Type == MediaStreamType.Audio).ToList();
            var defaultAudioStreams = audioStreams.Where(i => i.IsDefault).ToList();

            // If none are marked as default, just take a guess
            if (defaultAudioStreams.Count == 0)
            {
                defaultAudioStreams = audioStreams.Take(1).ToList();
            }

            // There's already a default audio stream for this language
            if (libraryOptions.SkipSubtitlesIfAudioTrackMatches &&
                defaultAudioStreams.Any(i => string.Equals(i.Language, language, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            // There's an internal subtitle stream for this language
            if (libraryOptions.SkipSubtitlesIfEmbeddedSubtitlesPresent &&
                mediaStreams.Any(i => i.Type == MediaStreamType.Subtitle && !i.IsExternal && string.Equals(i.Language, language, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var request = new SubtitleSearchRequest
            {
                ContentType = mediaType,
                IndexNumber = video.IndexNumber,
                Language = language,
                MediaPath = video.Path,
                Name = video.Name,
                ParentIndexNumber = video.ParentIndexNumber,
                ProductionYear = video.ProductionYear,
                ProviderIds = video.ProviderIds,

                // Stop as soon as we find something
                SearchAllProviders = false,

                IsPerfectMatch = libraryOptions.RequirePerfectSubtitleMatch,
                DisabledSubtitleFetchers = libraryOptions.DisabledSubtitleFetchers,
                SubtitleFetcherOrder = libraryOptions.SubtitleFetcherOrder,
                IsAutomated = isAutomated
            };

            if (video is Episode episode)
            {
                request.IndexNumberEnd = episode.IndexNumberEnd;
                request.SeriesName = episode.SeriesName;
            }

            try
            {
                var searchResults = await _subtitleManager.SearchSubtitles(request, cancellationToken).ConfigureAwait(false);

                var result = searchResults.FirstOrDefault();

                if (result is not null)
                {
                    await _subtitleManager.DownloadSubtitles(video, libraryOptions, result.Id, cancellationToken).ConfigureAwait(false);

                    return true;
                }
            }
            catch (RateLimitExceededException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error downloading subtitles");
            }

            return false;
        }
    }
}
