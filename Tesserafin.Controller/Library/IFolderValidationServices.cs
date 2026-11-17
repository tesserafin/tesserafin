using Tesserafin.Controller.Channels;
using Tesserafin.Controller.LibraryTaskScheduler;

namespace Tesserafin.Controller.Library;

/// <summary>
/// The services a folder's validation and recursive refresh use: deletion, writes, image preparation, the
/// bounded-concurrency scheduler and the channel manager (which decides whether a channel item may be removed).
/// </summary>
/// <remarks>
/// One port instead of a growing parameter list threaded through <c>Folder.ValidateChildren</c> and its overrides.
/// The channel manager reaches validation itself, so implementations resolve it on first use.
/// </remarks>
public interface IFolderValidationServices
{
    /// <summary>Gets the deletion commands.</summary>
    IItemDeletionCommands Deletions { get; }

    /// <summary>Gets the item write commands.</summary>
    IItemWriteCommands Writes { get; }

    /// <summary>Gets the image preparation and commit service.</summary>
    IItemImageService Images { get; }

    /// <summary>Gets the bounded-concurrency scheduler.</summary>
    ILimitedConcurrencyLibraryScheduler Scheduler { get; }

    /// <summary>Gets the channel manager.</summary>
    IChannelManager Channels { get; }
}
