using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Extensions;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;

namespace Tesserafin.Server.Core.Library;

/// <summary>One mutation boundary for library definitions; all filesystem checks run under scan admission.</summary>
internal sealed class LibraryStructureCommands : ILibraryStructureCommands
{
    private const string ShortcutFileExtension = ".mblink";
    private readonly IServerConfigurationManager _configurationManager;
    private readonly IFileSystem _fileSystem;
    private readonly IServerApplicationHost _appHost;
    private readonly ILibraryScanCoordinator _scans;
    private readonly IItemLookupService _items;
    private readonly ILogger<LibraryStructureCommands> _logger;

    public LibraryStructureCommands(IServerConfigurationManager configurationManager, IFileSystem fileSystem, IServerApplicationHost appHost, ILibraryScanCoordinator scans, IItemLookupService items, ILogger<LibraryStructureCommands> logger)
    {
        _configurationManager = configurationManager;
        _fileSystem = fileSystem;
        _appHost = appHost;
        _scans = scans;
        _items = items;
        _logger = logger;
    }

    private string Root => _configurationManager.ApplicationPaths.DefaultUserViewsPath;

    private Task ChangeAsync(Action change, bool refreshLibrary, bool removeRoot, CancellationToken cancellationToken)
        => _scans.ApplyLibraryChangeAsync(
            () =>
            {
                try
                {
                    change();
                }
                finally
                {
                    CollectionFolder.OnCollectionFolderChange();
                }
            },
            refreshLibrary,
            removeRoot,
            cancellationToken);

