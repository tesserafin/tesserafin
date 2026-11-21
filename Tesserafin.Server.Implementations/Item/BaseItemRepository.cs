using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Database.Implementations;
using Tesserafin.Extensions;
using BaseItemDto = Tesserafin.Controller.Entities.BaseItem;
using BaseItemEntity = Tesserafin.Data.Entities.BaseItemEntity;

namespace Tesserafin.Server.Implementations.Item;

/*
    All queries in this class and all other nullable enabled EFCore repository classes will make liberal use of the null-forgiving operator "!".
    This is done as the code isn't actually executed client side, but only the expressions are interpret and the compiler cannot know that.
    This is your only warning/message regarding this topic.
*/

/// <summary>
/// Handles all storage logic for BaseItems.
/// </summary>
public sealed partial class BaseItemRepository
    : IItemRepository, IItemQueryHelpers
{
    /// <summary>
    /// Gets the placeholder id for UserData detached items.
    /// </summary>
    public static readonly Guid PlaceholderId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly IItemReadServices _readServices;

    private readonly IDbContextFactory<TesserafinDbContext> _dbProvider;
    private readonly IServerApplicationHost _appHost;
    private readonly IItemTypeLookup _itemTypeLookup;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly ILogger<BaseItemRepository> _logger;

    private static readonly IReadOnlyList<ItemValueType> _getAllArtistsValueTypes = [ItemValueType.Artist, ItemValueType.AlbumArtist];
    private static readonly IReadOnlyList<ItemValueType> _getArtistValueTypes = [ItemValueType.Artist];
    private static readonly IReadOnlyList<ItemValueType> _getAlbumArtistValueTypes = [ItemValueType.AlbumArtist];
    private static readonly IReadOnlyList<ItemValueType> _getStudiosValueTypes = [ItemValueType.Studios];
    private static readonly IReadOnlyList<ItemValueType> _getGenreValueTypes = [ItemValueType.Genre];

    /// <summary>
    /// Initializes a new instance of the <see cref="BaseItemRepository"/> class.
    /// </summary>
    /// <param name="dbProvider">The db factory.</param>
    /// <param name="appHost">The Application host.</param>
    /// <param name="itemTypeLookup">The static type lookup.</param>
    /// <param name="serverConfigurationManager">The server Configuration manager.</param>
    /// <param name="logger">System logger.</param>
    /// <param name="activeRecordings">The recordings in progress, for the catalogue reads attached to every loaded item.</param>
    /// <param name="linkedChildren">The deferred resolver for links belonging to loaded items.</param>
    /// <param name="lookup">The deferred cached identity lookup for loaded items.</param>
    /// <param name="links">The persisted version links.</param>
    public BaseItemRepository(
        IDbContextFactory<TesserafinDbContext> dbProvider,
        IServerApplicationHost appHost,
        IItemTypeLookup itemTypeLookup,
        IServerConfigurationManager serverConfigurationManager,
        ILogger<BaseItemRepository> logger,
        ActiveRecordings activeRecordings,
        Lazy<LinkedChildResolver> linkedChildren,
        Lazy<IItemLookupService> lookup,
        Lazy<ILinkedChildrenService> links)
    {
        _readServices = new ItemReadServices(this, activeRecordings, linkedChildren, serverConfigurationManager, lookup, links);
        _dbProvider = dbProvider;
        _appHost = appHost;
        _itemTypeLookup = itemTypeLookup;
        _serverConfigurationManager = serverConfigurationManager;
        _logger = logger;
    }

    /// <summary>
    /// Maps a Entity to the DTO. Delegates to <see cref="BaseItemMapper"/>.
    /// </summary>
    /// <param name="entity">The database entity.</param>
    /// <param name="dto">The target DTO.</param>
    /// <param name="appHost">The application host.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The mapped DTO.</returns>
    public static BaseItemDto Map(BaseItemEntity entity, BaseItemDto dto, IServerApplicationHost? appHost, ILogger logger)
    {
        return BaseItemMapper.Map(entity, dto, appHost);
    }

    /// <summary>
    /// Maps a DTO to a database entity. Delegates to <see cref="BaseItemMapper"/>.
    /// </summary>
    /// <param name="dto">The DTO to map.</param>
    /// <returns>The mapped database entity.</returns>
    public BaseItemEntity Map(BaseItemDto dto)
    {
        return BaseItemMapper.Map(dto, _appHost);
    }

    /// <summary>
    /// Deserializes a BaseItemEntity and sets all properties.
    /// </summary>
    /// <param name="baseItemEntity">The entity to deserialize.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="appHost">The application host.</param>
    /// <param name="skipDeserialization">Whether to skip JSON deserialization.</param>
    /// <returns>The deserialized item, or null.</returns>
    public static BaseItemDto? DeserializeBaseItem(BaseItemEntity baseItemEntity, ILogger logger, IServerApplicationHost? appHost, bool skipDeserialization = false)
    {
        return BaseItemMapper.DeserializeBaseItem(baseItemEntity, logger, appHost, skipDeserialization);
    }

    /// <inheritdoc />
    public void PrepareFilterQuery(InternalItemsQuery query)
    {
        if (query.Limit.HasValue && query.EnableGroupByMetadataKey)
        {
            query.Limit = query.Limit.Value + 4;
        }

        if (query.IsResumable ?? false)
        {
            query.IsVirtualItem = false;
        }
    }

    private List<string> GetItemByNameTypesInQuery(InternalItemsQuery query)
    {
        var list = new List<string>();

        if (IsTypeInQuery(BaseItemKind.Person, query))
        {
            list.Add(_itemTypeLookup.BaseItemKindNames[BaseItemKind.Person]!);
        }

        if (IsTypeInQuery(BaseItemKind.Genre, query))
        {
            list.Add(_itemTypeLookup.BaseItemKindNames[BaseItemKind.Genre]!);
        }

        if (IsTypeInQuery(BaseItemKind.MusicGenre, query))
        {
            list.Add(_itemTypeLookup.BaseItemKindNames[BaseItemKind.MusicGenre]!);
        }

        if (IsTypeInQuery(BaseItemKind.MusicArtist, query))
        {
            list.Add(_itemTypeLookup.BaseItemKindNames[BaseItemKind.MusicArtist]!);
        }

        if (IsTypeInQuery(BaseItemKind.Studio, query))
        {
            list.Add(_itemTypeLookup.BaseItemKindNames[BaseItemKind.Studio]!);
        }

        return list;
    }

    private bool IsTypeInQuery(BaseItemKind type, InternalItemsQuery query)
    {
        if (query.ExcludeItemTypes.Contains(type))
        {
            return false;
        }

        return query.IncludeItemTypes.Length == 0 || query.IncludeItemTypes.Contains(type);
    }

    private bool EnableGroupByPresentationUniqueKey(InternalItemsQuery query)
    {
        if (!query.GroupByPresentationUniqueKey)
        {
            return false;
        }

        // Resume queries surface the actually-played version (which may be an alternate sharing the
        // primary's presentation key). The resumable filter already keeps one version per group, so
        // presentation-key grouping must not collapse the surfaced version back onto the primary.
        if (query.IsResumable == true)
        {
            return false;
        }

        if (query.GroupBySeriesPresentationUniqueKey)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(query.PresentationUniqueKey))
        {
            return false;
        }

        if (query.User is null)
        {
            return false;
        }

        if (query.IncludeItemTypes.Length == 0)
        {
            return true;
        }

        return query.IncludeItemTypes.Contains(BaseItemKind.Episode)
            || query.IncludeItemTypes.Contains(BaseItemKind.Video)
            || query.IncludeItemTypes.Contains(BaseItemKind.Movie)
            || query.IncludeItemTypes.Contains(BaseItemKind.MusicVideo)
            || query.IncludeItemTypes.Contains(BaseItemKind.Series)
            || query.IncludeItemTypes.Contains(BaseItemKind.Season);
    }

    private static BaseItemImageInfo Map(Guid baseItemId, ItemImageInfo e)
    {
        return BaseItemMapper.MapImageToEntity(baseItemId, e);
    }

    private static ItemImageInfo Map(BaseItemImageInfo e, IServerApplicationHost? appHost)
    {
        return BaseItemMapper.MapImageFromEntity(e, appHost);
    }

    private string? GetPathToSave(string path)
    {
        if (path is null)
        {
            return null;
        }

        return _appHost.ReverseVirtualPath(path);
    }

    /// <inheritdoc />
    public BaseItemDto? DeserializeBaseItem(BaseItemEntity entity, bool skipDeserialization = false)
    {
        ArgumentNullException.ThrowIfNull(entity, nameof(entity));
        if (_serverConfigurationManager?.Configuration is null)
        {
            throw new InvalidOperationException("Server Configuration manager or configuration is null");
        }

        var typeToSerialise = BaseItemMapper.GetType(entity.Type);
        return BaseItemMapper.DeserializeBaseItem(
            entity,
            _logger,
            _appHost,
            skipDeserialization || (_serverConfigurationManager.Configuration.SkipDeserializationForBasicTypes && (typeToSerialise == typeof(Channel) || typeToSerialise == typeof(UserRootFolder))),
            _readServices);
    }
}
