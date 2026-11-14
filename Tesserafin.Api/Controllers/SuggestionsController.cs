using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tesserafin.Api.Extensions;
using Tesserafin.Api.Helpers;
using Tesserafin.Api.ModelBinders;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Querying;

namespace Tesserafin.Api.Controllers;

/// <summary>
/// The suggestions controller.
/// </summary>
[Route("")]
[Authorize]
[Tags("Suggestion")]
public class SuggestionsController : BaseTesserafinApiController
{
    private readonly IDtoService _dtoService;
    private readonly IUserManager _userManager;
    private readonly IItemQueryService _itemQueryService;

    /// <summary>
    /// Initializes a new instance of the <see cref="SuggestionsController"/> class.
    /// </summary>
    /// <param name="dtoService">Instance of the <see cref="IDtoService"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="itemQueryService">Instance of the <see cref="IItemQueryService"/> interface.</param>
    public SuggestionsController(
        IDtoService dtoService,
        IUserManager userManager,
        IItemQueryService itemQueryService)
    {
        _dtoService = dtoService;
        _userManager = userManager;
        _itemQueryService = itemQueryService;
    }

    /// <summary>
    /// Gets suggestions.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="mediaType">The media types.</param>
    /// <param name="type">The type.</param>
    /// <param name="startIndex">Optional. The start index.</param>
    /// <param name="limit">Optional. The limit.</param>
    /// <param name="enableTotalRecordCount">Whether to enable the total record count.</param>
    /// <response code="200">Suggestions returned.</response>
    /// <returns>A <see cref="QueryResult{BaseItemDto}"/> with the suggestions.</returns>
    [HttpGet("Items/Suggestions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<QueryResult<BaseItemDto>> GetSuggestions(
        [FromQuery] Guid? userId,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] MediaType[] mediaType,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] BaseItemKind[] type,
        [FromQuery] int? startIndex,
        [FromQuery] int? limit,
        [FromQuery] bool enableTotalRecordCount = false)
    {
        User? user;
        if (userId.IsNullOrEmpty())
        {
            user = null;
        }
        else
        {
            var requestUserId = RequestHelpers.GetUserId(User, userId);
            user = _userManager.GetUserById(requestUserId);
        }

        var dtoOptions = new DtoOptions();
        var result = _itemQueryService.GetItems(new InternalItemsQuery(user)
        {
            OrderBy = new[] { (ItemSortBy.Random, SortOrder.Descending) },
            MediaTypes = mediaType,
            IncludeItemTypes = type,
            IsVirtualItem = false,
            StartIndex = startIndex,
            Limit = limit,
            DtoOptions = dtoOptions,
            EnableTotalRecordCount = enableTotalRecordCount,
            Recursive = true
        });

        var dtoList = _dtoService.GetBaseItemDtos(result.Items, dtoOptions, user);

        return new QueryResult<BaseItemDto>(
            startIndex,
            result.TotalRecordCount,
            dtoList);
    }

    /// <summary>
    /// Gets suggestions.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="mediaType">The media types.</param>
    /// <param name="type">The type.</param>
    /// <param name="startIndex">Optional. The start index.</param>
    /// <param name="limit">Optional. The limit.</param>
    /// <param name="enableTotalRecordCount">Whether to enable the total record count.</param>
    /// <response code="200">Suggestions returned.</response>
    /// <returns>A <see cref="QueryResult{BaseItemDto}"/> with the suggestions.</returns>
    [HttpGet("Users/{userId}/Suggestions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Obsolete("Kept for backwards compatibility")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult<QueryResult<BaseItemDto>> GetSuggestionsLegacy(
        [FromRoute, Required] Guid userId,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] MediaType[] mediaType,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] BaseItemKind[] type,
        [FromQuery] int? startIndex,
        [FromQuery] int? limit,
        [FromQuery] bool enableTotalRecordCount = false)
        => GetSuggestions(userId, mediaType, type, startIndex, limit, enableTotalRecordCount);
}