    public Task AddVirtualFolderAsync(string name, CollectionTypeOptions? collectionType, LibraryOptions options, bool refreshLibrary, CancellationToken cancellationToken)
        => ChangeAsync(
            () =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(name);
                ArgumentNullException.ThrowIfNull(options);
                VirtualFolderPath.Resolve(Root, name.Trim());
                var originalName = _fileSystem.GetValidFilename(name.Trim());
                var selectedName = originalName;
                var path = VirtualFolderPath.Resolve(Root, selectedName);
                var suffix = 1;
                while (Directory.Exists(path) || File.Exists(path))
                {
                    selectedName = originalName + (++suffix).ToString(CultureInfo.InvariantCulture);
                    path = VirtualFolderPath.Resolve(Root, selectedName);
                }

                foreach (var source in options.PathInfos)
                {
                    ValidateSource(source);
                }

                Directory.CreateDirectory(path);
                if (collectionType is not null)
                {
                    FileHelper.CreateEmpty(Path.Combine(path, collectionType.ToString()!.ToLowerInvariant() + ".collection"));
                }

                CollectionFolder.SaveLibraryOptions(path, options);
                foreach (var source in options.PathInfos)
                {
                    AddMediaPathInternal(selectedName, source, false);
                }
            },
            refreshLibrary,
            false,
            cancellationToken);

    public Task RemoveVirtualFolderAsync(string name, bool refreshLibrary, CancellationToken cancellationToken)
        => ChangeAsync(() => Directory.Delete(ExistingLibrary(name), true), refreshLibrary, true, cancellationToken);

    public async Task<LibraryRenameResult> RenameVirtualFolderAsync(string name, string newName, bool refreshLibrary, CancellationToken cancellationToken)
    {
        var result = LibraryRenameResult.Renamed;
        await ChangeAsync(
            () =>
            {
                var current = VirtualFolderPath.Resolve(Root, name);
                var destination = VirtualFolderPath.Resolve(Root, newName);
                if (!Directory.Exists(current))
                {
                    result = LibraryRenameResult.NotFound;
                    return;
                }

                var caseOnly = _fileSystem.AreEqual(current, destination);
                if (!caseOnly && (Directory.Exists(destination) || File.Exists(destination)))
                {
                    result = LibraryRenameResult.Conflict;
                    return;
                }

                if (caseOnly)
                {
                    var temporary = VirtualFolderPath.Resolve(Root, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
                    Directory.Move(current, temporary);
                    try
                    {
                        Directory.Move(temporary, destination);
                    }
                    catch
                    {
                        // The source definition remains recoverable even if restoration itself fails.
                        try
                        {
                            Directory.Move(temporary, current);
                        }
                        catch (Exception cleanup)
                        {
                            _logger.LogError(cleanup, "Error restoring library definition after rename failure");
                        }

                        throw;
                    }
                }
                else
                {
                    Directory.Move(current, destination);
                }
            },
            refreshLibrary,
            true,
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task AddMediaPathAsync(string name, MediaPathInfo path, bool refreshLibrary, CancellationToken cancellationToken)
        => ChangeAsync(() => AddMediaPathInternal(name, path, true), refreshLibrary, false, cancellationToken);

    public Task UpdateMediaPathAsync(string name, MediaPathInfo path, CancellationToken cancellationToken)
        => ChangeAsync(
            () =>
            {
                ArgumentNullException.ThrowIfNull(path);
                var library = ExistingLibrary(name);
                var options = Clone(CollectionFolder.GetLibraryOptions(library));
                SyncLibraryOptionsToLocations(library, options);
                CollectionFolder.SaveLibraryOptions(library, options);
            },
            false,
            false,
            cancellationToken);

    public Task RemoveMediaPathAsync(string name, string path, bool refreshLibrary, CancellationToken cancellationToken)
        => ChangeAsync(() => RemoveMediaPath(name, path), refreshLibrary, true, cancellationToken);

    public async Task<bool> UpdateLibraryOptionsAsync(Guid id, LibraryOptions options, CancellationToken cancellationToken)
    {
        var found = false;
        await ChangeAsync(
            () =>
            {
                ArgumentNullException.ThrowIfNull(options);
                if (_items.GetItemById(id) is not CollectionFolder item || !Directory.Exists(item.Path))
                {
                    return;
                }

                // Resolve by name as well: an item id never permits writing outside the library root.
                var library = ExistingLibrary(Path.GetFileName(item.Path));
                if (!_fileSystem.AreEqual(Path.GetFullPath(item.Path), library))
                {
                    throw new ArgumentException("The item is not a library definition.");
                }

                var old = CollectionFolder.GetLibraryOptions(library);
                foreach (var source in options.PathInfos)
                {
                    if (!old.PathInfos.Any(path => string.Equals(path.Path, source.Path, StringComparison.Ordinal)))
                    {
                        ValidateSource(source);
                    }
                }

                foreach (var source in options.PathInfos.Where(source => !old.PathInfos.Any(path => string.Equals(path.Path, source.Path, StringComparison.Ordinal))))
                {
                    CreateShortcut(library, source);
                }

                CollectionFolder.SaveLibraryOptions(library, options);
                found = true;
            },
            false,
            false,
            cancellationToken).ConfigureAwait(false);
        return found;
    }

    private string ExistingLibrary(string name)
    {
        var path = VirtualFolderPath.Resolve(Root, name);
        if (!Directory.Exists(path))
        {
            throw new FileNotFoundException("The media collection does not exist.");
        }

        return path;
    }

    private static void ValidateSource(MediaPathInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Path);
        if (!Directory.Exists(source.Path))
        {
            throw new FileNotFoundException("The media path does not exist.");
        }
    }

    private static LibraryOptions Clone(LibraryOptions options)
        => JsonSerializer.Deserialize<LibraryOptions>(JsonSerializer.SerializeToUtf8Bytes(options, JsonDefaults.Options), JsonDefaults.Options)!;

    private void AddMediaPathInternal(string virtualFolderName, MediaPathInfo pathInfo, bool saveLibraryOptions)
    {
        ArgumentNullException.ThrowIfNull(pathInfo);

        var path = pathInfo.Path;

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(nameof(path));
        }

        if (!Directory.Exists(path))
        {
            throw new FileNotFoundException("The path does not exist.");
        }

        var rootFolderPath = _configurationManager.ApplicationPaths.DefaultUserViewsPath;
        var virtualFolderPath = ExistingLibrary(virtualFolderName);

        CreateShortcut(virtualFolderPath, pathInfo);

        if (saveLibraryOptions)
        {
            var libraryOptions = Clone(CollectionFolder.GetLibraryOptions(virtualFolderPath));

            libraryOptions.PathInfos = [.. libraryOptions.PathInfos, pathInfo];

            SyncLibraryOptionsToLocations(virtualFolderPath, libraryOptions);

            CollectionFolder.SaveLibraryOptions(virtualFolderPath, libraryOptions);
        }
    }

    private void SyncLibraryOptionsToLocations(string virtualFolderPath, LibraryOptions options)
    {
        var locations = LibraryPathReader.ReadLocations(virtualFolderPath, _fileSystem, _appHost, _logger);

        if (locations.Length > 0 && locations.Length != options.PathInfos.Length)
        {
            var list = options.PathInfos.ToList();

            foreach (var location in locations)
            {
                if (!list.Any(i => string.Equals(i.Path, location, StringComparison.Ordinal)))
                {
                    list.Add(new MediaPathInfo(location));
                }
            }

            options.PathInfos = list.ToArray();
        }
    }

    private void RemoveContentTypeOverrides(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentNullException(nameof(path));
        }

        List<NameValuePair>? removeList = null;

        foreach (var contentType in _configurationManager.Configuration.ContentTypes)
        {
            if (string.IsNullOrWhiteSpace(contentType.Name)
                || _fileSystem.AreEqual(path, contentType.Name)
                || _fileSystem.ContainsSubPath(path, contentType.Name))
            {
                (removeList ??= new()).Add(contentType);
            }
        }

        if (removeList is not null)
        {
            _configurationManager.Configuration.ContentTypes = _configurationManager.Configuration.ContentTypes
                .Except(removeList)
                .ToArray();

            _configurationManager.SaveConfiguration();
        }
    }

    private void RemoveMediaPath(string virtualFolderName, string mediaPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(mediaPath);

        var rootFolderPath = _configurationManager.ApplicationPaths.DefaultUserViewsPath;
        var virtualFolderPath = ExistingLibrary(virtualFolderName);

        if (!Directory.Exists(virtualFolderPath))
        {
            throw new FileNotFoundException(
                string.Format(CultureInfo.InvariantCulture, "The media collection {0} does not exist", virtualFolderName));
        }

        var shortcut = _fileSystem.GetFilePaths(virtualFolderPath, true)
            .Where(i => Path.GetExtension(i.AsSpan()).Equals(ShortcutFileExtension, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(f => _appHost.ExpandVirtualPath(_fileSystem.ResolveShortcut(f)).Equals(mediaPath, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(shortcut))
        {
            _fileSystem.DeleteFile(shortcut);
        }

        var libraryOptions = Clone(CollectionFolder.GetLibraryOptions(virtualFolderPath));

        libraryOptions.PathInfos = libraryOptions
            .PathInfos
            .Where(i => !string.Equals(i.Path, mediaPath, StringComparison.Ordinal))
            .ToArray();

        CollectionFolder.SaveLibraryOptions(virtualFolderPath, libraryOptions);
    }

    private void CreateShortcut(string virtualFolderPath, MediaPathInfo pathInfo)
    {
        var path = pathInfo.Path;
        var rootFolderPath = _configurationManager.ApplicationPaths.DefaultUserViewsPath;

        var shortcutFilename = Path.GetFileNameWithoutExtension(path);

        // The shortcut's leaf name is derived from the caller-supplied media path.
        // GetFileNameWithoutExtension drops any directory structure, but dropping structure
        // is not the same as guaranteeing containment, so the result is resolved through the
        // same rule the virtual folder root uses. The link is then provably a direct child of
        // this library, whatever the media path looked like.
        var lnk = VirtualFolderPath.Resolve(virtualFolderPath, shortcutFilename + ShortcutFileExtension);

        while (File.Exists(lnk))
        {
            shortcutFilename += "1";
            lnk = VirtualFolderPath.Resolve(virtualFolderPath, shortcutFilename + ShortcutFileExtension);
        }

        _fileSystem.CreateShortcut(lnk, _appHost.ReverseVirtualPath(path));
        RemoveContentTypeOverrides(path);
    }
}
