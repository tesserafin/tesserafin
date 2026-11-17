using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;

namespace Tesserafin.Server.Core.Library;

/// <summary>Projects library definitions through explicit read ports, without a metadata-provider dependency.</summary>
internal sealed class LibraryStructureReader(
    IServerConfigurationManager configuration,
    IFileSystem fileSystem,
    IServerApplicationHost appHost,
    IUserRootFolderProvider roots,
    IItemLookupService lookup,
    ILibraryItemOptions options,
    ILogger<LibraryStructureReader> logger) : ILibraryStructureReader
{
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IServerApplicationHost _appHost = appHost;
    private readonly ILibraryItemOptions _options = options;
    private readonly ILogger<LibraryStructureReader> _logger = logger;

    public IReadOnlyList<VirtualFolderInfo> GetVirtualFolders()
    {
        var root = roots.GetUserRootFolder();
        var libraries = (root is UserRootFolder userRoot ? userRoot.GetLibraries(lookup) : root.Children).ToList();
        return _fileSystem.GetDirectoryPaths(configuration.ApplicationPaths.DefaultUserViewsPath)
            .Select(path => GetVirtualFolderInfo(path, libraries))
            .ToList();
    }

    private VirtualFolderInfo GetVirtualFolderInfo(string dir, List<BaseItem> allCollectionFolders)
    {
        var info = new VirtualFolderInfo
        {
            Name = Path.GetFileName(dir),

            Locations = LibraryPathReader.ReadLocations(dir, _fileSystem, _appHost, _logger),

            CollectionType = GetCollectionType(dir)
        };

        var libraryFolder = allCollectionFolders.FirstOrDefault(i => string.Equals(i.Path, dir, StringComparison.OrdinalIgnoreCase));
        if (libraryFolder is not null)
        {
            var libraryFolderId = libraryFolder.Id.ToString("N", CultureInfo.InvariantCulture);
            info.ItemId = libraryFolderId;
            if (libraryFolder.HasImage(ImageType.Primary))
            {
                info.PrimaryImageItemId = libraryFolderId;
            }

            info.LibraryOptions = _options.GetLibraryOptions(libraryFolder);
        }

        return info;
    }

    private CollectionTypeOptions? GetCollectionType(string path)
    {
        var files = _fileSystem.GetFilePaths(path, [".collection"], true, false);
        foreach (ReadOnlySpan<char> file in files)
        {
            if (Enum.TryParse<CollectionTypeOptions>(Path.GetFileNameWithoutExtension(file), true, out var res))
            {
                return res;
            }
        }

        return null;
    }
}
