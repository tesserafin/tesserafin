using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Configuration;

namespace Tesserafin.Server.Core.Library.SimilarItems;

/// <summary>
/// Provides similar items for Live TV programs.
/// </summary>
public class LiveTvProgramSimilarItemsProvider : ILocalSimilarItemsProvider<LiveTvProgram>
{
    private readonly ILibraryManager _libraryManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveTvProgramSimilarItemsProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="serverConfigurationManager">The server configuration manager.</param>
    public LiveTvProgramSimilarItemsProvider(
        ILibraryManager libraryManager,
        IServerConfigurationManager serverConfigurationManager)
    {
        _libraryManager = libraryManager;
        _serverConfigurationManager = serverConfigurationManager;
    }

    /// <inheritdoc/>
    public string Name => "Local Genre/Tag";

    /// <inheritdoc/>
    public MetadataPluginType Type => MetadataPluginType.LocalSimilarityProvider;

    /// <inheritdoc/>
    public Task<IReadOnlyList<BaseItem>> GetSimilarItemsAsync(LiveTvProgram item, SimilarItemsQuery query, CancellationToken cancellationToken)
    {
        BaseItemKind[] includeItemTypes;
        bool enableGroupByMetadataKey;
        bool enableTotalRecordCount;

        if (item.IsMovie)
        {
            // Movie-like program
            var itemTypes = new List<BaseItemKind> { BaseItemKind.Movie };

            if (_serverConfigurationManager.Configuration.EnableExternalContentInSuggestions)
            {
                itemTypes.Add(BaseItemKind.Trailer);
                itemTypes.Add(BaseItemKind.LiveTvProgram);
            }

            includeItemTypes = [.. itemTypes];
            enableGroupByMetadataKey = true;
            enableTotalRecordCount = false;
        }
        else if (item.IsSeries)
        {
            // Series-like program
            includeItemTypes = [BaseItemKind.Series];
            enableGroupByMetadataKey = false;
            enableTotalRecordCount = true;
        }
        else
        {
            // Default - match same type
            includeItemTypes = [item.GetBaseItemKind()];
            enableGroupByMetadataKey = false;
            enableTotalRecordCount = true;
        }

        var internalQuery = new InternalItemsQuery(query.User)
        {
            Genres = item.Genres,
            Tags = item.Tags,
            Limit = query.Limit,
            DtoOptions = query.DtoOptions ?? new DtoOptions(),
            ExcludeItemIds = [.. query.ExcludeItemIds],
            IncludeItemTypes = includeItemTypes,
            EnableGroupByMetadataKey = enableGroupByMetadataKey,
            EnableTotalRecordCount = enableTotalRecordCount,
            OrderBy = [(ItemSortBy.Random, SortOrder.Ascending)]
        };

        return Task.FromResult(_libraryManager.GetItemList(internalQuery));
    }
}
