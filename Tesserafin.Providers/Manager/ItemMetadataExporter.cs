using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Extensions;
using Tesserafin.Model.Configuration;

namespace Tesserafin.Providers.Manager;

/// <summary>Owns sidecar export selection and file watcher coordination.</summary>
/// <param name="configuration">Metadata configuration.</param>
/// <param name="options">Effective library options.</param>
/// <param name="monitor">Watcher coordination, evaluated only for an actual file export.</param>
/// <param name="logger">The logger.</param>
public sealed class ItemMetadataExporter(
    IServerConfigurationManager configuration,
    ILibraryItemOptions options,
    Lazy<ILibraryMonitor> monitor,
    ILogger<ItemMetadataExporter> logger) : IItemMetadataExporter
{
    private readonly Lazy<ILibraryMonitor> _monitor = monitor;
    private readonly ILogger<ItemMetadataExporter> _logger = logger;
    private IMetadataSaver[] _savers = [];

    /// <inheritdoc />
    public void AddParts(IEnumerable<IMetadataSaver> savers) => _savers = savers.ToArray();

    /// <inheritdoc />
    public IEnumerable<IMetadataSaver> GetSavers(BaseItem item, LibraryOptions libraryOptions, ItemUpdateType reason, bool includeDisabled)
        => _savers.Where(s => IsEnabled(s, item, libraryOptions, reason, includeDisabled));

    /// <inheritdoc />
    public async Task SaveAsync(BaseItem item, ItemUpdateType updateType, CancellationToken cancellationToken, IEnumerable<string>? selectedSavers = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var libraryOptions = options.GetLibraryOptions(item);
        var savers = selectedSavers is null ? _savers : _savers.Where(i => selectedSavers.Contains(i.Name, StringComparer.OrdinalIgnoreCase));
        var applicableSavers = savers.Where(i => IsEnabled(i, item, libraryOptions, updateType, false)).ToList();
        if (applicableSavers.Count == 0)
        {
            return;
        }

        foreach (var saver in applicableSavers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogDebug("Saving {Item} to {Saver}", (item.Path ?? item.Name).ToSingleLogLine(), saver.Name);

            if (saver is IMetadataFileSaver fileSaver)
            {
                string path;

                try
                {
                    path = fileSaver.GetSavePath(item);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in {Saver} GetSavePath", saver.Name);
                    continue;
                }

                try
                {
                    _monitor.Value.ReportFileSystemChangeBeginning(path);
                    await saver.SaveAsync(item, cancellationToken).ConfigureAwait(false);
                    item.DateLastSaved = DateTime.UtcNow;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in metadata saver");
                }
                finally
                {
                    _monitor.Value.ReportFileSystemChangeComplete(path, false);
                }
            }
            else
            {
                try
                {
                    await saver.SaveAsync(item, cancellationToken).ConfigureAwait(false);
                    item.DateLastSaved = DateTime.UtcNow;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in metadata saver");
                }
            }
        }
    }

    private bool IsEnabled(IMetadataSaver saver, BaseItem item, LibraryOptions libraryOptions, ItemUpdateType updateType, bool includeDisabled)
    {
        var options = configuration.GetMetadataOptionsForType(item.GetType().Name) ?? new MetadataOptions();

        try
        {
            if (!saver.IsEnabledFor(item, updateType))
            {
                return false;
            }

            if (!includeDisabled)
            {
                if (libraryOptions.MetadataSavers is null)
                {
                    if (options.DisabledMetadataSavers.Contains(saver.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    if (!item.IsSaveLocalMetadataEnabled())
                    {
                        if (updateType >= ItemUpdateType.MetadataEdit)
                        {
                            // Manual edit occurred
                            // Even if save local is off, save locally anyway if the metadata file already exists
                            if (saver is not IMetadataFileSaver fileSaver || !File.Exists(fileSaver.GetSavePath(item)))
                            {
                                return false;
                            }
                        }
                        else
                        {
                            // Manual edit did not occur
                            // Since local metadata saving is disabled, consider it disabled
                            return false;
                        }
                    }
                }
                else
                {
                    if (!libraryOptions.MetadataSavers.Contains(saver.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in {Saver}.IsEnabledFor", saver.Name);
            return false;
        }
    }
}
