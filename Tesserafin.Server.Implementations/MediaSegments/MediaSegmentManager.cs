using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tesserafin.Common.Extensions;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.MediaSegments;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Database.Implementations;
using Tesserafin.Extensions;
using Tesserafin.Model;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.MediaSegments;

namespace Tesserafin.Server.Implementations.MediaSegments;

/// <summary>
/// Manages media segments retrieval and storage.
/// </summary>
public class MediaSegmentManager : IMediaSegmentManager
{
    private const string ChapterProviderName = "Embedded intro chapters";

    private readonly IChapterRepository _chapters;
    private readonly ILogger<MediaSegmentManager> _logger;
    private readonly IDbContextFactory<TesserafinDbContext> _dbProvider;
    private readonly IMediaSegmentProvider[] _segmentProviders;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaSegmentManager"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="dbProvider">EFCore Database factory.</param>
    /// <param name="chapters">The chapter repository.</param>
    /// <param name="segmentProviders">List of all media segment providers.</param>
    public MediaSegmentManager(
        ILogger<MediaSegmentManager> logger,
        IDbContextFactory<TesserafinDbContext> dbProvider,
        IEnumerable<IMediaSegmentProvider> segmentProviders,
        IChapterRepository chapters)
    {
        _logger = logger;
        _dbProvider = dbProvider;
        _chapters = chapters;

        _segmentProviders = segmentProviders
            .OrderBy(i => i is IHasOrder hasOrder ? hasOrder.Order : 0)
            .ToArray();
    }

