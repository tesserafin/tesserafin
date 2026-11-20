using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.IO;
using Tesserafin.Providers.Manager;

namespace Tesserafin.Providers.Music;

/// <summary>
/// Service to manage artist metadata.
/// </summary>
public class ArtistMetadataService : MetadataService<MusicArtist, ArtistInfo>
{
    private readonly IItemLookupService _lookup;
    private readonly ICollectionFolderResolver _collections;
    private readonly LinkedChildResolver _links;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArtistMetadataService"/> class.
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
    /// <param name="lookup">The item identity and hierarchy lookup.</param>
    /// <param name="collections">The library membership resolver.</param>
    /// <param name="links">The saved link resolver.</param>
    public ArtistMetadataService(
        IItemWriteCommands writes,
        IItemPeopleCommands peopleCommands,
        IServerConfigurationManager serverConfigurationManager,
        ILogger<ArtistMetadataService> logger,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILibraryItemOptions itemOptions,
        IItemNamingService itemNamingService,
        IExternalDataManager externalDataManager,
        IItemRepository itemRepository,
        IItemLookupService lookup,
        ICollectionFolderResolver collections,
        LinkedChildResolver links)
        : base(writes, peopleCommands, serverConfigurationManager, logger, providerManager, fileSystem, itemOptions, itemNamingService, externalDataManager, itemRepository)
    {
        _lookup = lookup;
        _collections = collections;
        _links = links;
    }

    /// <inheritdoc />
    protected override bool EnableUpdatingGenresFromChildren => true;

    /// <inheritdoc />
    protected override ArtistInfo GetLookupInfo(MusicArtist item, LibraryOptions libraryOptions)
    {
        var configuration = ServerConfigurationManager.Configuration;
        var locale = item.GetMetadataLocale(_lookup, _collections, libraryOptions, configuration);
        var info = item.GetLookupInfo(locale);
        info.SongInfos = item.GetRecursiveCatalogueChildren(ItemRepository, _lookup, _links, static child => child is Audio)
            .Cast<Audio>()
            .Select(track => track.GetLookupInfo(track.GetMetadataLocale(_lookup, _collections, ItemOptions, configuration)))
            .ToList();

        return info;
    }

    /// <inheritdoc />
    protected override IReadOnlyList<BaseItem> GetChildrenForMetadataUpdates(MusicArtist item)
    {
        return item.IsAccessedByName
            ? ItemRepository.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                IsFolder = false,
                IncludeItemTypes = [BaseItemKind.Audio, BaseItemKind.MusicVideo, BaseItemKind.MusicAlbum],
                ArtistIds = [item.Id]
            })
            : item.GetRecursiveCatalogueChildren(ItemRepository, _lookup, _links, static child => child is IHasArtist && !child.IsFolder);
    }
}
