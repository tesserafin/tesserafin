using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tesserafin.Api.Extensions;
using Tesserafin.Api.Helpers;
using Tesserafin.Api.ModelBinders;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Sorting;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Querying;

namespace Tesserafin.Api.Controllers;

/// <summary>
/// Years controller.
/// </summary>
[Authorize]
[Tags("Year")]
public class YearsController : BaseTesserafinApiController
{
    private readonly INamedItemCatalog _namedItems;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IDtoService _dtoService;
    private readonly IItemSortService _itemSortService;

    /// <summary>
    /// Initializes a new instance of the <see cref="YearsController"/> class.
    /// </summary>
    /// <param name="namedItems">Read-only named catalogue.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="dtoService">Instance of the <see cref="IDtoService"/> interface.</param>
    /// <param name="itemSortService">Instance of the <see cref="IItemSortService"/> interface.</param>
    public YearsController(
        INamedItemCatalog namedItems,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IDtoService dtoService,
        IItemSortService itemSortService)
    {
        _namedItems = namedItems;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _dtoService = dtoService;
        _itemSortService = itemSortService;
    }

    /// <summary>
    /// Get years.
    /// </summary>
    /// <param name="startIndex">Skips over a given number of items within the results. Use for paging.</param>
    /// <param name="limit">Optional. The maximum number of records to return.</param>
    /// <param name="sortOrder">Sort Order - Ascending,Descending.</param>
    /// <param name="parentId">Specify this to localize the search to a specific item or folder. Omit to use the root.</param>
    /// <param name="fields">Optional. Specify additional fields of information to return in the output.</param>
    /// <param name="excludeItemTypes">Optional. If specified, results will be excluded based on item type. This allows multiple, comma delimited.</param>
    /// <param name="includeItemTypes">Optional. If specified, results will be included based on item type. This allows multiple, comma delimited.</param>
    /// <param name="mediaTypes">Optional. Filter by MediaType. Allows multiple, comma delimited.</param>
    /// <param name="sortBy">Optional. Specify one or more sort orders, comma delimited. Options: Album, AlbumArtist, Artist, Budget, CommunityRating, CriticRating, DateCreated, DatePlayed, PlayCount, PremiereDate, ProductionYear, SortName, Random, Revenue, Runtime.</param>
    /// <param name="enableUserData">Optional. Include user data.</param>
    /// <param name="imageTypeLimit">Optional. The max number of images to return, per image type.</param>
    /// <param name="enableImageTypes">Optional. The image types to include in the output.</param>
    /// <param name="userId">User Id.</param>
    /// <param name="recursive">Search recursively.</param>
    /// <param name="enableImages">Optional. Include image information in output.</param>
    /// <response code="200">Year query returned.</response>
    /// <returns> A <see cref="QueryResult{BaseItemDto}"/> containing the year result.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<QueryResult<BaseItemDto>> GetYears(
        [FromQuery] int? startIndex,
        [FromQuery] int? limit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] SortOrder[] sortOrder,
        [FromQuery] Guid? parentId,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[] fields,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] BaseItemKind[] excludeItemTypes,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] BaseItemKind[] includeItemTypes,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] MediaType[] mediaTypes,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemSortBy[] sortBy,
        [FromQuery] bool? enableUserData,
        [FromQuery] int? imageTypeLimit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ImageType[] enableImageTypes,
        [FromQuery] Guid? userId,
        [FromQuery] bool recursive = true,
        [FromQuery] bool? enableImages = true)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var dtoOptions = new DtoOptions { Fields = fields }
            .AddAdditionalDtoOptions(enableImages, enableUserData, imageTypeLimit, enableImageTypes);

        User? user = userId.IsNullOrEmpty()
            ? null
            : _userManager.GetUserById(userId.Value);
        BaseItem parentItem = _libraryManager.GetParentItem(parentId, userId);

        var query = new InternalItemsQuery(user)
        {
            ExcludeItemTypes = excludeItemTypes,
            IncludeItemTypes = includeItemTypes,
            MediaTypes = mediaTypes,
            DtoOptions = dtoOptions
        };

        bool Filter(BaseItem i) => FilterItem(i, excludeItemTypes, includeItemTypes, mediaTypes);

        IReadOnlyList<BaseItem> items;
        int totalCount = -1;
        if (parentItem.IsFolder)
        {
            var folder = (Folder)parentItem;

            if (userId.IsNullOrEmpty())
            {
                items = recursive ? folder.GetRecursiveChildren(Filter) : folder.Children.Where(Filter).ToArray();
            }
            else
            {
                items = recursive ? folder.GetRecursiveChildren(user, query, out totalCount, _itemSortService) : folder.GetChildren(user, true, null, _itemSortService).Where(Filter).ToArray();
            }
        }
        else
        {
            items = new[] { parentItem }.Where(Filter).ToList();
        }

        var extractedItems = GetAllItems(items);

        var filteredItems = _itemSortService.Sort(extractedItems, user, RequestHelpers.GetOrderBy(sortBy, sortOrder));

        var ibnItemsArray = filteredItems.ToList();

        IEnumerable<BaseItem> ibnItems = ibnItemsArray;

        if (startIndex.HasValue || limit.HasValue)
        {
            if (startIndex.HasValue)
            {
                ibnItems = ibnItems.Skip(startIndex.Value);
            }

            if (limit.HasValue)
            {
                ibnItems = ibnItems.Take(limit.Value);
            }
        }

        var tuples = ibnItems.Select(i => new Tuple<BaseItem, List<BaseItem>>(i, new List<BaseItem>()));

        var dtos = tuples.Select(i => _dtoService.GetItemByNameDto(i.Item1, dtoOptions, i.Item2, user));

        var result = new QueryResult<BaseItemDto>(
            startIndex,
            totalCount == -1 ? ibnItemsArray.Count : totalCount,
            dtos.Where(i => i is not null).ToArray());
        return result;
    }

    /// <summary>
    /// Gets a year.
    /// </summary>
    /// <param name="year">The year.</param>
    /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
    /// <response code="200">Year returned.</response>
    /// <response code="404">Year not found.</response>
    /// <returns>
    /// An <see cref="OkResult"/> containing the year,
    /// or a <see cref="NotFoundResult"/> if year not found.
    /// </returns>
    [HttpGet("{year}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<BaseItemDto> GetYear([FromRoute, Required] int year, [FromQuery] Guid? userId)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var item = _namedItems.Find<Year>(year.ToString(CultureInfo.InvariantCulture));
        if (item is null)
        {
            return NotFound();
        }

        var dtoOptions = new DtoOptions();

        if (!userId.IsNullOrEmpty())
        {
            var user = _userManager.GetUserById(userId.Value);
            return _dtoService.GetBaseItemDto(item, dtoOptions, user);
        }

        return _dtoService.GetBaseItemDto(item, dtoOptions);
    }

    private bool FilterItem(BaseItem f, IReadOnlyCollection<BaseItemKind> excludeItemTypes, IReadOnlyCollection<BaseItemKind> includeItemTypes, IReadOnlyCollection<MediaType> mediaTypes)
    {
        var baseItemKind = f.GetBaseItemKind();
        // Exclude item types
        if (excludeItemTypes.Count > 0 && excludeItemTypes.Contains(baseItemKind))
        {
            return false;
        }

        // Include item types
        if (includeItemTypes.Count > 0 && !includeItemTypes.Contains(baseItemKind))
        {
            return false;
        }

        // Include MediaTypes
        if (mediaTypes.Count > 0 && !mediaTypes.Contains(f.MediaType))
        {
            return false;
        }

        return true;
    }

    private IEnumerable<BaseItem> GetAllItems(IEnumerable<BaseItem> items)
    {
        return items
            .Select(i => i.ProductionYear ?? 0)
            .Where(i => i > 0)
            .Distinct()
            .Select(year => _namedItems.Find<Year>(year.ToString(CultureInfo.InvariantCulture)))
            .OfType<Year>();
    }
}
