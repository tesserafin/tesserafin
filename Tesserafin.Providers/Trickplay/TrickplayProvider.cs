using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Trickplay;
using Tesserafin.Model.Configuration;

namespace Tesserafin.Providers.Trickplay;

/// <summary>
/// Class TrickplayProvider. Provides images and metadata for trickplay
/// scrubbing previews.
/// </summary>
public class TrickplayProvider : ICustomMetadataProvider<Episode>,
    ICustomMetadataProvider<MusicVideo>,
    ICustomMetadataProvider<Movie>,
    ICustomMetadataProvider<Trailer>,
    ICustomMetadataProvider<Video>,
    IHasItemChangeMonitor,
    IHasOrder,
    IForcedProvider
{
    private readonly IServerConfigurationManager _config;
    private readonly ITrickplayManager _trickplayManager;
    private readonly ILibraryItemOptions _libraryOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrickplayProvider"/> class.
    /// </summary>
    /// <param name="config">The configuration manager.</param>
    /// <param name="trickplayManager">The trickplay manager.</param>
    /// <param name="libraryOptions">The effective library settings.</param>
    public TrickplayProvider(
        IServerConfigurationManager config,
        ITrickplayManager trickplayManager,
        ILibraryItemOptions libraryOptions)
    {
        _config = config;
        _trickplayManager = trickplayManager;
        _libraryOptions = libraryOptions;
    }

    /// <inheritdoc />
    public string Name => "Trickplay Provider";

    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    public bool HasChanged(BaseItem item, IDirectoryService directoryService)
    {
        if (item.IsFileProtocol)
        {
            var file = directoryService.GetFile(item.Path);
            if (file is not null && item.HasChanged(file.LastWriteTimeUtc))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public Task<ItemUpdateType> FetchAsync(Episode item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        return FetchInternal(item, options, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ItemUpdateType> FetchAsync(MusicVideo item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        return FetchInternal(item, options, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ItemUpdateType> FetchAsync(Movie item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        return FetchInternal(item, options, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ItemUpdateType> FetchAsync(Trailer item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        return FetchInternal(item, options, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ItemUpdateType> FetchAsync(Video item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        return FetchInternal(item, options, cancellationToken);
    }

    private async Task<ItemUpdateType> FetchInternal(Video video, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        var libraryOptions = _libraryOptions.GetLibraryOptions(video);
        bool? enableDuringScan = libraryOptions?.ExtractTrickplayImagesDuringLibraryScan;
        bool replace = options.RegenerateTrickplay && options.MetadataRefreshMode > MetadataRefreshMode.Default;

        if (libraryOptions is null || !enableDuringScan.GetValueOrDefault(false))
        {
            return ItemUpdateType.None;
        }

        if (_config.Configuration.TrickplayOptions.ScanBehavior == TrickplayScanBehavior.Blocking)
        {
            await _trickplayManager.RefreshTrickplayDataAsync(video, replace, libraryOptions, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _ = _trickplayManager.RefreshTrickplayDataAsync(video, replace, libraryOptions, cancellationToken).ConfigureAwait(false);
        }

        // The core doesn't need to trigger any save operations over this
        return ItemUpdateType.None;
    }
}
