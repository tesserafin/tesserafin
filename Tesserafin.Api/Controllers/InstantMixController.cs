using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
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
using Tesserafin.Controller.Playlists;
using Tesserafin.Data.Entities;
using Tesserafin.Extensions;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Querying;

namespace Tesserafin.Api.Controllers;

/// <summary>
/// The instant mix controller.
/// </summary>
[Route("")]
[Authorize]
public class InstantMixController : BaseTesserafinApiController
{
    private readonly IUserManager _userManager;
    private readonly IDtoService _dtoService;
    private readonly IItemLookupService _itemLookupService;
    private readonly IItemAccessService _itemAccessService;
    private readonly IMusicManager _musicManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="InstantMixController"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="dtoService">Instance of the <see cref="IDtoService"/> interface.</param>
    /// <param name="musicManager">Instance of the <see cref="IMusicManager"/> interface.</param>
    /// <param name="itemLookupService">Instance of the <see cref="IItemLookupService"/> interface.</param>
    /// <param name="itemAccessService">Instance of the <see cref="IItemAccessService"/> interface.</param>
    public InstantMixController(
        IUserManager userManager,
        IDtoService dtoService,
        IMusicManager musicManager,
        IItemLookupService itemLookupService,
        IItemAccessService itemAccessService)
    {
        _userManager = userManager;
        _dtoService = dtoService;
        _musicManager = musicManager;
        _itemLookupService = itemLookupService;
        _itemAccessService = itemAccessService;
    }

    /// <summary>
    /// Creates an instant playlist based on a given song.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
    /// <param name="limit">Optional. The maximum number of records to return.</param>
    /// <param name="fields">Optional. Specify additional fields of information to return in the output.</param>
    /// <param name="enableImages">Optional. Include image information in output.</param>
    /// <param name="enableUserData">Optional. Include user data.</param>
    /// <param name="imageTypeLimit">Optional. The max number of images to return, per image type.</param>
    /// <param name="enableImageTypes">Optional. The image types to include in the output.</param>
    /// <response code="200">Instant playlist returned.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>A <see cref="QueryResult{BaseItemDto}"/> with the playlist items.</returns>
    [HttpGet("Songs/{itemId}/InstantMix")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<QueryResult<BaseItemDto>> GetInstantMixFromSong(
        [FromRoute, Required] Guid itemId,
        [FromQuery] Guid? userId,
        [FromQuery] int? limit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[] fields,
        [FromQuery] bool? enableImages,
        [FromQuery] bool? enableUserData,
        [FromQuery] int? imageTypeLimit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ImageType[] enableImageTypes)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = userId.IsNullOrEmpty()
            ? null
            : _userManager.GetUserById(userId.Value);
        var item = user is null
            ? _itemLookupService.GetItemById<BaseItem>(itemId)
            : _itemAccessService.GetVisibleItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return NotFound();
        }

