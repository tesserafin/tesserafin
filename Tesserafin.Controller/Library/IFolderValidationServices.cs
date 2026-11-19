using Tesserafin.Controller.Channels;
using Tesserafin.Controller.LibraryTaskScheduler;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;

namespace Tesserafin.Controller.Library;

/// <summary>
/// The services a folder's validation and recursive refresh use: deletion, writes, image preparation,
/// filesystem candidate resolution, the bounded-concurrency scheduler, refresh tracking and channel removal policy.
/// </summary>
/// <remarks>
/// One port instead of a growing parameter list threaded through <c>Folder.ValidateChildren</c> and its overrides.
/// The channel and provider managers reach validation themselves, so implementations resolve them on first use.
/// </remarks>
public interface IFolderValidationServices
{
    /// <summary>Gets the deletion commands.</summary>
    IItemDeletionCommands Deletions { get; }

    /// <summary>Gets the item write commands.</summary>
    IItemWriteCommands Writes { get; }

    /// <summary>Gets the image preparation and commit service.</summary>
    IItemImageService Images { get; }

    /// <summary>Gets the filesystem candidate resolver for the scan.</summary>
    IItemResolutionService Resolution { get; }

    /// <summary>Gets the catalogue hierarchy reader.</summary>
    IItemLookupService Lookup { get; }

    /// <summary>Gets the catalogue repository for scan child reads.</summary>
    IItemRepository Repository { get; }

    /// <summary>Gets the item cache store.</summary>
    IItemStore Store { get; }

    /// <summary>Gets the bounded-concurrency scheduler.</summary>
    ILimitedConcurrencyLibraryScheduler Scheduler { get; }

    /// <summary>Gets the progress tracker of active recursive refreshes.</summary>
    ILibraryRefreshProgress RefreshProgress { get; }

    /// <summary>Gets the provider manager for metadata refreshes.</summary>
    IProviderManager Providers { get; }

    /// <summary>Gets the channel manager.</summary>
    IChannelManager Channels { get; }
}
