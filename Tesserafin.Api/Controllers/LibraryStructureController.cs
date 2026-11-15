using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tesserafin.Api.Auth.FirstTimeSetupPolicy;
using Tesserafin.Api.Extensions;
using Tesserafin.Api.Helpers;
using Tesserafin.Api.ModelBinders;
using Tesserafin.Api.Models.LibraryStructureDto;
using Tesserafin.Common.Api;
using Tesserafin.Controller;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
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
    private readonly IServerApplicationPaths _appPaths;
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryScanCoordinator _scans;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryStructureController"/> class.
    /// </summary>
    /// <param name="serverConfigurationManager">Instance of <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="scans">Library scan scheduling and state.</param>
    /// <param name="libraryManager">Instance of <see cref="ILibraryManager"/> interface.</param>
    public LibraryStructureController(
        IServerConfigurationManager serverConfigurationManager,
        ILibraryManager libraryManager,
        ILibraryScanCoordinator scans)
    {
        _appPaths = serverConfigurationManager.ApplicationPaths;
        _libraryManager = libraryManager;
        _scans = scans;
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
        return _libraryManager.GetVirtualFolders(true);
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

        await _libraryManager.AddVirtualFolder(name, collectionType, libraryOptions, refreshLibrary).ConfigureAwait(false);

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
        await _libraryManager.RemoveVirtualFolder(name, refreshLibrary).ConfigureAwait(false);

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
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentNullException(nameof(name));
        }

        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new ArgumentNullException(nameof(newName));
        }

        var rootFolderPath = _appPaths.DefaultUserViewsPath;

        // Rename resolves two caller-supplied names, so it is held to the same containment rule
        // as create, read, update and delete even though it does its own path work here.
        var currentPath = VirtualFolderPath.Resolve(rootFolderPath, name);
        var newPath = VirtualFolderPath.Resolve(rootFolderPath, newName);

        if (!Directory.Exists(currentPath))
        {
            return NotFound("The media collection does not exist.");
        }

        if (!string.Equals(currentPath, newPath, StringComparison.OrdinalIgnoreCase) && Directory.Exists(newPath))
        {
            // Reported by name, never by host path.
            return Conflict($"The media library {newName} already exists.");
        }

        await _scans.ApplyLibraryChangeAsync(
            () =>
            {
                try
                {
                    if (string.Equals(currentPath, newPath, StringComparison.OrdinalIgnoreCase))
                    {
                        var tempPath = VirtualFolderPath.Resolve(_appPaths.DefaultUserViewsPath, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
                        Directory.Move(currentPath, tempPath);
                        currentPath = tempPath;
                    }

                    Directory.Move(currentPath, newPath);
                }
                finally
                {
                    CollectionFolder.OnCollectionFolderChange();
                }
            },
            refreshLibrary,
            removeRoot: true,
            CancellationToken.None).ConfigureAwait(false);

        return NoContent();
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
        await _scans.ApplyLibraryChangeAsync(() => _libraryManager.AddMediaPath(mediaPathDto.Name, mediaPath), refreshLibrary, removeRoot: false, CancellationToken.None).ConfigureAwait(false);

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
    public ActionResult UpdateMediaPath([FromBody, Required] UpdateMediaPathRequestDto mediaPathRequestDto)
    {
        if (string.IsNullOrWhiteSpace(mediaPathRequestDto.Name))
        {
            throw new ArgumentNullException(nameof(mediaPathRequestDto), "Name must not be null or empty");
        }

        _libraryManager.UpdateMediaPath(mediaPathRequestDto.Name, mediaPathRequestDto.PathInfo);
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

        await _scans.ApplyLibraryChangeAsync(() => _libraryManager.RemoveMediaPath(name, path), refreshLibrary, removeRoot: true, CancellationToken.None).ConfigureAwait(false);

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
    public ActionResult UpdateLibraryOptions(
        [FromBody] UpdateLibraryOptionsDto request)
    {
        var item = _libraryManager.GetItemById<CollectionFolder>(request.Id);
        if (item is null)
        {
            return NotFound();
        }

        LibraryOptions options = item.GetLibraryOptions();
        foreach (var mediaPath in request.LibraryOptions!.PathInfos)
        {
            if (options.PathInfos.Any(i => i.Path == mediaPath.Path))
            {
                continue;
            }

            _libraryManager.CreateShortcut(item.Path, mediaPath);
        }

        item.UpdateLibraryOptions(request.LibraryOptions);
        return NoContent();
    }
}
