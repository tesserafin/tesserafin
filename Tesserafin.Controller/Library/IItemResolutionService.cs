#pragma warning disable CS1591

using System.Collections.Generic;
using System;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Resolvers;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.IO;

namespace Tesserafin.Controller.Library;

/// <summary>Resolves filesystem candidates without saving items or invoking metadata exporters.</summary>
public interface IItemResolutionService : IItemResolutionPolicy
{
    void AddParts(IEnumerable<IResolverIgnoreRule> rules, IEnumerable<IItemResolver> resolvers);

    BaseItem? ResolvePath(FileSystemMetadata fileInfo, Folder? parent = null, IDirectoryService? directoryService = null, CollectionType? collectionType = null);

    BaseItem? ResolvePath(FileSystemMetadata fileInfo, IDirectoryService directoryService, IItemResolver[]? resolvers, Folder? parent = null, CollectionType? collectionType = null, LibraryOptions? libraryOptions = null);

    IEnumerable<BaseItem> ResolvePaths(IEnumerable<FileSystemMetadata> files, IDirectoryService directoryService, Folder parent, LibraryOptions libraryOptions, CollectionType? collectionType = null);

    IEnumerable<BaseItem> ResolvePaths(IEnumerable<FileSystemMetadata> files, IDirectoryService directoryService, Folder parent, LibraryOptions libraryOptions, CollectionType? collectionType, IItemResolver[] resolvers);

    Video? ResolveAlternateVersion(string path, Type expectedVideoType, Folder? parent, CollectionType? collectionType);

    List<FileSystemMetadata> NormalizeRootPathList(IEnumerable<FileSystemMetadata> paths);
}
