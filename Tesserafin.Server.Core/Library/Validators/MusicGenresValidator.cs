using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Data.Enums;

namespace Tesserafin.Server.Core.Library.Validators;

/// <summary>
/// Class MusicGenresValidator.
/// </summary>
public class MusicGenresValidator
{
    private readonly IItemWriteCommands _writes;
    private readonly IItemDeletionCommands _deletions;

    /// <summary>
    /// The library manager.
    /// </summary>
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// The logger.
    /// </summary>
    private readonly ILogger<MusicGenresValidator> _logger;
    private readonly IItemRepository _itemRepo;

    /// <summary>
    /// Initializes a new instance of the <see cref="MusicGenresValidator" /> class.
    /// </summary>
    /// <param name="writes">Explicit item write/resolution ownership.</param>
    /// <param name="deletions">Item deletion operations.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="itemRepo">The item repository.</param>
    public MusicGenresValidator(IItemWriteCommands writes, IItemDeletionCommands deletions, ILibraryManager libraryManager, ILogger<MusicGenresValidator> logger, IItemRepository itemRepo)
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
        var names = _itemRepo.GetMusicGenreNames();
        var existingMusicGenreIds = _libraryManager.GetItemIds(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.MusicGenre]
        }).ToHashSet();

        var numComplete = 0;
        var count = names.Count;
        var refreshed = 0;

        foreach (var name in names)
        {
            try
            {
                var item = _writes.GetOrCreateNamedItem<MusicGenre>(name, cancellationToken);
                if (!existingMusicGenreIds.Contains(item.Id))
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
                _logger.LogError(ex, "Error refreshing {GenreName}", name);
            }

            numComplete++;
            double percent = numComplete;
            percent /= count;
            percent *= 100;

            progress.Report(percent);
        }

        _logger.LogInformation("Refreshed metadata for {RefreshedCount} new music genres out of {TotalCount} total", refreshed, count);

        progress.Report(100);
    }
}
