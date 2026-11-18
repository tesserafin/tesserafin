using System.Linq;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Providers.Manager;

namespace Tesserafin.Providers.Movies;

/// <summary>
/// Service to manage trailer metadata.
/// </summary>
public class TrailerMetadataService : MetadataService<Trailer, TrailerInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TrailerMetadataService"/> class.
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
    public TrailerMetadataService(
        IItemWriteCommands writes,
        IItemPeopleCommands peopleCommands,
        IServerConfigurationManager serverConfigurationManager,
        ILogger<TrailerMetadataService> logger,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILibraryItemOptions itemOptions,
        IItemNamingService itemNamingService,
        IExternalDataManager externalDataManager,
        IItemRepository itemRepository)
        : base(writes, peopleCommands, serverConfigurationManager, logger, providerManager, fileSystem, itemOptions, itemNamingService, externalDataManager, itemRepository)
    {
    }

    /// <inheritdoc />
    protected override void MergeData(MetadataResult<Trailer> source, MetadataResult<Trailer> target, MetadataField[] lockedFields, bool replaceData, bool mergeMetadataSettings)
    {
        base.MergeData(source, target, lockedFields, replaceData, mergeMetadataSettings);

        if (replaceData || target.Item.TrailerTypes.Length == 0)
        {
            target.Item.TrailerTypes = source.Item.TrailerTypes;
        }
        else
        {
            target.Item.TrailerTypes = target.Item.TrailerTypes.Concat(source.Item.TrailerTypes).Distinct().ToArray();
        }
    }
}
