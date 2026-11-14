using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.MediaSegments;
using Tesserafin.Controller.Persistence;
using Tesserafin.Database.Implementations;
using Tesserafin.Model.IO;
using Tesserafin.Server.Implementations.Item;
using Tesserafin.Server.Migrations.Stages;
using Tesserafin.Server.ServerSetupApp;

namespace Tesserafin.Server.Migrations.Routines;

/// <summary>
/// Removes orphaned extras (items with OwnerId pointing to non-existent items).
/// Must run before EF migrations that add FK constraints on OwnerId.
/// </summary>
[TesserafinMigration("2026-01-13T23:00:00", nameof(CleanupOrphanedExtras), Stage = TesserafinMigrationStageTypes.AppInitialisation)]
[TesserafinMigrationBackup(TesserafinDb = true)]
public class CleanupOrphanedExtras : IAsyncMigrationRoutine
{
    private readonly IStartupLogger<CleanupOrphanedExtras> _logger;
    private readonly IDbContextFactory<TesserafinDbContext> _dbContextFactory;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="CleanupOrphanedExtras"/> class.
    /// </summary>
    /// <param name="logger">The startup logger.</param>
    /// <param name="dbContextFactory">The database context factory.</param>
    /// <param name="libraryManager">The library manager.</param>
    public CleanupOrphanedExtras(
        IStartupLogger<CleanupOrphanedExtras> logger,
        IDbContextFactory<TesserafinDbContext> dbContextFactory,
        ILibraryManager libraryManager)
    {
        _logger = logger;
        _dbContextFactory = dbContextFactory;
        _libraryManager = libraryManager;
    }

    /// <inheritdoc/>
    public async Task PerformAsync(CancellationToken cancellationToken)
    {
        var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var placeholderOwner = Guid.Parse("00000000-0000-0000-0000-000000000001");
#pragma warning disable RS0030 // Do not use banned APIs
            var orphanedItemIds = await context.BaseItems
                .Where(b => b.OwnerId.HasValue && b.OwnerId == placeholderOwner)
                .Select(b => new
                {
                    b.Id,
                    b.Path,
                    b.Type
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
#pragma warning restore RS0030 // Do not use banned APIs

            if (orphanedItemIds.Count == 0)
            {
                _logger.LogInformation("No orphaned extras found, skipping migration.");
                return;
            }

            _logger.LogInformation("Found {Count} orphaned extras to remove", orphanedItemIds.Count);

            // Resolve items for metadata path cleanup, then delete in batches so we never issue one
            // massive delete transaction and progress stays visible on large libraries.
            _logger.LogInformation("Deleting {Count} orphaned extras...", orphanedItemIds.Count);
            const int deleteBatchSize = 500;
            var deletedSoFar = 0;
            for (var offset = 0; offset < orphanedItemIds.Count; offset += deleteBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batch = orphanedItemIds.GetRange(offset, Math.Min(deleteBatchSize, orphanedItemIds.Count - offset));
                var itemsToDelete = batch
                    .Select(itemId => BaseItemMapper.DeserializeBaseItem(
                        new global::Tesserafin.Data.Entities.BaseItemEntity()
                        {
                            Id = itemId.Id,
                            Path = itemId.Path,
                            Type = itemId.Type
                        },
                        _logger,
                        null,
                        true)!)
                    .ToList();

                _libraryManager.DeleteItemsUnsafeFast(itemsToDelete);

                deletedSoFar += batch.Count;
                _logger.LogInformation("Deleting orphaned extras: {Deleted}/{Total}", deletedSoFar, orphanedItemIds.Count);
            }

            _logger.LogInformation("Successfully removed {Count} orphaned extras", orphanedItemIds.Count);
        }
    }
}
