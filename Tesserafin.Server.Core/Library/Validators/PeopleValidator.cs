using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Data.Enums;
using Tesserafin.Model.IO;

namespace Tesserafin.Server.Core.Library.Validators;

/// <summary>Refreshes credited people and removes unlocked unreferenced person metadata.</summary>
public class PeopleValidator
{
    private readonly IItemWriteCommands _writes;
    private readonly IItemDeletionCommands _deletions;
    private readonly Lazy<IItemMetadataDispatcher> _providers;
    private readonly IItemPeopleService _people;
    private readonly IItemRepository _items;
    private readonly IServerConfigurationManager _configuration;
    private readonly ILogger<PeopleValidator> _logger;
    private readonly IFileSystem _files;

    /// <summary>Initializes a new instance of the <see cref="PeopleValidator"/> class.</summary>
    /// <param name="writes">Explicit item writes.</param>
    /// <param name="deletions">Unreferenced item deletion.</param>
    /// <param name="providers">Runs metadata refreshes, resolved on first use.</param>
    /// <param name="people">Credit and person reads.</param>
    /// <param name="items">Unreferenced person queries.</param>
    /// <param name="configuration">Managed person root configuration.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="files">Metadata refresh directory access.</param>
    public PeopleValidator(IItemWriteCommands writes, IItemDeletionCommands deletions, Lazy<IItemMetadataDispatcher> providers, IItemPeopleService people, IItemRepository items, IServerConfigurationManager configuration, ILogger<PeopleValidator> logger, IFileSystem files)
    {
        _writes = writes;
        _deletions = deletions;
        _providers = providers;
        _people = people;
        _items = items;
        _configuration = configuration;
        _logger = logger;
        _files = files;
    }

    /// <summary>Refreshes existing credited people, then removes unlocked dead person records.</summary>
    /// <param name="cancellationToken">Cancels before admission or between people and deletion batches.</param>
    /// <param name="progress">Receives monotonic completion percentages.</param>
    /// <returns>The validation task.</returns>
    public async Task ValidatePeople(CancellationToken cancellationToken, IProgress<double> progress)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_configuration.ApplicationPaths.PeoplePath);
        var names = _people.GetPeopleNames(new InternalPeopleQuery());
        _logger.LogDebug("Will refresh {Amount} people", names.Count);
        for (var i = 0; i < names.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var person = _people.GetPerson(names[i]);
                if (person is null)
                {
                    _logger.LogWarning("Failed to get person: {Name}", names[i]);
                }
                else
                {
                    var options = new MetadataRefreshOptions(new DirectoryService(_files))
                    {
                        ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                        MetadataRefreshMode = MetadataRefreshMode.ValidationOnly
                    };
                    await person.RefreshMetadata(_deletions, _writes, _providers.Value, options, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating person {Person}", names[i]);
            }

            progress.Report(50.0 * (i + 1) / names.Count);
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(50);
        var dead = _items.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Person],
            IsDeadPerson = true,
            IsLocked = false
        });
        var removed = 0;
        foreach (var batch in dead.Chunk(500))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _deletions.DeleteUnreferencedAsync(batch, true, cancellationToken).ConfigureAwait(false);
            removed += batch.Length;
            progress.Report(50 + (50.0 * removed / dead.Count));
        }

        progress.Report(100);
        _logger.LogInformation("People validation complete");
    }
}
