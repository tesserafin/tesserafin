using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tesserafin.Api.Extensions;
using Tesserafin.Api.Helpers;
using Tesserafin.Api.ModelBinders;
using Tesserafin.Api.Models.ContentPackDtos;
using Tesserafin.Common.Api;
using Tesserafin.Controller.ContentPacks;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.ContentPacks;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Querying;

namespace Tesserafin.Api.Controllers;

/// <summary>
/// The content packs controller.
/// </summary>
/// <remarks>
/// Reading a pack needs nothing but an authenticated user; every write needs the content pack
/// management permission. Membership grants no access of its own: what a caller sees is decided
/// entirely by the ordinary item query path.
/// </remarks>
[Route("ContentPacks")]
[Authorize]
[Tags("ContentPacks")]
public class ContentPacksController : BaseTesserafinApiController
{
    private readonly IContentPackManager _contentPackManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IDtoService _dtoService;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentPacksController"/> class.
    /// </summary>
    /// <param name="contentPackManager">Instance of the <see cref="IContentPackManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="dtoService">Instance of the <see cref="IDtoService"/> interface.</param>
    public ContentPacksController(
        IContentPackManager contentPackManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IDtoService dtoService)
    {
        _contentPackManager = contentPackManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _dtoService = dtoService;
    }

