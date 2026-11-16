using System.Threading.Tasks;
using System.Threading;
using System;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;

namespace Tesserafin.Server.Core.Library.Validators;

/// <summary>
/// Class MusicGenresPostScanTask.
/// </summary>
public class StudiosPostScanTask : ILibraryPostScanTask
{
    private readonly IItemWriteCommands _writes;
    private readonly IItemDeletionCommands _deletions;

    /// <summary>
    /// The _library manager.
    /// </summary>
    private readonly ILibraryManager _libraryManager;

    private readonly ILogger<StudiosValidator> _logger;
    private readonly IItemRepository _itemRepo;

    /// <summary>
    /// Initializes a new instance of the <see cref="StudiosPostScanTask" /> class.
    /// </summary>
    /// <param name="writes">Explicit item write/resolution ownership.</param>
    /// <param name="deletions">Item deletion operations.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="itemRepo">The item repository.</param>
    public StudiosPostScanTask(
        IItemWriteCommands writes,
        IItemDeletionCommands deletions,
        ILibraryManager libraryManager,
        ILogger<StudiosValidator> logger,
        IItemRepository itemRepo)
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
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return new StudiosValidator(_writes, _deletions, _libraryManager, _logger, _itemRepo).Run(progress, cancellationToken);
    }
}
