using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Data.Enums;

namespace Tesserafin.Server.Core.Library.Validators;

/// <summary>
/// Class GenresValidator.
/// </summary>
public class GenresValidator
{
    private readonly IItemDeletionCommands _deletions;

    /// <summary>
    /// The library manager.
    /// </summary>
    private readonly ILibraryManager _libraryManager;
    private readonly IItemRepository _itemRepo;

    /// <summary>
    /// The logger.
    /// </summary>
    private readonly ILogger<GenresValidator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GenresValidator"/> class.
    /// </summary>
    /// <param name="deletions">Item deletion operations.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="itemRepo">The item repository.</param>
    public GenresValidator(IItemDeletionCommands deletions, ILibraryManager libraryManager, ILogger<GenresValidator> logger, IItemRepository itemRepo)
    {
        _deletions = deletions;
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
        var names = _itemRepo.GetGenreNames();
        var existingGenreIds = _libraryManager.GetItemIds(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Genre]
        }).ToHashSet();

        var existingGenres = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Genre]
        }).Cast<Genre>()
        .GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var numComplete = 0;
        var count = names.Count;
        var refreshed = 0;

        foreach (var name in names)
        {
            try
            {
                Genre? item = null;
                if (existingGenres.TryGetValue(name, out var existingGenre))
                {
                    item = existingGenre;
                }

                // Fall back to GetGenre if not found (creates new item if needed)
                item ??= _libraryManager.GetGenre(name);

                if (!existingGenreIds.Contains(item.Id))
                {
                    await item.RefreshMetadata(_deletions, cancellationToken).ConfigureAwait(false);
                    refreshed++;
                }
            }
            catch (OperationCanceledException)
            {
                // Don't clutter the log
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing {GenreName}", name);
            }

            numComplete++;
            double percent = numComplete;
            percent /= count;
            percent *= 100;

            progress.Report(percent);
        }

        _logger.LogInformation("Refreshed metadata for {RefreshedCount} new genres out of {TotalCount} total", refreshed, count);

        var deadEntities = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Genre, BaseItemKind.MusicGenre],
            IsDeadGenre = true,
            IsLocked = false
        });

        foreach (var item in deadEntities)
        {
            _logger.LogInformation("Deleting dead {ItemType} {ItemId} {ItemName}", item.GetType().Name, item.Id.ToString("N", CultureInfo.InvariantCulture), item.Name);
        }

        await _deletions.DeleteUnreferencedAsync(deadEntities, true, cancellationToken).ConfigureAwait(false);

        progress.Report(100);
    }
}
