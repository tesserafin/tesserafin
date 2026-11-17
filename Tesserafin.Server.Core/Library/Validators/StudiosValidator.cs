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
/// Class StudiosValidator.
/// </summary>
public class StudiosValidator
{
    private readonly IItemWriteCommands _writes;
    private readonly IItemDeletionCommands _deletions;

    /// <summary>
    /// The library manager.
    /// </summary>
    private readonly ILibraryManager _libraryManager;

    private readonly IItemRepository _itemRepo;

    /// <summary>
    /// The logger.
    /// </summary>
    private readonly ILogger<StudiosValidator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StudiosValidator" /> class.
    /// </summary>
    /// <param name="writes">Explicit item write/resolution ownership.</param>
    /// <param name="deletions">Item deletion operations.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="itemRepo">The item repository.</param>
    public StudiosValidator(IItemWriteCommands writes, IItemDeletionCommands deletions, ILibraryManager libraryManager, ILogger<StudiosValidator> logger, IItemRepository itemRepo)
    {
        _writes = writes;
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
        var names = _itemRepo.GetStudioNames();
        var existingStudioIds = _libraryManager.GetItemIds(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Studio]
        }).ToHashSet();

        var existingStudios = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Studio]
        }).Cast<Studio>()
        .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var numComplete = 0;
        var count = names.Count;
        var refreshed = 0;

        foreach (var name in names)
        {
            try
            {
                Studio? item = null;
                if (existingStudios.TryGetValue(name, out var existingStudio))
                {
                    item = existingStudio;
                }

                // Fall back to GetStudio if not found (creates new item if needed)
                item ??= _writes.GetOrCreateNamedItem<Studio>(name, cancellationToken);

                if (!existingStudioIds.Contains(item.Id))
                {
                    await item.RefreshMetadata(_deletions, _writes, cancellationToken).ConfigureAwait(false);
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
                _logger.LogError(ex, "Error refreshing {StudioName}", name);
            }

            numComplete++;
            double percent = numComplete;
            percent /= count;
            percent *= 100;

            progress.Report(percent);
        }

        _logger.LogInformation("Refreshed metadata for {RefreshedCount} new studios out of {TotalCount} total", refreshed, count);

        var deadEntities = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Studio],
            IsDeadStudio = true,
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
