using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tesserafin.Api.Auth.FirstTimeSetupPolicy;
using Tesserafin.Api.ModelBinders;
using Tesserafin.Api.Models.LibraryStructureDto;
using Tesserafin.Common.Api;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;

namespace Tesserafin.Api.Controllers;

/// <summary>
/// The library structure controller.
/// </summary>
[Route("Library/VirtualFolders")]
[Authorize(Policy = Policies.FirstTimeSetupOrElevated)]
[FirstTimeSetupEndpoint]
public class LibraryStructureController : BaseTesserafinApiController
{
    private readonly ILibraryStructureReader _reader;
    private readonly IProviderManager _providers;
    private readonly ILibraryStructureCommands _commands;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryStructureController"/> class.
    /// </summary>
    /// <param name="commands">Library structure commands.</param>
    /// <param name="reader">Configured library reader.</param>
    /// <param name="providers">Metadata refresh state.</param>
    public LibraryStructureController(
        ILibraryStructureReader reader,
        IProviderManager providers,
        ILibraryStructureCommands commands)
    {
        _reader = reader;
        _providers = providers;
        _commands = commands;
    }

    /// <summary>
    /// Gets all virtual folders.
    /// </summary>
    /// <response code="200">Virtual folders retrieved.</response>
    /// <returns>An <see cref="IEnumerable{VirtualFolderInfo}"/> with the virtual folders.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<VirtualFolderInfo>> GetVirtualFolders()
    {
        var folders = _reader.GetVirtualFolders();
        var queue = _providers.GetRefreshQueue();
        foreach (var folder in folders)
        {
            if (Guid.TryParse(folder.ItemId, out var id))
            {
                folder.RefreshProgress = _providers.GetRefreshProgress(id);
                folder.RefreshStatus = folder.RefreshProgress.HasValue ? "Active" : queue.Contains(id) ? "Queued" : "Idle";
            }
        }

        return Ok<IEnumerable<VirtualFolderInfo>>(folders);
    }

