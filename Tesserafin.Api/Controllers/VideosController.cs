using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tesserafin.Api.Attributes;
using Tesserafin.Api.Auth.HlsJobOwnership;
using Tesserafin.Api.Extensions;
using Tesserafin.Api.Helpers;
using Tesserafin.Api.ModelBinders;
using Tesserafin.Common.Api;
using Tesserafin.Common.Configuration;
using Tesserafin.Common.Net;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Controller.Streaming;
using Tesserafin.Extensions;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.MediaInfo;
using Tesserafin.Model.Net;
using Tesserafin.Model.Querying;

namespace Tesserafin.Api.Controllers;

/// <summary>
/// The videos controller.
/// </summary>
[Tags("Video")]
public class VideosController : BaseTesserafinApiController
{
    private readonly IItemWriteCommands _writes;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IDtoService _dtoService;
    private readonly IMediaSourceManager _mediaSourceManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideosController"/> class.
    /// </summary>
    /// <param name="writes">Explicit item write/resolution ownership.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="dtoService">Instance of the <see cref="IDtoService"/> interface.</param>
    /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
    public VideosController(
        IItemWriteCommands writes,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IDtoService dtoService,
        IMediaSourceManager mediaSourceManager)
    {
        _writes = writes;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _dtoService = dtoService;
        _mediaSourceManager = mediaSourceManager;
    }

    /// <summary>
    /// Gets additional parts for a video.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
    /// <response code="200">Additional parts returned.</response>
    /// <returns>A <see cref="QueryResult{BaseItemDto}"/> with the parts.</returns>
    [HttpGet("{itemId}/AdditionalParts")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<QueryResult<BaseItemDto>> GetAdditionalPart([FromRoute, Required] Guid itemId, [FromQuery] Guid? userId)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = userId.IsNullOrEmpty()
            ? null
            : _userManager.GetUserById(userId.Value);

        var item = itemId.IsEmpty()
            ? (userId.IsNullOrEmpty()
                ? _libraryManager.RootFolder
                : _libraryManager.GetUserRootFolder())
            : _libraryManager.GetItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return NotFound();
        }

        var dtoOptions = new DtoOptions();

        BaseItemDto[] items;
        if (item is Video video)
        {
            items = video.AdditionalParts
                .Select(path => _libraryManager.GetNewItemId(path, typeof(Video)))
                .Select(id => _libraryManager.GetItemById<Video>(id, user))
                .OfType<Video>()
                .OrderBy(part => part.SortName)
                .Select(part => _dtoService.GetBaseItemDto(part, dtoOptions, user, video))
                .ToArray();
        }
        else
        {
            items = Array.Empty<BaseItemDto>();
        }

        var result = new QueryResult<BaseItemDto>(items);
        return result;
    }

    /// <summary>
    /// Removes alternate video sources.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <response code="204">Alternate sources deleted.</response>
    /// <response code="404">Video not found.</response>
    /// <returns>A <see cref="NoContentResult"/> indicating success, or a <see cref="NotFoundResult"/> if the video doesn't exist.</returns>
    [HttpDelete("{itemId}/AlternateSources")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteAlternateSources([FromRoute, Required] Guid itemId)
    {
        var item = _libraryManager.GetItemById<Video>(itemId, User.GetUserId());
        if (item is null)
        {
            return NotFound();
        }

        if (item.LinkedAlternateVersions.Length == 0 && item.PrimaryVersionId.HasValue)
        {
            item = _libraryManager.GetItemById<Video>(item.PrimaryVersionId.Value);
        }

        if (item is null)
        {
            return NotFound();
        }

        foreach (var link in _libraryManager.GetLinkedAlternateVersions(item))
        {
            link.SetPrimaryVersionId(null);
            link.LinkedAlternateVersions = Array.Empty<LinkedChild>();

            await link.UpdateToRepositoryAsync(_writes, ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
        }

        item.LinkedAlternateVersions = Array.Empty<LinkedChild>();
        item.SetPrimaryVersionId(null);
        await item.UpdateToRepositoryAsync(_writes, ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Merges videos into a single record.
    /// </summary>
    /// <param name="ids">Item id list. This allows multiple, comma delimited.</param>
    /// <response code="204">Videos merged.</response>
    /// <response code="400">Supply at least 2 video ids.</response>
    /// <returns>A <see cref="NoContentResult"/> indicating success, or a <see cref="BadRequestResult"/> if less than two ids were supplied.</returns>
    [HttpPost("MergeVersions")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> MergeVersions([FromQuery, Required, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] Guid[] ids)
    {
        var userId = User.GetUserId();
        var items = ids
            .Select(i => _libraryManager.GetItemById<BaseItem>(i, userId))
            .OfType<Video>()
            .OrderBy(i => i.Id)
            .ToList();

        if (items.Count < 2)
        {
            return BadRequest("Please supply at least two videos to merge.");
        }

        var primaryVersion = items.FirstOrDefault(i => i.MediaSourceCount > 1 && !i.PrimaryVersionId.HasValue);
        if (primaryVersion is null)
        {
            primaryVersion = items
                .OrderBy(i =>
                {
                    if (i.Video3DFormat.HasValue || i.VideoType != VideoType.VideoFile)
                    {
                        return 1;
                    }

                    return 0;
                })
                .ThenByDescending(i => i.GetDefaultVideoStream(_mediaSourceManager)?.Width ?? 0)
                .First();
        }

        var alternateVersionsOfPrimary = primaryVersion.LinkedAlternateVersions.ToList();

        foreach (var item in items.Where(i => !i.Id.Equals(primaryVersion.Id)))
        {
            item.SetPrimaryVersionId(primaryVersion.Id);

            await item.UpdateToRepositoryAsync(_writes, ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);

            // Re-route any playlist/collection references from this item to the primary
            await _writes.RerouteLinkedChildReferencesAsync(item.Id, primaryVersion.Id, HttpContext.RequestAborted).ConfigureAwait(false);

            if (!alternateVersionsOfPrimary.Any(i => i.ItemId.HasValue && i.ItemId.Value.Equals(item.Id)))
            {
                alternateVersionsOfPrimary.Add(new LinkedChild
                {
                    ItemId = item.Id,
                    Type = LinkedChildType.LinkedAlternateVersion
                });
            }

            foreach (var linkedItem in item.LinkedAlternateVersions)
            {
                if (linkedItem.ItemId.HasValue && !alternateVersionsOfPrimary.Any(i => i.ItemId.HasValue && i.ItemId.Value.Equals(linkedItem.ItemId.Value)))
                {
                    alternateVersionsOfPrimary.Add(linkedItem);
                }
            }

            if (item.LinkedAlternateVersions.Length > 0)
            {
                item.LinkedAlternateVersions = Array.Empty<LinkedChild>();
                await item.UpdateToRepositoryAsync(_writes, ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
            }
        }

        primaryVersion.LinkedAlternateVersions = alternateVersionsOfPrimary.ToArray();
        await primaryVersion.UpdateToRepositoryAsync(_writes, ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
        return NoContent();
    }
}