    /// <summary>
    /// Gets the content packs the current user may see.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Content packs returned.</response>
    /// <returns>The ordered content packs.</returns>
    /// <remarks>
    /// A pack that holds items but none the caller may see is omitted. A pack that is genuinely
    /// empty is returned with a zero count, so an empty pack stays distinguishable from one that
    /// does not exist.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ContentPackDto>>> GetContentPacks(CancellationToken cancellationToken = default)
    {
        var user = _userManager.GetUserById(User.GetUserId());
        var packs = await _contentPackManager.GetPacksAsync(cancellationToken).ConfigureAwait(false);
        var nonEmpty = (await _contentPackManager.GetNonEmptyPackIdsAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();

        var result = new List<ContentPackDto>(packs.Count);
        foreach (var pack in packs)
        {
            var view = GetVisibleView(pack, user);
            if (view.VisibleItemCount == 0 && nonEmpty.Contains(pack.Id))
            {
                // Populated, but nothing in it is visible to this caller: omit it rather than
                // advertise its existence with a count that would have to be a lie.
                continue;
            }

            result.Add(view);
        }

        return Ok((IReadOnlyList<ContentPackDto>)result);
    }

    /// <summary>
    /// Gets one content pack.
    /// </summary>
    /// <param name="packId">The content pack id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Content pack returned.</response>
    /// <response code="404">Content pack not found, or wholly inaccessible to the caller.</response>
    /// <returns>The content pack.</returns>
    [HttpGet("{packId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ContentPackDto>> GetContentPack(
        [FromRoute, Required] Guid packId,
        CancellationToken cancellationToken = default)
    {
        var user = _userManager.GetUserById(User.GetUserId());
        var pack = await _contentPackManager.GetPackAsync(packId, cancellationToken).ConfigureAwait(false);
        if (pack is null)
        {
            return NotFound();
        }

        var view = GetVisibleView(pack, user);
        if (view.VisibleItemCount == 0)
        {
            var nonEmpty = await _contentPackManager.GetNonEmptyPackIdsAsync(cancellationToken).ConfigureAwait(false);
            if (nonEmpty.Contains(packId))
            {
                // Same answer as for a pack that does not exist, so the response cannot be used to
                // learn that inaccessible content is filed here.
                return NotFound();
            }
        }

        return Ok(view);
    }

    /// <summary>
    /// Creates a content pack.
    /// </summary>
    /// <param name="request">The content pack to create.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Content pack created.</response>
    /// <response code="400">The name is missing, empty or too long.</response>
    /// <response code="403">The user does not have permission to manage content packs.</response>
    /// <response code="409">A content pack with that name already exists.</response>
    /// <returns>The created content pack.</returns>
    [HttpPost]
    [Authorize(Policy = Policies.ContentPackManagement)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ContentPackDto>> CreateContentPack(
        [FromBody, Required] CreateContentPackRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var pack = await _contentPackManager
                .CreatePackAsync(request.Name, request.Description, ContentPackOrigin.Manual, cancellationToken)
                .ConfigureAwait(false);

            var user = _userManager.GetUserById(User.GetUserId());
            return Ok(GetVisibleView(pack, user));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (ContentPackNameConflictException ex)
        {
            return Conflict(ex.Message);
        }
    }

    /// <summary>
    /// Updates a content pack's name and description. The identifier never changes.
    /// </summary>
    /// <param name="packId">The content pack id.</param>
    /// <param name="request">The new metadata.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Content pack updated.</response>
    /// <response code="400">The name is missing, empty or too long.</response>
    /// <response code="403">The user does not have permission to manage content packs.</response>
    /// <response code="404">Content pack not found.</response>
    /// <response code="409">Another content pack already has that name.</response>
    /// <returns>The updated content pack.</returns>
    [HttpPost("{packId}")]
    [Authorize(Policy = Policies.ContentPackManagement)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ContentPackDto>> UpdateContentPack(
        [FromRoute, Required] Guid packId,
        [FromBody, Required] UpdateContentPackRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var pack = await _contentPackManager
                .UpdatePackAsync(packId, request.Name, request.Description, cancellationToken)
                .ConfigureAwait(false);

            var user = _userManager.GetUserById(User.GetUserId());
            return Ok(GetVisibleView(pack, user));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (ContentPackNotFoundException)
        {
            return NotFound();
        }
        catch (ContentPackNameConflictException ex)
        {
            return Conflict(ex.Message);
        }
    }

    /// <summary>
    /// Replaces the global content pack ordering.
    /// </summary>
    /// <param name="request">Every content pack id, exactly once, in the wanted order.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Content packs reordered.</response>
    /// <response code="400">The submitted list is not a complete ordering.</response>
    /// <response code="403">The user does not have permission to manage content packs.</response>
    /// <response code="404">One of the content packs does not exist.</response>
    /// <returns>A <see cref="NoContentResult"/> indicating success.</returns>
    [HttpPost("Order")]
    [Authorize(Policy = Policies.ContentPackManagement)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> ReorderContentPacks(
        [FromBody, Required] ReorderContentPacksRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            await _contentPackManager.ReorderPacksAsync(request.PackIds, cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (ContentPackNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Deletes a content pack and its membership links. No media, metadata, artwork, collection or
    /// library is touched.
    /// </summary>
    /// <param name="packId">The content pack id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Content pack and its membership links deleted.</response>
    /// <response code="403">The user does not have permission to manage content packs.</response>
    /// <response code="404">Content pack not found.</response>
    /// <returns>A <see cref="NoContentResult"/> indicating success.</returns>
    [HttpDelete("{packId}")]
    [Authorize(Policy = Policies.ContentPackManagement)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteContentPack(
        [FromRoute, Required] Guid packId,
        CancellationToken cancellationToken = default)
    {
        var deleted = await _contentPackManager.DeletePackAsync(packId, cancellationToken).ConfigureAwait(false);
        return deleted ? NoContent() : NotFound();
    }

    /// <summary>
    /// Adds an item to a content pack.
    /// </summary>
    /// <param name="packId">The content pack id.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="provenance">Why the item is being added. Defaults to <c>Manual</c>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Item is in the content pack.</response>
    /// <response code="400">The provenance is not one of the values this server produces.</response>
    /// <response code="403">The user does not have permission to manage content packs.</response>
    /// <response code="404">Content pack not found, or the item is unknown or invisible to the caller.</response>
    /// <returns>A <see cref="NoContentResult"/> indicating success.</returns>
    /// <remarks>
    /// Idempotent: adding the same item twice leaves one membership. An existing manual membership
    /// is never downgraded by an automated add.
    /// </remarks>
    [HttpPost("{packId}/Items/{itemId}")]
    [Authorize(Policy = Policies.ContentPackManagement)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> AddContentPackItem(
        [FromRoute, Required] Guid packId,
        [FromRoute, Required] Guid itemId,
        [FromQuery] ContentPackMembershipProvenance provenance = ContentPackMembershipProvenance.Manual,
        CancellationToken cancellationToken = default)
    {
        if (provenance != ContentPackMembershipProvenance.Manual
            && provenance != ContentPackMembershipProvenance.SystemSeed)
        {
            return BadRequest("Only Manual and SystemSeed provenance can be written.");
        }

        var user = _userManager.GetUserById(User.GetUserId());

        // The management permission says the caller may curate packs. It says nothing about which
        // items they may see, so visibility is asked of the ordinary item query, exactly as a read
        // would ask it. An unknown item and an invisible one give the same answer on purpose.
        if (!IsItemVisible(itemId, user))
        {
            return NotFound();
        }

        try
        {
            await _contentPackManager.AddItemAsync(packId, itemId, provenance, cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (ContentPackNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Removes an item from a content pack. The item itself is not touched.
    /// </summary>
    /// <param name="packId">The content pack id.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Item is not in the content pack.</response>
    /// <response code="403">The user does not have permission to manage content packs.</response>
    /// <response code="404">Content pack not found.</response>
    /// <returns>A <see cref="NoContentResult"/> indicating success.</returns>
    /// <remarks>
    /// Idempotent: removing an absent membership succeeds.
    /// </remarks>
    [HttpDelete("{packId}/Items/{itemId}")]
    [Authorize(Policy = Policies.ContentPackManagement)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> RemoveContentPackItem(
        [FromRoute, Required] Guid packId,
        [FromRoute, Required] Guid itemId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _contentPackManager.RemoveItemAsync(packId, itemId, cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (ContentPackNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Gets the items in a content pack that the current user may see.
    /// </summary>
    /// <param name="packId">The content pack id.</param>
    /// <param name="startIndex">Optional. The record index to start at.</param>
    /// <param name="limit">Optional. The maximum number of records to return.</param>
    /// <param name="searchTerm">Optional. Filter by search term.</param>
    /// <param name="sortBy">Optional. Specify one or more sort orders, comma delimited.</param>
    /// <param name="sortOrder">Optional. Sort order, ascending or descending.</param>
    /// <param name="includeItemTypes">Optional. Filter by item kind, comma delimited.</param>
    /// <param name="fields">Optional. The fields to return.</param>
    /// <param name="enableImages">Optional. Include image information in the output.</param>
    /// <param name="imageTypeLimit">Optional. The max number of images to return per image type.</param>
    /// <param name="enableImageTypes">Optional. The image types to include in the output.</param>
    /// <param name="enableUserData">Optional. Include user data.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Items returned. An empty page is a normal answer.</response>
    /// <response code="404">Content pack not found.</response>
    /// <returns>The items in the content pack the caller may see.</returns>
    /// <remarks>
    /// Expressed as a filter on the existing item query, so paging, sorting and every other item
    /// filter keep their usual meaning and there is exactly one authorization implementation.
    /// </remarks>
    [HttpGet("{packId}/Items")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<QueryResult<BaseItemDto>>> GetContentPackItems(
        [FromRoute, Required] Guid packId,
        [FromQuery] int? startIndex,
        [FromQuery] int? limit,
        [FromQuery] string? searchTerm,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemSortBy[] sortBy,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] SortOrder[] sortOrder,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] BaseItemKind[] includeItemTypes,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[] fields,
        [FromQuery] bool? enableImages,
        [FromQuery] int? imageTypeLimit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ImageType[] enableImageTypes,
        [FromQuery] bool? enableUserData,
        CancellationToken cancellationToken = default)
    {
        var pack = await _contentPackManager.GetPackAsync(packId, cancellationToken).ConfigureAwait(false);
        if (pack is null)
        {
            return NotFound();
        }

        var user = _userManager.GetUserById(User.GetUserId());
        var dtoOptions = new DtoOptions { Fields = fields }
            .AddAdditionalDtoOptions(enableImages, enableUserData, imageTypeLimit, enableImageTypes);

        var query = new InternalItemsQuery(user)
        {
            ContentPackId = packId,
            StartIndex = startIndex,
            Limit = limit,
            SearchTerm = searchTerm,
            OrderBy = RequestHelpers.GetOrderBy(sortBy, sortOrder),
            IncludeItemTypes = includeItemTypes,
            DtoOptions = dtoOptions,
            Recursive = true
        };

        var result = _libraryManager.GetItemsResult(query);

        return Ok(new QueryResult<BaseItemDto>
        {
            Items = _dtoService.GetBaseItemDtos(result.Items, dtoOptions, user),
            TotalRecordCount = result.TotalRecordCount,
            StartIndex = startIndex ?? 0
        });
    }

    /// <summary>
    /// Gets the content packs that contain an item.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Content packs returned. An empty list is a normal answer.</response>
    /// <response code="404">The item is unknown or invisible to the caller.</response>
    /// <returns>The ordered content packs containing the item.</returns>
    [HttpGet("/Items/{itemId}/ContentPacks")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<ContentPackDto>>> GetContentPacksForItem(
        [FromRoute, Required] Guid itemId,
        CancellationToken cancellationToken = default)
    {
        var user = _userManager.GetUserById(User.GetUserId());

        // An unknown item and an invisible one answer identically, so the response never says
        // which of the two happened.
        if (!IsItemVisible(itemId, user))
        {
            return NotFound();
        }

        var packs = await _contentPackManager.GetPacksForItemAsync(itemId, cancellationToken).ConfigureAwait(false);

        // Each pack here already holds one item this caller can see, so each has a truthful,
        // non-zero visible view.
        return Ok((IReadOnlyList<ContentPackDto>)packs.Select(pack => GetVisibleView(pack, user)).ToList());
    }

    private bool IsItemVisible(Guid itemId, User? user)
    {
        var query = new InternalItemsQuery(user)
        {
            Recursive = true,
            EnableTotalRecordCount = true,
            DtoOptions = new DtoOptions(false)
        };

        if (user is not null)
        {
            // Resolve the caller's accessible libraries into the query *before* narrowing it to
            // one item. A query that already carries ItemIds is treated as scoped and skips that
            // resolution, which would silently drop library and folder restriction.
            _libraryManager.ConfigureUserAccess(query, user);
        }

        query.ItemIds = [itemId];

        return _libraryManager.GetCount(query) > 0;
    }

    private ContentPackDto GetVisibleView(ContentPack pack, User? user)
    {
        // One bounded query per pack: the total record count and a single representative item come
        // back together, and both are computed from what this caller may see.
        var visible = _libraryManager.GetItemsResult(new InternalItemsQuery(user)
        {
            ContentPackId = pack.Id,
            Limit = 1,
            Recursive = true,
            EnableTotalRecordCount = true,
            DtoOptions = new DtoOptions(false)
        });

        return new ContentPackDto
        {
            Id = pack.Id,
            Name = pack.Name,
            Description = pack.Description,
            SortOrder = pack.SortOrder,
            Origin = pack.Origin,
            DateCreated = pack.DateCreated,
            VisibleItemCount = visible.TotalRecordCount,
            RepresentativeItemId = visible.Items.Count > 0 ? visible.Items[0].Id : null
        };
    }
}
