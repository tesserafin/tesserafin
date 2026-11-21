using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.IO;

namespace Tesserafin.Server.Core.Library.Validators;

/// <summary>
/// Class MusicGenresPostScanTask.
/// </summary>
public class MusicGenresPostScanTask : ILibraryPostScanTask
{
    private readonly IItemWriteCommands _writes;
    private readonly IItemDeletionCommands _deletions;
    private readonly IProviderManager _providers;

    /// <summary>
    /// The library manager.
    /// </summary>
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MusicGenresValidator> _logger;
    private readonly IItemRepository _itemRepo;
    private readonly IFileSystem _fileSystem;

    /// <summary>
    /// Initializes a new instance of the <see cref="MusicGenresPostScanTask" /> class.
    /// </summary>
    /// <param name="writes">Explicit item write/resolution ownership.</param>
    /// <param name="deletions">Item deletion operations.</param>
    /// <param name="providers">The provider manager for metadata refreshes.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="itemRepo">The item repository.</param>
    /// <param name="fileSystem">The filesystem used to read refresh directories.</param>
    public MusicGenresPostScanTask(
        IItemWriteCommands writes,
        IItemDeletionCommands deletions,
        IProviderManager providers,
        ILibraryManager libraryManager,
        ILogger<MusicGenresValidator> logger,
        IItemRepository itemRepo,
        IFileSystem fileSystem)
    {
        _writes = writes;
        _deletions = deletions;
        _providers = providers;
        _libraryManager = libraryManager;
        _logger = logger;
        _itemRepo = itemRepo;
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Runs the specified progress.
    /// </summary>
    /// <param name="progress">The progress.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Task.</returns>
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return new MusicGenresValidator(_writes, _deletions, _providers, _libraryManager, _logger, _itemRepo, _fileSystem).Run(progress, cancellationToken);
    }
}
