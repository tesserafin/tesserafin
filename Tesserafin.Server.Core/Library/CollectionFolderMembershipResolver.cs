using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.IO;

namespace Tesserafin.Server.Core.Library;

/// <summary>Reads a library definition and resolves its physical catalogue folders.</summary>
internal sealed class CollectionFolderMembershipResolver(
    ILibraryRootFolderProvider roots,
    IItemRepository repository,
    IItemLookupService lookup,
    IFileSystem files,
    IServerConfigurationManager configuration,
    ILogger<CollectionFolderMembershipResolver> logger)
{
    public (string[] Locations, IReadOnlyList<Folder> Folders) Resolve(CollectionFolder owner, IDirectoryService directoryService, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = files.GetDirectoryInfo(owner.ContainingFolderPath);
        var discovery = FileData.ReadLibraryDirectory(directory, directoryService, files, configuration.ApplicationPaths, logger, physicalRoot: false);
        if (discovery.Entries is null)
        {
            throw new DirectoryNotFoundException($"Cannot read library directory {owner.ContainingFolderPath}.");
        }

        var locations = discovery.PhysicalLocations.Where(location => !files.AreEqual(location, owner.Path)).ToArray();
        if (locations.Length == 0)
        {
            return (discovery.PhysicalLocations, []);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var rootFolders = repository.GetItemList(new InternalItemsQuery
        {
            Parent = roots.GetRootFolder(),
            GroupByPresentationUniqueKey = false,
            DtoOptions = new DtoOptions(true)
        }).OfType<Folder>().ToArray();

        var folders = new List<Folder>();
        var seen = new HashSet<Guid>();
        foreach (var location in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Prefer folders already scanned at the root. Nested configured paths may be
            // covered by an ancestor in the physical scan, so resolve those by path.
            var matches = rootFolders.Where(folder => files.AreEqual(folder.Path, location)).ToArray();
            if (matches.Length == 0 && ItemPathQuery.Find(repository, location, true) is Folder nested)
            {
                matches = [nested];
            }

            foreach (var folder in matches)
            {
                if (!folder.Id.Equals(owner.Id) && seen.Add(folder.Id))
                {
                    folders.Add(lookup.GetItemById(folder.Id) as Folder ?? folder);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return (discovery.PhysicalLocations, folders);
    }
}