        var dtoOptions = new DtoOptions { Fields = fields }
            .AddAdditionalDtoOptions(enableImages, enableUserData, imageTypeLimit, enableImageTypes);
        var items = _musicManager.GetInstantMixFromItem(item, user, dtoOptions);
        return GetResult(items, user, limit, dtoOptions);
    }

    /// <summary>
    /// Creates an instant playlist based on a given album.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
    /// <param name="limit">Optional. The maximum number of records to return.</param>
    /// <param name="fields">Optional. Specify additional fields of information to return in the output.</param>
    /// <param name="enableImages">Optional. Include image information in output.</param>
    /// <param name="enableUserData">Optional. Include user data.</param>
    /// <param name="imageTypeLimit">Optional. The max number of images to return, per image type.</param>
    /// <param name="enableImageTypes">Optional. The image types to include in the output.</param>
    /// <response code="200">Instant playlist returned.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>A <see cref="QueryResult{BaseItemDto}"/> with the playlist items.</returns>
    [HttpGet("Albums/{itemId}/InstantMix")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<QueryResult<BaseItemDto>> GetInstantMixFromAlbum(
        [FromRoute, Required] Guid itemId,
        [FromQuery] Guid? userId,
        [FromQuery] int? limit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[] fields,
        [FromQuery] bool? enableImages,
        [FromQuery] bool? enableUserData,
        [FromQuery] int? imageTypeLimit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ImageType[] enableImageTypes)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = userId.IsNullOrEmpty()
            ? null
            : _userManager.GetUserById(userId.Value);
        var item = user is null
            ? _itemLookupService.GetItemById<BaseItem>(itemId)
            : _itemAccessService.GetVisibleItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return NotFound();
        }

        var dtoOptions = new DtoOptions { Fields = fields }
            .AddAdditionalDtoOptions(enableImages, enableUserData, imageTypeLimit, enableImageTypes);
        var items = _musicManager.GetInstantMixFromItem(item, user, dtoOptions);
        return GetResult(items, user, limit, dtoOptions);
    }

    /// <summary>
    /// Creates an instant playlist based on a given playlist.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
    /// <param name="limit">Optional. The maximum number of records to return.</param>
    /// <param name="fields">Optional. Specify additional fields of information to return in the output.</param>
    /// <param name="enableImages">Optional. Include image information in output.</param>
    /// <param name="enableUserData">Optional. Include user data.</param>
    /// <param name="imageTypeLimit">Optional. The max number of images to return, per image type.</param>
    /// <param name="enableImageTypes">Optional. The image types to include in the output.</param>
    /// <response code="200">Instant playlist returned.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>A <see cref="QueryResult{BaseItemDto}"/> with the playlist items.</returns>
    [HttpGet("Playlists/{itemId}/InstantMix")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<QueryResult<BaseItemDto>> GetInstantMixFromPlaylist(
        [FromRoute, Required] Guid itemId,
        [FromQuery] Guid? userId,
        [FromQuery] int? limit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[] fields,
        [FromQuery] bool? enableImages,
        [FromQuery] bool? enableUserData,
        [FromQuery] int? imageTypeLimit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ImageType[] enableImageTypes)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = userId.IsNullOrEmpty()
            ? null
            : _userManager.GetUserById(userId.Value);
        var item = user is null
            ? _itemLookupService.GetItemById<Playlist>(itemId)
            : _itemAccessService.GetVisibleItemById<Playlist>(itemId, user);
        if (item is null)
        {
            return NotFound();
        }

        var dtoOptions = new DtoOptions { Fields = fields }
            .AddAdditionalDtoOptions(enableImages, enableUserData, imageTypeLimit, enableImageTypes);
        var items = _musicManager.GetInstantMixFromItem(item, user, dtoOptions);
        return GetResult(items, user, limit, dtoOptions);
    }

    /// <summary>
    /// Creates an instant playlist based on a given genre.
    /// </summary>
    /// <param name="name">The genre name.</param>
    /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
    /// <param name="limit">Optional. The maximum number of records to return.</param>
    /// <param name="fields">Optional. Specify additional fields of information to return in the output.</param>
    /// <param name="enableImages">Optional. Include image information in output.</param>
    /// <param name="enableUserData">Optional. Include user data.</param>
    /// <param name="imageTypeLimit">Optional. The max number of images to return, per image type.</param>
    /// <param name="enableImageTypes">Optional. The image types to include in the output.</param>
    /// <response code="200">Instant playlist returned.</response>
    /// <returns>A <see cref="QueryResult{BaseItemDto}"/> with the playlist items.</returns>
    [HttpGet("MusicGenres/{name}/InstantMix")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Obsolete("Use GetInstantMixFromItem")]
    public ActionResult<QueryResult<BaseItemDto>> GetInstantMixFromMusicGenreByName(
        [FromRoute, Required] string name,
        [FromQuery] Guid? userId,
        [FromQuery] int? limit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[] fields,
        [FromQuery] bool? enableImages,
        [FromQuery] bool? enableUserData,
        [FromQuery] int? imageTypeLimit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ImageType[] enableImageTypes)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = userId.IsNullOrEmpty()
            ? null
            : _userManager.GetUserById(userId.Value);
        var dtoOptions = new DtoOptions { Fields = fields }
            .AddAdditionalDtoOptions(enableImages, enableUserData, imageTypeLimit, enableImageTypes);
        var items = _musicManager.GetInstantMixFromGenres(new[] { name }, user, dtoOptions);
        return GetResult(items, user, limit, dtoOptions);
    }

    /// <summary>
    /// Creates an instant playlist based on a given artist.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
    /// <param name="limit">Optional. The maximum number of records to return.</param>
    /// <param name="fields">Optional. Specify additional fields of information to return in the output.</param>
    /// <param name="enableImages">Optional. Include image information in output.</param>
    /// <param name="enableUserData">Optional. Include user data.</param>
    /// <param name="imageTypeLimit">Optional. The max number of images to return, per image type.</param>
    /// <param name="enableImageTypes">Optional. The image types to include in the output.</param>
    /// <response code="200">Instant playlist returned.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>A <see cref="QueryResult{BaseItemDto}"/> with the playlist items.</returns>
    [HttpGet("Artists/{itemId}/InstantMix")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<QueryResult<BaseItemDto>> GetInstantMixFromArtists(
        [FromRoute, Required] Guid itemId,
        [FromQuery] Guid? userId,
        [FromQuery] int? limit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[] fields,
        [FromQuery] bool? enableImages,
        [FromQuery] bool? enableUserData,
        [FromQuery] int? imageTypeLimit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ImageType[] enableImageTypes)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = userId.IsNullOrEmpty()
            ? null
            : _userManager.GetUserById(userId.Value);
        var item = user is null
            ? _itemLookupService.GetItemById<BaseItem>(itemId)
            : _itemAccessService.GetVisibleItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return NotFound();
        }

        var dtoOptions = new DtoOptions { Fields = fields }
            .AddAdditionalDtoOptions(enableImages, enableUserData, imageTypeLimit, enableImageTypes);
        var items = _musicManager.GetInstantMixFromItem(item, user, dtoOptions);
        return GetResult(items, user, limit, dtoOptions);
    }

    /// <summary>
    /// Creates an instant playlist based on a given item.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
    /// <param name="limit">Optional. The maximum number of records to return.</param>
    /// <param name="fields">Optional. Specify additional fields of information to return in the output.</param>
    /// <param name="enableImages">Optional. Include image information in output.</param>
    /// <param name="enableUserData">Optional. Include user data.</param>
    /// <param name="imageTypeLimit">Optional. The max number of images to return, per image type.</param>
    /// <param name="enableImageTypes">Optional. The image types to include in the output.</param>
    /// <response code="200">Instant playlist returned.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>A <see cref="QueryResult{BaseItemDto}"/> with the playlist items.</returns>
    [HttpGet("Items/{itemId}/InstantMix")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<QueryResult<BaseItemDto>> GetInstantMixFromItem(
        [FromRoute, Required] Guid itemId,
        [FromQuery] Guid? userId,
        [FromQuery] int? limit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[] fields,
        [FromQuery] bool? enableImages,
        [FromQuery] bool? enableUserData,
        [FromQuery] int? imageTypeLimit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ImageType[] enableImageTypes)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = userId.IsNullOrEmpty()
            ? null
            : _userManager.GetUserById(userId.Value);
        var item = user is null
            ? _itemLookupService.GetItemById<BaseItem>(itemId)
            : _itemAccessService.GetVisibleItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return NotFound();
        }

        var dtoOptions = new DtoOptions { Fields = fields }
            .AddAdditionalDtoOptions(enableImages, enableUserData, imageTypeLimit, enableImageTypes);
        var items = _musicManager.GetInstantMixFromItem(item, user, dtoOptions);
        return GetResult(items, user, limit, dtoOptions);
    }

    /// <summary>
    /// Creates an instant playlist based on a given artist.
    /// </summary>
    /// <param name="id">The item id.</param>
    /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
    /// <param name="limit">Optional. The maximum number of records to return.</param>
    /// <param name="fields">Optional. Specify additional fields of information to return in the output.</param>
    /// <param name="enableImages">Optional. Include image information in output.</param>
    /// <param name="enableUserData">Optional. Include user data.</param>
    /// <param name="imageTypeLimit">Optional. The max number of images to return, per image type.</param>
    /// <param name="enableImageTypes">Optional. The image types to include in the output.</param>
    /// <response code="200">Instant playlist returned.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>A <see cref="QueryResult{BaseItemDto}"/> with the playlist items.</returns>
    [HttpGet("Artists/InstantMix")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Obsolete("Use GetInstantMixFromArtists")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult<QueryResult<BaseItemDto>> GetInstantMixFromArtists2(
        [FromQuery, Required] Guid id,
        [FromQuery] Guid? userId,
        [FromQuery] int? limit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[] fields,
        [FromQuery] bool? enableImages,
        [FromQuery] bool? enableUserData,
        [FromQuery] int? imageTypeLimit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ImageType[] enableImageTypes)
    {
        return GetInstantMixFromArtists(
            id,
            userId,
            limit,
            fields,
            enableImages,
            enableUserData,
            imageTypeLimit,
            enableImageTypes);
    }

    /// <summary>
    /// Creates an instant playlist based on a given genre.
    /// </summary>
    /// <param name="id">The item id.</param>
    /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
    /// <param name="limit">Optional. The maximum number of records to return.</param>
    /// <param name="fields">Optional. Specify additional fields of information to return in the output.</param>
    /// <param name="enableImages">Optional. Include image information in output.</param>
    /// <param name="enableUserData">Optional. Include user data.</param>
    /// <param name="imageTypeLimit">Optional. The max number of images to return, per image type.</param>
    /// <param name="enableImageTypes">Optional. The image types to include in the output.</param>
    /// <response code="200">Instant playlist returned.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>A <see cref="QueryResult{BaseItemDto}"/> with the playlist items.</returns>
    [HttpGet("MusicGenres/InstantMix")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Obsolete("Use GetInstantMixFromItem")]
    public ActionResult<QueryResult<BaseItemDto>> GetInstantMixFromMusicGenreById(
        [FromQuery, Required] Guid id,
        [FromQuery] Guid? userId,
        [FromQuery] int? limit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[] fields,
        [FromQuery] bool? enableImages,
        [FromQuery] bool? enableUserData,
        [FromQuery] int? imageTypeLimit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ImageType[] enableImageTypes)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = userId.IsNullOrEmpty()
            ? null
            : _userManager.GetUserById(userId.Value);
        var item = user is null
            ? _itemLookupService.GetItemById<BaseItem>(id)
            : _itemAccessService.GetVisibleItemById<BaseItem>(id, user);
        if (item is null)
        {
            return NotFound();
        }

        var dtoOptions = new DtoOptions { Fields = fields }
            .AddAdditionalDtoOptions(enableImages, enableUserData, imageTypeLimit, enableImageTypes);
        var items = _musicManager.GetInstantMixFromItem(item, user, dtoOptions);
        return GetResult(items, user, limit, dtoOptions);
    }

    private QueryResult<BaseItemDto> GetResult(IReadOnlyList<BaseItem> items, User? user, int? limit, DtoOptions dtoOptions)
    {
        var totalCount = items.Count;

        if (limit.HasValue && limit < items.Count)
        {
            items = items.Take(limit.Value).ToArray();
        }

        var result = new QueryResult<BaseItemDto>(
            0,
            totalCount,
            _dtoService.GetBaseItemDtos(items, dtoOptions, user));

        return result;
    }
}
