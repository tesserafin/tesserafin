using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Model.Globalization;
using Tesserafin.Server.ServerSetupApp;

namespace Tesserafin.Server.Migrations.Routines;

/// <summary>
/// Migration to fix broken library subtitle download languages.
/// </summary>
[TesserafinMigration("2026-02-06T20:00:00", nameof(FixLibrarySubtitleDownloadLanguages))]
internal class FixLibrarySubtitleDownloadLanguages : IAsyncMigrationRoutine
{
    private readonly ILocalizationManager _localizationManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryStructureReader _libraryStructure;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FixLibrarySubtitleDownloadLanguages"/> class.
    /// </summary>
    /// <param name="localizationManager">The Localization manager.</param>
    /// <param name="startupLogger">The startup logger for Startup UI integration.</param>
    /// <param name="libraryStructure">Configured library reader.</param>
    /// <param name="libraryManager">The Library manager.</param>
    /// <param name="logger">The logger.</param>
    public FixLibrarySubtitleDownloadLanguages(
        ILocalizationManager localizationManager,
        IStartupLogger<FixLibrarySubtitleDownloadLanguages> startupLogger,
        ILibraryManager libraryManager,
        ILibraryStructureReader libraryStructure,
        ILogger<FixLibrarySubtitleDownloadLanguages> logger)
    {
        _localizationManager = localizationManager;
        _libraryManager = libraryManager;
        _libraryStructure = libraryStructure;
        _logger = startupLogger.With(logger);
    }

    /// <inheritdoc />
    public Task PerformAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting to fix library subtitle download languages.");

        var virtualFolders = _libraryStructure.GetVirtualFolders();

        foreach (var virtualFolder in virtualFolders)
        {
            var options = virtualFolder.LibraryOptions;
            if (options?.SubtitleDownloadLanguages is null || options.SubtitleDownloadLanguages.Length == 0)
            {
                continue;
            }

            // Some virtual folders don't have a proper item id.
            if (!Guid.TryParse(virtualFolder.ItemId, out var folderId))
            {
                continue;
            }

            var collectionFolder = _libraryManager.GetItemById<CollectionFolder>(folderId);
            if (collectionFolder is null)
            {
                _logger.LogWarning("Could not find collection folder for virtual folder '{LibraryName}' with id '{FolderId}'. Skipping.", virtualFolder.Name, folderId);
                continue;
            }

            var fixedLanguages = new List<string>();

            foreach (var language in options.SubtitleDownloadLanguages)
            {
                var foundLanguage = _localizationManager.FindLanguageInfo(language)?.ThreeLetterISOLanguageName;
                if (foundLanguage is not null)
                {
                    // Converted ISO 639-2/B to T (ger to deu)
                    if (!string.Equals(foundLanguage, language, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("Converted '{Language}' to '{ResolvedLanguage}' in library '{LibraryName}'.", language, foundLanguage, virtualFolder.Name);
                    }

                    if (fixedLanguages.Contains(foundLanguage, StringComparer.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("Language '{Language}' already exists for library '{LibraryName}'. Skipping duplicate.", foundLanguage, virtualFolder.Name);
                        continue;
                    }

                    fixedLanguages.Add(foundLanguage);
                }
                else
                {
                    _logger.LogInformation("Could not resolve language '{Language}' in library '{LibraryName}'. Skipping.", language, virtualFolder.Name);
                }
            }

            options.SubtitleDownloadLanguages = [.. fixedLanguages];
            collectionFolder.UpdateLibraryOptions(options);
        }

        _logger.LogInformation("Library subtitle download languages fixed.");

        return Task.CompletedTask;
    }
}
