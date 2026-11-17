using System;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LibraryTaskScheduler;

namespace Tesserafin.Server.Core.Library;

/// <summary>
/// Sole production <see cref="IFolderValidationServices"/>; the channel manager is deferred because it
/// validates and refreshes folders itself.
/// </summary>
internal sealed class FolderValidationServices(
    IItemDeletionCommands deletions,
    IItemWriteCommands writes,
    IItemImageService images,
    ILimitedConcurrencyLibraryScheduler scheduler,
    Lazy<IChannelManager> channels) : IFolderValidationServices
{
    /// <inheritdoc />
    public IItemDeletionCommands Deletions { get; } = deletions;

    /// <inheritdoc />
    public IItemWriteCommands Writes { get; } = writes;

    /// <inheritdoc />
    public IItemImageService Images { get; } = images;

    /// <inheritdoc />
    public ILimitedConcurrencyLibraryScheduler Scheduler { get; } = scheduler;

    /// <inheritdoc />
    public IChannelManager Channels => channels.Value;
}
