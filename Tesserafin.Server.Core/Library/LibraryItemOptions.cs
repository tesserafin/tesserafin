using System;
using System.Linq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.IO;

namespace Tesserafin.Server.Core.Library;

/// <summary>Reads effective item options and collection type from explicit hierarchy inputs.</summary>
internal sealed class LibraryItemOptions(
    ICollectionFolderResolver collections,
    IItemLookupService lookup,
    IUserRootFolderProvider roots,
    IServerConfigurationManager configuration,
    IFileSystem files) : ILibraryItemOptions
{
    public LibraryOptions GetLibraryOptions(BaseItem item)
    {
        if (item is CollectionFolder collectionFolder)
        {
            return collectionFolder.GetLibraryOptions();
        }

        // List.Find is more performant than FirstOrDefault due to enumerator allocation
        return collections.GetCollectionFolders(item)
            .Find(folder => folder is CollectionFolder) is CollectionFolder collectionFolder2
            ? collectionFolder2.GetLibraryOptions()
            : new LibraryOptions();
    }

    public CollectionType? GetContentType(BaseItem item)
        => GetConfiguredContentType(item, false)
            ?? GetConfiguredContentType(item, true)
            ?? GetInheritedContentType(item);

    public CollectionType? GetInheritedContentType(BaseItem item)
        => GetTopFolderContentType(item)
            ?? item.GetParents(lookup).Select(parent => GetConfiguredContentType(parent, false)).LastOrDefault(type => type is not null);

    private CollectionType? GetConfiguredContentType(BaseItem item, bool inherit)
        => item is ICollectionFolder collection
            ? collection.CollectionType
            : GetConfiguredContentType(item.ContainingFolderPath, inherit);

    public CollectionType? GetConfiguredContentType(string path, bool inherit = false)
    {
        var nameValuePair = configuration.Configuration.ContentTypes
                                .FirstOrDefault(i => files.AreEqual(i.Name, path)
                                                     || (inherit && !string.IsNullOrEmpty(i.Name)
                                                                 && files.ContainsSubPath(i.Name, path)));
        if (Enum.TryParse<CollectionType>(nameValuePair?.Value, out var collectionType))
        {
            return collectionType;
        }

        return null;
    }

    public CollectionType? GetTopFolderContentType(BaseItem? item)
    {
        if (item is null)
        {
            return null;
        }

        while (!item.ParentId.IsEmpty())
        {
            var parent = item.GetParent(lookup);
            if (parent is null || parent is AggregateFolder)
            {
                break;
            }

            item = parent;
        }

        return CollectionFolderResolver.GetLibraries(roots.GetUserRootFolder(), lookup)
            .OfType<ICollectionFolder>()
            .Where(i => string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase) || i.PhysicalLocations.Contains(item.Path))
            .Select(i => i.CollectionType)
            .FirstOrDefault(i => i is not null);
    }
}