    /// <summary>
    /// Adds a virtual folder.
    /// </summary>
    /// <param name="name">The name of the virtual folder.</param>
    /// <param name="collectionType">The type of the collection.</param>
    /// <param name="paths">The paths of the virtual folder.</param>
    /// <param name="libraryOptionsDto">The library options.</param>
    /// <param name="refreshLibrary">Whether to refresh the library.</param>
    /// <response code="204">Folder added.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> AddVirtualFolder(
        [FromQuery]
        [RegularExpression(@"^(?:\S(?:.*\S)?)$", ErrorMessage = "Library name cannot be empty or have leading/trailing spaces.")]
        string name,
        [FromQuery] CollectionTypeOptions? collectionType,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] string[] paths,
        [FromBody] AddVirtualFolderDto? libraryOptionsDto,
        [FromQuery] bool refreshLibrary = false)
    {
        var libraryOptions = libraryOptionsDto?.LibraryOptions ?? new LibraryOptions();

        if (paths is not null && paths.Length > 0)
        {
            libraryOptions.PathInfos = Array.ConvertAll(paths, i => new MediaPathInfo(i));
        }

        await _commands.AddVirtualFolderAsync(name, collectionType, libraryOptions, refreshLibrary, HttpContext.RequestAborted).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Removes a virtual folder.
    /// </summary>
    /// <param name="name">The name of the folder.</param>
    /// <param name="refreshLibrary">Whether to refresh the library.</param>
    /// <response code="204">Folder removed.</response>
    /// <response code="404">Folder not found.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> RemoveVirtualFolder(
        [FromQuery] string name,
        [FromQuery] bool refreshLibrary = false)
    {
        // TODO: refactor! this relies on an FileNotFound exception to return NotFound when attempting to remove a library that does not exist.
        await _commands.RemoveVirtualFolderAsync(name, refreshLibrary, HttpContext.RequestAborted).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Renames a virtual folder.
    /// </summary>
    /// <param name="name">The name of the virtual folder.</param>
    /// <param name="newName">The new name.</param>
    /// <param name="refreshLibrary">Whether to refresh the library.</param>
    /// <response code="204">Folder renamed.</response>
    /// <response code="404">Library doesn't exist.</response>
    /// <response code="409">Library already exists.</response>
    /// <returns>A <see cref="NoContentResult"/> on success, a <see cref="NotFoundResult"/> if the library doesn't exist, a <see cref="ConflictResult"/> if the new name is already taken.</returns>
    /// <exception cref="ArgumentNullException">The new name may not be null.</exception>
    [HttpPost("Name")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> RenameVirtualFolder(
        [FromQuery] string? name,
        [FromQuery] string? newName,
        [FromQuery] bool refreshLibrary = false)
    {
        var result = await _commands.RenameVirtualFolderAsync(name!, newName!, refreshLibrary, HttpContext.RequestAborted).ConfigureAwait(false);
        return result switch
        {
            LibraryRenameResult.NotFound => NotFound("The media collection does not exist."),
            LibraryRenameResult.Conflict => Conflict($"The media library {newName} already exists."),
            _ => NoContent()
        };
    }

    /// <summary>
    /// Add a media path to a library.
    /// </summary>
    /// <param name="mediaPathDto">The media path dto.</param>
    /// <param name="refreshLibrary">Whether to refresh the library.</param>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    /// <response code="204">Media path added.</response>
    /// <exception cref="ArgumentNullException">The name of the library may not be empty.</exception>
    [HttpPost("Paths")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> AddMediaPath(
        [FromBody, Required] MediaPathDto mediaPathDto,
        [FromQuery] bool refreshLibrary = false)
    {
        var mediaPath = mediaPathDto.PathInfo ?? new MediaPathInfo(mediaPathDto.Path ?? throw new ArgumentException("PathInfo and Path can't both be null."));
        await _commands.AddMediaPathAsync(mediaPathDto.Name, mediaPath, refreshLibrary, HttpContext.RequestAborted).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Updates a media path.
    /// </summary>
    /// <param name="mediaPathRequestDto">The name of the library and path infos.</param>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    /// <response code="204">Media path updated.</response>
    /// <exception cref="ArgumentNullException">The name of the library may not be empty.</exception>
    [HttpPost("Paths/Update")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> UpdateMediaPath([FromBody, Required] UpdateMediaPathRequestDto mediaPathRequestDto)
    {
        if (string.IsNullOrWhiteSpace(mediaPathRequestDto.Name))
        {
            throw new ArgumentNullException(nameof(mediaPathRequestDto), "Name must not be null or empty");
        }

        await _commands.UpdateMediaPathAsync(mediaPathRequestDto.Name, mediaPathRequestDto.PathInfo, HttpContext.RequestAborted).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Remove a media path.
    /// </summary>
    /// <param name="name">The name of the library.</param>
    /// <param name="path">The path to remove.</param>
    /// <param name="refreshLibrary">Whether to refresh the library.</param>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    /// <response code="204">Media path removed.</response>
    /// <exception cref="ArgumentException">The name of the library and path may not be empty.</exception>
    [HttpDelete("Paths")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> RemoveMediaPath(
        [FromQuery] string name,
        [FromQuery] string path,
        [FromQuery] bool refreshLibrary = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await _commands.RemoveMediaPathAsync(name, path, refreshLibrary, HttpContext.RequestAborted).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Update library options.
    /// </summary>
    /// <param name="request">The library name and options.</param>
    /// <response code="204">Library updated.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("LibraryOptions")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UpdateLibraryOptions(
        [FromBody] UpdateLibraryOptionsDto request)
    {
        var found = await _commands.UpdateLibraryOptionsAsync(request.Id, request.LibraryOptions!, HttpContext.RequestAborted).ConfigureAwait(false);
        return found ? NoContent() : NotFound();
    }
}
