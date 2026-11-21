#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Threading;
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
    /// <summary>Reads a complete shortcut snapshot, failing before publication on unreadable entries.</summary>
    /// <param name="owner">The folder containing the shortcuts.</param>
    /// <param name="fileSystemChildren">Completed discovery inputs.</param>
    /// <param name="cancellationToken">Cancels each read and target expansion.</param>
    /// <returns>Expanded absolute shortcut targets in deterministic source-file order.</returns>
    IReadOnlyList<ResolvedShortcutTarget> ResolveShortcutTargets(Folder owner, IReadOnlyList<FileSystemMetadata> fileSystemChildren, CancellationToken cancellationToken);

    /// <summary>Resolves owned bonus-media candidates without saving or mutating published records.</summary>
    /// <param name="owner">The main media item.</param>
    /// <param name="fileSystemChildren">The owner's filesystem neighbours.</param>
    /// <param name="directoryService">Resolution directory access.</param>
    /// <returns>Resolved bonus candidates, detached from existing catalogue records.</returns>
    IEnumerable<BaseItem> FindExtras(BaseItem owner, IReadOnlyList<FileSystemMetadata> fileSystemChildren, IDirectoryService directoryService);

    void AddParts(IEnumerable<IResolverIgnoreRule> rules, IEnumerable<IItemResolver> resolvers);

    BaseItem? ResolvePath(FileSystemMetadata fileInfo, Folder? parent = null, IDirectoryService? directoryService = null, CollectionType? collectionType = null);

    BaseItem? ResolvePath(FileSystemMetadata fileInfo, IDirectoryService directoryService, IItemResolver[]? resolvers, Folder? parent = null, CollectionType? collectionType = null, LibraryOptions? libraryOptions = null);

    /// <summary>Reads the configured physical library root and expands its configured locations.</summary>
    /// <param name="folder">The physical library root.</param>
    /// <param name="directoryService">Directory access shared with the scan.</param>
    /// <returns>The discovered entries and locations.</returns>
    (FileSystemMetadata[]? Entries, string[] PhysicalLocations) DiscoverPhysicalRoot(AggregateFolder folder, IDirectoryService directoryService);

    /// <summary>Resolves a folder's filesystem entries with its current library options and collection type.</summary>
    /// <param name="parent">The folder whose children are being discovered.</param>
    /// <param name="files">Filesystem entries supplied by the folder.</param>
    /// <param name="directoryService">Directory access shared with the scan.</param>
    /// <param name="cancellationToken">Cancels candidate discovery and resolution.</param>
    /// <returns>The resolved candidates for catalogue reconciliation.</returns>
    IEnumerable<BaseItem> ResolveFolderChildren(Folder parent, IReadOnlyList<FileSystemMetadata> files, IDirectoryService directoryService, CancellationToken cancellationToken);

    Video? ResolveAlternateVersion(string path, Type expectedVideoType, Folder? parent, CollectionType? collectionType);
}
