using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Data.Enums;

namespace Tesserafin.Server.Core.Library.Validators;

/// <summary>
/// Class ArtistsValidator.
/// </summary>
public class ArtistsValidator
{
    private readonly IItemWriteCommands _writes;
    private readonly IItemDeletionCommands _deletions;
    private readonly IProviderManager _providers;

    /// <summary>
    /// The library manager.
    /// </summary>
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// The logger.
    /// </summary>
    private readonly ILogger<ArtistsValidator> _logger;
    private readonly IItemRepository _itemRepo;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArtistsValidator" /> class.
    /// </summary>
    /// <param name="writes">Explicit item write/resolution ownership.</param>
    /// <param name="deletions">Item deletion operations.</param>
    /// <param name="providers">The provider manager for metadata refreshes.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="itemRepo">The item repository.</param>
    public ArtistsValidator(IItemWriteCommands writes, IItemDeletionCommands deletions, IProviderManager providers, ILibraryManager libraryManager, ILogger<ArtistsValidator> logger, IItemRepository itemRepo)
    {
        _writes = writes;
        _deletions = deletions;
        _providers = providers;
        _libraryManager = libraryManager;
        _logger = logger;
        _itemRepo = itemRepo;
    }

    /// <summary>
    /// Runs the specified progress.
    /// </summary>
    /// <param name="progress">The progress.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Task.</returns>
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var names = _itemRepo.GetAllArtistNames();
        var existingArtistIds = _libraryManager.GetItemIds(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.MusicArtist]
        }).ToHashSet();

        var existingArtists = _libraryManager.GetArtists(names);

        var numComplete = 0;
        var count = names.Count;
        var refreshed = 0;

        foreach (var name in names)
        {
            try
            {
                MusicArtist? item = null;
                if (existingArtists.TryGetValue(name, out var artists) && artists.Length > 0)
                {
                    item = artists.OrderBy(i => i.IsAccessedByName ? 1 : 0).First();
                }

                // Explicitly materialize missing artist metadata.
                item ??= _writes.GetOrCreateNamedItem<MusicArtist>(name, cancellationToken);
                var isNew = !existingArtistIds.Contains(item.Id);
                var neverRefreshed = item.DateLastRefreshed == default;

                if (isNew || neverRefreshed)
                {
                    await item.RefreshMetadata(_deletions, _writes, _providers, cancellationToken).ConfigureAwait(false);
                    refreshed++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing {ArtistName}", name);
            }

            numComplete++;
            double percent = numComplete;
            percent /= count;
            percent *= 100;

            progress.Report(percent);
        }

        _logger.LogInformation("Refreshed metadata for {RefreshedCount} new artists out of {TotalCount} total", refreshed, count);

        var deadEntities = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.MusicArtist],
            IsDeadArtist = true,
            IsLocked = false
        }).Cast<MusicArtist>()
        .Where(item => item.IsAccessedByName)
        .ToList();

        foreach (var item in deadEntities)
        {
            _logger.LogInformation("Deleting dead {ItemType} {ItemId} {ItemName}", item.GetType().Name, item.Id.ToString("N", CultureInfo.InvariantCulture), item.Name);
        }

        await _deletions.DeleteUnreferencedAsync(deadEntities, true, cancellationToken).ConfigureAwait(false);

        progress.Report(100);
    }
}
