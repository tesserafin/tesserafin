using System;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LibraryTaskScheduler;
using Tesserafin.Controller.Providers;

namespace Tesserafin.Server.Core.Library;

/// <summary>
/// Sole production <see cref="IFolderValidationServices"/>; the channel and provider managers
/// are deferred because they validate and refresh folders themselves.
/// </summary>
internal sealed class FolderValidationServices(
    IItemDeletionCommands deletions,
    IItemWriteCommands writes,
    IItemImageService images,
    IItemResolutionService resolution,
    IItemLookupService lookup,
    ILimitedConcurrencyLibraryScheduler scheduler,
    ILibraryRefreshProgress refreshProgress,
    Lazy<IChannelManager> channels,
    Lazy<IProviderManager> providers) : IFolderValidationServices
{
    /// <inheritdoc />
    public IItemDeletionCommands Deletions { get; } = deletions;

    /// <inheritdoc />
    public IItemWriteCommands Writes { get; } = writes;

    /// <inheritdoc />
    public IItemImageService Images { get; } = images;

    /// <inheritdoc />
    public IItemResolutionService Resolution { get; } = resolution;

    /// <inheritdoc />
    public IItemLookupService Lookup { get; } = lookup;

    /// <inheritdoc />
    public ILimitedConcurrencyLibraryScheduler Scheduler { get; } = scheduler;

    /// <inheritdoc />
    public ILibraryRefreshProgress RefreshProgress { get; } = refreshProgress;

    /// <inheritdoc />
    public IProviderManager Providers => providers.Value;

    /// <inheritdoc />
    public IChannelManager Channels => channels.Value;
}
