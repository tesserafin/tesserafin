using System;
using Tesserafin.Controller.LibraryTaskScheduler;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;

namespace Tesserafin.Controller.Library;

/// <summary>
/// The shared catalogue and scheduling services used by folder scans.
/// </summary>
/// <param name="DeletionContext">Checks whether stale items can be removed.</param>
/// <param name="Deletions">Removes stale items during reconciliation.</param>
/// <param name="Writes">Reconciles and updates catalogue items.</param>
/// <param name="Images">Prepares and commits item image changes.</param>
/// <param name="Resolution">Resolves filesystem entries during discovery.</param>
/// <param name="RefreshProgress">Tracks active recursive refreshes.</param>
/// <param name="Scheduler">Schedules nested discovery and metadata work.</param>
/// <param name="Lookup">Reads the catalogue hierarchy.</param>
/// <param name="Repository">Reads the persisted children of each folder.</param>
/// <param name="Store">Registers refreshed library definitions in the item cache.</param>
/// <param name="Metadata">Executes metadata refreshes. Resolution is deferred because the manager also initiates scans.</param>
/// <param name="Links">Resolves saved links during container metadata refresh.</param>
public sealed record LibraryScanContext(
    ItemDeletionContext DeletionContext,
    IItemDeletionCommands Deletions,
    IItemWriteCommands Writes,
    IItemImageService Images,
    IItemResolutionService Resolution,
    ILibraryRefreshProgress RefreshProgress,
    ILimitedConcurrencyLibraryScheduler Scheduler,
    IItemLookupService Lookup,
    IItemRepository Repository,
    IItemStore Store,
    Lazy<IItemMetadataDispatcher> Metadata,
    LinkedChildResolver Links);