    /// <inheritdoc/>
    public async Task RunSegmentPluginProviders(BaseItem baseItem, LibraryOptions libraryOptions, bool forceOverwrite, CancellationToken cancellationToken)
    {
        var providers = _segmentProviders
            .Where(e => !libraryOptions.DisabledMediaSegmentProviders.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(i =>
                {
                    var index = libraryOptions.MediaSegmentProviderOrder.IndexOf(i.Name);
                    return index == -1 ? int.MaxValue : index;
                })
            .ToList();

        if (providers.Count == 0)
        {
            _logger.LogDebug("Skipping media segment extraction as no providers are enabled for {MediaPath}", baseItem.Path);
            return;
        }

        var db = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            _logger.LogDebug("Start media segment extraction for {MediaPath} with {CountProviders} providers enabled", baseItem.Path, providers.Count);

            if (forceOverwrite)
            {
                // delete all existing media segments if forceOverwrite is set.
                await db.MediaSegments.Where(e => e.ItemId.Equals(baseItem.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var provider in providers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!await provider.Supports(baseItem).ConfigureAwait(false))
                {
                    _logger.LogDebug("Media Segment provider {ProviderName} does not support item with path {MediaPath}", provider.Name, baseItem.Path);
                    continue;
                }

                IQueryable<MediaSegment> existingSegments;
                if (forceOverwrite)
                {
                    existingSegments = Array.Empty<MediaSegment>().AsQueryable();
                }
                else
                {
                    existingSegments = db.MediaSegments.Where(e => e.ItemId.Equals(baseItem.Id) && e.SegmentProviderId == GetProviderId(provider.Name));
                }

                var requestItem = new MediaSegmentGenerationRequest()
                {
                    ItemId = baseItem.Id,
                    ExistingSegments = existingSegments.Select(e => Map(e)).ToArray()
                };

                try
                {
                    var segments = await provider.GetMediaSegments(requestItem, cancellationToken)
                        .ConfigureAwait(false);

                    if (!forceOverwrite)
                    {
                        var existingSegmentsList = existingSegments.ToArray(); // Cannot use requestItem's list, as the provider might tamper with its items.
                        if (segments.Count == requestItem.ExistingSegments.Count && segments.All(e => existingSegmentsList.Any(f =>
                        {
                            return
                                e.StartTicks == f.StartTicks &&
                                e.EndTicks == f.EndTicks &&
                                e.Type == f.Type;
                        })))
                        {
                            _logger.LogDebug("Media Segment provider {ProviderName} did not modify any segments for {MediaPath}", provider.Name, baseItem.Path);
                            continue;
                        }

                        // delete existing media segments that were re-generated.
                        await existingSegments.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                    }

                    if (segments.Count == 0 && !requestItem.ExistingSegments.Any())
                    {
                        _logger.LogDebug("Media Segment provider {ProviderName} did not find any segments for {MediaPath}", provider.Name, baseItem.Path);
                        continue;
                    }
                    else if (segments.Count == 0 && requestItem.ExistingSegments.Any())
                    {
                        _logger.LogDebug("Media Segment provider {ProviderName} deleted all segments for {MediaPath}", provider.Name, baseItem.Path);
                        continue;
                    }

                    _logger.LogInformation("Media Segment provider {ProviderName} found {CountSegments} for {MediaPath}", provider.Name, segments.Count, baseItem.Path);
                    var providerId = GetProviderId(provider.Name);
                    foreach (var segment in segments)
                    {
                        segment.ItemId = baseItem.Id;
                        await CreateSegmentAsync(segment, providerId).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug(ex, "Provider {ProviderName} aborted segment extraction for {MediaPath} due to shutdown", provider.Name, baseItem.Path);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Provider {ProviderName} failed to extract segments from {MediaPath}", provider.Name, baseItem.Path);
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task<MediaSegmentDto> CreateSegmentAsync(MediaSegmentDto mediaSegment, string segmentProviderId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(mediaSegment.EndTicks, mediaSegment.StartTicks);

        var db = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            db.MediaSegments.Add(Map(mediaSegment, segmentProviderId));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        return mediaSegment;
    }

    /// <inheritdoc />
    public async Task DeleteSegmentAsync(Guid segmentId)
    {
        var db = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await db.MediaSegments.Where(e => e.Id.Equals(segmentId)).ExecuteDeleteAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task DeleteSegmentsAsync(Guid itemId, CancellationToken cancellationToken)
    {
        foreach (var provider in _segmentProviders)
        {
            try
            {
                await provider.CleanupExtractedData(itemId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Provider {ProviderName} failed to clean up extracted data for item {ItemId}", provider.Name, itemId);
            }
        }

        var db = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await db.MediaSegments.Where(e => e.ItemId.Equals(itemId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<MediaSegmentDto>> GetSegmentsAsync(BaseItem? item, IEnumerable<MediaSegmentType>? typeFilter, LibraryOptions libraryOptions, bool filterByProvider = true)
    {
        if (item is null)
        {
            _logger.LogError("Tried to request segments for an invalid item");
            return [];
        }

        var db = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var query = db.MediaSegments
                .Where(e => e.ItemId.Equals(item.Id));

            var types = typeFilter?.ToArray();
            if (types is not null)
            {
                query = query.Where(e => types.Contains(e.Type));
            }

            if (filterByProvider)
            {
                var providerIds = _segmentProviders
                    .Where(e => !libraryOptions.DisabledMediaSegmentProviders.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
                    .Select(f => GetProviderId(f.Name))
                    .ToArray();
                query = query.Where(e => providerIds.Contains(e.SegmentProviderId));
            }

            var segments = await query
                .OrderBy(e => e.StartTicks)
                .AsNoTracking()
                .ToArrayAsync().ConfigureAwait(false);
            var result = segments.Select(Map);
            if ((types is null || types.Contains(MediaSegmentType.Intro))
                && (!filterByProvider || !libraryOptions.DisabledMediaSegmentProviders.Contains(ChapterProviderName, StringComparer.OrdinalIgnoreCase))
                && !segments.Any(segment => segment.Type == MediaSegmentType.Intro)
                && GetChapterIntro(item) is { } intro)
            {
                return result.Append(intro).OrderBy(segment => segment.StartTicks).ToArray();
            }

            return result.ToArray();
        }
    }

    private MediaSegmentDto? GetChapterIntro(BaseItem item)
    {
        // An item-level segment can only describe one timeline.
        if (item is not Episode { IsFileProtocol: true, IsVirtualItem: false, IsStacked: false } episode
            || episode.LocalAlternateVersions.Length != 0
            || episode.LinkedAlternateVersions.Length != 0
            || episode.RunTimeTicks is not > 0)
        {
            return null;
        }

        // Use the current chapter snapshot, so refreshing the media also replaces its intro boundaries.
        var chapters = _chapters.GetChapters(episode.Id);
        int introIndex = -1;
        for (int i = 0; i < chapters.Count; i++)
        {
            if (string.Equals(chapters[i].Name?.Trim(), "Intro", StringComparison.OrdinalIgnoreCase))
            {
                if (introIndex != -1)
                {
                    return null;
                }

                introIndex = i;
            }
        }

        if (introIndex < 0 || introIndex + 1 == chapters.Count)
        {
            return null;
        }

        var start = chapters[introIndex].StartPositionTicks;
        var end = chapters[introIndex + 1].StartPositionTicks;
        if (start < 0 || end <= start || end >= episode.RunTimeTicks)
        {
            return null;
        }

        return new MediaSegmentDto
        {
            Id = FormattableString.Invariant($"{ChapterProviderName}/{episode.Id:N}/{start}/{end}").GetMD5(),
            ItemId = episode.Id,
            Type = MediaSegmentType.Intro,
            StartTicks = start,
            EndTicks = end,
        };
    }

    private static MediaSegmentDto Map(MediaSegment segment)
    {
        return new MediaSegmentDto()
        {
            Id = segment.Id,
            EndTicks = segment.EndTicks,
            ItemId = segment.ItemId,
            StartTicks = segment.StartTicks,
            Type = segment.Type
        };
    }

    private static MediaSegment Map(MediaSegmentDto segment, string segmentProviderId)
    {
        return new MediaSegment()
        {
            Id = segment.Id,
            EndTicks = segment.EndTicks,
            ItemId = segment.ItemId,
            StartTicks = segment.StartTicks,
            Type = segment.Type,
            SegmentProviderId = segmentProviderId
        };
    }

    /// <inheritdoc />
    public bool HasSegments(BaseItem item)
    {
        using var db = _dbProvider.CreateDbContext();
        return db.MediaSegments.Any(e => e.ItemId.Equals(item.Id)) || GetChapterIntro(item) is not null;
    }

    /// <inheritdoc/>
    public bool IsTypeSupported(BaseItem baseItem)
    {
        return baseItem.MediaType is global::Tesserafin.Data.Enums.MediaType.Video or global::Tesserafin.Data.Enums.MediaType.Audio;
    }

    /// <inheritdoc/>
    public IEnumerable<(string Name, string Id)> GetSupportedProviders(BaseItem item)
    {
        if (item is not (Video or Audio))
        {
            return [];
        }

        var providers = _segmentProviders.Select(p => (p.Name, GetProviderId(p.Name)));
        return item is Episode ? providers.Append((ChapterProviderName, GetProviderId(ChapterProviderName))) : providers;
    }

    private string GetProviderId(string name)
        => name.ToLowerInvariant()
            .GetMD5()
            .ToString("N", CultureInfo.InvariantCulture);
}
