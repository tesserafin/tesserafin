using Tesserafin.Controller.LibraryTaskScheduler;
using Tesserafin.Controller.Persistence;

namespace Tesserafin.Controller.Library;

/// <summary>
/// The shared catalogue and scheduling services used by folder scans.
/// </summary>
/// <param name="Deletions">Removes stale items during reconciliation.</param>
/// <param name="Writes">Reconciles and updates catalogue items.</param>
/// <param name="Images">Prepares and commits item image changes.</param>
/// <param name="Resolution">Resolves filesystem entries during discovery.</param>
/// <param name="RefreshProgress">Tracks active recursive refreshes.</param>
/// <param name="Scheduler">Schedules nested discovery and metadata work.</param>
/// <param name="Lookup">Reads the catalogue hierarchy.</param>
/// <param name="Repository">Reads the persisted children of each folder.</param>
/// <param name="Store">Registers refreshed library definitions in the item cache.</param>
public sealed record LibraryScanContext(
    IItemDeletionCommands Deletions,
    IItemWriteCommands Writes,
    IItemImageService Images,
    IItemResolutionService Resolution,
    ILibraryRefreshProgress RefreshProgress,
    ILimitedConcurrencyLibraryScheduler Scheduler,
    IItemLookupService Lookup,
    IItemRepository Repository,
    IItemStore Store);
