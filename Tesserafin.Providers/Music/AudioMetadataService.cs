using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Providers.Manager;

namespace Tesserafin.Providers.Music;

/// <summary>
/// The audio metadata service.
/// </summary>
public class AudioMetadataService : MetadataService<Audio, SongInfo>
{
    private readonly IItemLookupService _lookup;
    private readonly ICollectionFolderResolver _collections;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioMetadataService"/> class.
    /// </summary>
    /// <param name="writes">Explicit item write/resolution ownership.</param>
    /// <param name="peopleCommands">Credit and person metadata writes.</param>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    /// <param name="itemOptions">Effective options for the item's library.</param>
    /// <param name="itemNamingService">Instance of the <see cref="IItemNamingService"/> interface.</param>
    /// <param name="externalDataManager">Instance of the <see cref="IExternalDataManager"/> interface.</param>
    /// <param name="itemRepository">Instance of the <see cref="IItemRepository"/> interface.</param>
    /// <param name="lookup">The item identity lookup.</param>
    /// <param name="collections">The library membership resolver.</param>
    public AudioMetadataService(
        IItemWriteCommands writes,
        IItemPeopleCommands peopleCommands,
        IServerConfigurationManager serverConfigurationManager,
        ILogger<AudioMetadataService> logger,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILibraryItemOptions itemOptions,
        IItemNamingService itemNamingService,
        IExternalDataManager externalDataManager,
        IItemRepository itemRepository,
        IItemLookupService lookup,
        ICollectionFolderResolver collections)
        : base(writes, peopleCommands, serverConfigurationManager, logger, providerManager, fileSystem, itemOptions, itemNamingService, externalDataManager, itemRepository)
    {
        _lookup = lookup;
        _collections = collections;
    }

    /// <inheritdoc />
    protected override SongInfo GetLookupInfo(Audio item, LibraryOptions libraryOptions)
        => item.GetLookupInfo(item.GetMetadataLocale(_lookup, _collections, libraryOptions, ServerConfigurationManager.Configuration));

    private void SetProviderId(Audio sourceItem, Audio targetItem, bool replaceData, MetadataProvider provider)
    {
        var target = targetItem.GetProviderId(provider);
        if (replaceData || string.IsNullOrEmpty(target))
        {
            var source = sourceItem.GetProviderId(provider);
            if (!string.IsNullOrEmpty(source)
                && (string.IsNullOrEmpty(target)
                    || !target.Equals(source, StringComparison.Ordinal)))
            {
                targetItem.SetProviderId(provider, source);
            }
        }
    }

    /// <inheritdoc />
    protected override void MergeData(MetadataResult<Audio> source, MetadataResult<Audio> target, MetadataField[] lockedFields, bool replaceData, bool mergeMetadataSettings)
    {
        base.MergeData(source, target, lockedFields, replaceData, mergeMetadataSettings);

        var sourceItem = source.Item;
        var targetItem = target.Item;

        if (replaceData || targetItem.Artists.Count == 0)
        {
            targetItem.Artists = sourceItem.Artists;
        }
        else
        {
            targetItem.Artists = targetItem.Artists.Concat(sourceItem.Artists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        if (replaceData || string.IsNullOrEmpty(targetItem.Album))
        {
            targetItem.Album = sourceItem.Album;
        }

        SetProviderId(sourceItem, targetItem, replaceData, MetadataProvider.MusicBrainzAlbumArtist);
        SetProviderId(sourceItem, targetItem, replaceData, MetadataProvider.MusicBrainzAlbum);
        SetProviderId(sourceItem, targetItem, replaceData, MetadataProvider.MusicBrainzReleaseGroup);
    }
}
