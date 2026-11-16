using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tesserafin.Api.Extensions;
using Tesserafin.Api.Helpers;
using Tesserafin.Api.ModelBinders;
using Tesserafin.Api.Models.UserViewDtos;
using Tesserafin.Common.Extensions;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Sorting;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Library;
using Tesserafin.Model.Querying;

namespace Tesserafin.Api.Controllers;

/// <summary>
/// User views controller.
/// </summary>
[Route("")]
[Authorize]
[Tags("UserView")]
public class UserViewsController : BaseTesserafinApiController
{
    private readonly IStandaloneVisibilityService _visibility;
    private readonly IUserManager _userManager;
    private readonly IUserViewManager _userViewManager;
    private readonly IDtoService _dtoService;
    private readonly ILibraryManager _libraryManager;
    private readonly IItemSortService _itemSortService;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserViewsController"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userViewManager">Instance of the <see cref="IUserViewManager"/> interface.</param>
    /// <param name="dtoService">Instance of the <see cref="IDtoService"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="itemSortService">Instance of the <see cref="IItemSortService"/> interface.</param>
    /// <param name="visibility">Decides item visibility through the services, never the entity statics.</param>
    public UserViewsController(
        IUserManager userManager,
        IUserViewManager userViewManager,
        IDtoService dtoService,
        ILibraryManager libraryManager,
        IItemSortService itemSortService,
        IStandaloneVisibilityService visibility)
    {
        _visibility = visibility;
        _userManager = userManager;
        _userViewManager = userViewManager;
        _dtoService = dtoService;
        _libraryManager = libraryManager;
        _itemSortService = itemSortService;
    }

    /// <summary>
    /// Get user views.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="includeExternalContent">Whether or not to include external views such as channels or live tv.</param>
    /// <param name="presetViews">Preset views.</param>
    /// <param name="includeHidden">Whether or not to include hidden content.</param>
    /// <response code="200">User views returned.</response>
    /// <returns>An <see cref="OkResult"/> containing the user views.</returns>
    [HttpGet("UserViews")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public QueryResult<BaseItemDto> GetUserViews(
        [FromQuery] Guid? userId,
        [FromQuery] bool? includeExternalContent,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] CollectionType?[] presetViews,
        [FromQuery] bool includeHidden = false)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = _userManager.GetUserById(userId.Value) ?? throw new ResourceNotFoundException();

        var query = new UserViewQuery { User = user, IncludeHidden = includeHidden };

        if (includeExternalContent.HasValue)
        {
            query.IncludeExternalContent = includeExternalContent.Value;
        }

        if (presetViews.Length != 0)
        {
            query.PresetViews = presetViews;
        }

        var folders = _userViewManager.GetUserViews(query);

        var dtoOptions = new DtoOptions();
        dtoOptions.Fields = [.. dtoOptions.Fields, ItemFields.PrimaryImageAspectRatio, ItemFields.DisplayPreferencesId];

        var dtos = Array.ConvertAll(folders, i => _dtoService.GetBaseItemDto(i, dtoOptions, user));

        return new QueryResult<BaseItemDto>(dtos);
    }

    /// <summary>
    /// Get user views.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="includeExternalContent">Whether or not to include external views such as channels or live tv.</param>
    /// <param name="presetViews">Preset views.</param>
    /// <param name="includeHidden">Whether or not to include hidden content.</param>
    /// <response code="200">User views returned.</response>
    /// <returns>An <see cref="OkResult"/> containing the user views.</returns>
    [HttpGet("Users/{userId}/Views")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Obsolete("Kept for backwards compatibility")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public QueryResult<BaseItemDto> GetUserViewsLegacy(
        [FromRoute, Required] Guid userId,
        [FromQuery] bool? includeExternalContent,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] CollectionType?[] presetViews,
        [FromQuery] bool includeHidden = false)
        => GetUserViews(userId, includeExternalContent, presetViews, includeHidden);

    /// <summary>
    /// Get user view grouping options.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <response code="200">User view grouping options returned.</response>
    /// <response code="404">User not found.</response>
    /// <returns>
    /// An <see cref="OkResult"/> containing the user view grouping options
    /// or a <see cref="NotFoundResult"/> if user not found.
    /// </returns>
    [HttpGet("UserViews/GroupingOptions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IEnumerable<SpecialViewOptionDto>> GetGroupingOptions([FromQuery] Guid? userId)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = _userManager.GetUserById(userId.Value);
        if (user is null)
        {
            return NotFound();
        }

        return Ok(_libraryManager.GetUserRootFolder()
            .GetChildren(user, true, new InternalItemsQuery(user) { Visibility = _visibility }, _itemSortService)
            .OfType<Folder>()
            .Where(UserView.IsEligibleForGrouping)
            .Select(i => new SpecialViewOptionDto
            {
                Name = i.Name,
                Id = i.Id.ToString("N", CultureInfo.InvariantCulture)
            })
            .OrderBy(i => i.Name)
            .AsEnumerable());
    }

    /// <summary>
    /// Get user view grouping options.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <response code="200">User view grouping options returned.</response>
    /// <response code="404">User not found.</response>
    /// <returns>
    /// An <see cref="OkResult"/> containing the user view grouping options
    /// or a <see cref="NotFoundResult"/> if user not found.
    /// </returns>
    [HttpGet("Users/{userId}/GroupingOptions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Obsolete("Kept for backwards compatibility")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult<IEnumerable<SpecialViewOptionDto>> GetGroupingOptionsLegacy(
        [FromRoute, Required] Guid userId)
        => GetGroupingOptions(userId);
}
