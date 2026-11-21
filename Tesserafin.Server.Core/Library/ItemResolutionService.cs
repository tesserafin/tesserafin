#pragma warning disable CS1591
#pragma warning disable CA5394

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Resolvers;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Naming.Common;
using Tesserafin.Naming.Video;
using Tesserafin.Server.Core.Library.Resolvers;

namespace Tesserafin.Server.Core.Library;

/// <summary>Filesystem resolution and typed alternate construction without write callbacks.</summary>
internal sealed class ItemResolutionService(
    IServerConfigurationManager configuration,
    IFileSystem files,
    ILibraryItemOptions options,
    IItemLookupService lookup,
    NamingOptions namingOptions,
    ILogger<ItemResolutionService> logger,
    ILoggerFactory loggerFactory,
    IDirectoryService directoryService,
    DotIgnoreIgnoreRule dotIgnore) : IItemResolutionService
{
    private readonly IServerConfigurationManager _configurationManager = configuration;
    private readonly IFileSystem _fileSystem = files;
    private readonly NamingOptions _namingOptions = namingOptions;
    private readonly ILogger<ItemResolutionService> _logger = logger;

    private readonly ExtraResolver _extraResolver = new(loggerFactory.CreateLogger<ExtraResolver>(), namingOptions, directoryService);
    private readonly DotIgnoreIgnoreRule _dotIgnoreIgnoreRule = dotIgnore;

    private IResolverIgnoreRule[] EntityResolutionIgnoreRules { get; set; } = [];

    private IItemResolver[] EntityResolvers { get; set; } = [];

    private IMultiItemResolver[] MultiItemResolvers { get; set; } = [];

    public IReadOnlyList<ResolvedShortcutTarget> ResolveShortcutTargets(Folder owner, IReadOnlyList<FileSystemMetadata> fileSystemChildren, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<ResolvedShortcutTarget>();
        foreach (var file in fileSystemChildren.Where(file => !file.IsDirectory && _fileSystem.IsShortcut(file.FullName)).OrderBy(file => file.FullName, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogDebug("Resolving shortcut {Path}", file.FullName);
            var raw = _fileSystem.ResolveShortcut(file.FullName);
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new IOException($"Shortcut has no target: {file.FullName}");
            }

            var expanded = VirtualPaths.Expand(_configurationManager.ApplicationPaths, raw);
            if (string.IsNullOrWhiteSpace(expanded))
            {
                throw new IOException($"Shortcut has no target: {file.FullName}");
            }

            var target = FilePath.MakeAbsolute(owner.ContainingFolderPath, expanded);
            if (string.IsNullOrWhiteSpace(target))
            {
                throw new IOException($"Shortcut has no absolute target: {file.FullName}");
            }

            result.Add(new ResolvedShortcutTarget(file.FullName, target));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    /// <inheritdoc />
    public IEnumerable<BaseItem> FindExtras(BaseItem owner, IReadOnlyList<FileSystemMetadata> fileSystemChildren, IDirectoryService directoryService)
    {
        // Apply .ignore rules
        var filtered = fileSystemChildren.Where(c => !_dotIgnoreIgnoreRule.ShouldIgnore(c, owner)).ToList();
        var isFolder = owner.IsFolder || (owner is Video video && (video.VideoType == VideoType.BluRay || video.VideoType == VideoType.Dvd));
        var ownerVideoInfo = VideoResolver.Resolve(owner.Path, isFolder, _namingOptions, libraryRoot: owner.ContainingFolderPath);
        if (ownerVideoInfo is null)
        {
            yield break;
        }

        var count = filtered.Count;
        for (var i = 0; i < count; i++)
        {
            var current = filtered[i];
            if (current.IsDirectory && _namingOptions.AllExtrasTypesFolderNames.ContainsKey(current.Name))
            {
                var filesInSubFolder = _fileSystem.GetFiles(current.FullName, null, false, false);
                var filesInSubFolderList = filesInSubFolder.ToList();

                bool subFolderIsMixedFolder = filesInSubFolderList.Count > 1;

                foreach (var file in filesInSubFolderList)
                {
                    if (!_extraResolver.TryGetExtraTypeForOwner(file.FullName, ownerVideoInfo, out var extraType))
                    {
                        continue;
                    }

                    var extra = GetExtra(file, extraType.Value, subFolderIsMixedFolder);
                    if (extra is not null)
                    {
                        yield return extra;
                    }
                }
            }
            else if (!current.IsDirectory && _extraResolver.TryGetExtraTypeForOwner(current.FullName, ownerVideoInfo, out var extraType))
            {
                var extra = GetExtra(current, extraType.Value, false);
                if (extra is not null)
                {
                    yield return extra;
                }
            }
        }

        BaseItem? GetExtra(FileSystemMetadata file, ExtraType extraType, bool isInMixedFolder)
        {
            var extra = ResolvePath(_fileSystem.GetFileInfo(file.FullName), directoryService, _extraResolver.GetResolversForExtraType(extraType));
            if (extra is not Video && extra is not Audio)
            {
                return null;
            }

            // Try to retrieve it from the db. If we don't find it, use the resolved version
            var itemById = GetItemById(extra.Id);
            if (itemById is not null)
            {
                extra = itemById.CopyForWrite();
            }

            // Only update extra type if it is more specific then the currently known extra type
            if (extra.ExtraType is null or ExtraType.Unknown || extraType != ExtraType.Unknown)
            {
                extra.ExtraType = extraType;
            }

            // Only return items that are actual extras (have ExtraType set)
            // Note: OwnerId and ParentId are set by RefreshExtras, not here,
            // so that RefreshExtras can detect when they need updating and set ForceSave.
            if (extra.ExtraType is not null)
            {
                extra.IsInMixedFolder = isInMixedFolder;
                return extra;
            }

            return null;
        }
    }

    public void AddParts(IEnumerable<IResolverIgnoreRule> rules, IEnumerable<IItemResolver> resolvers)
    {
        EntityResolutionIgnoreRules = rules.ToArray();
        EntityResolvers = resolvers.OrderBy(i => i.Priority).ToArray();
        MultiItemResolvers = EntityResolvers.OfType<IMultiItemResolver>().ToArray();
    }

    public Guid GetNewItemId(string key, Type type) => ItemIdentity.Create(_configurationManager, key, type);

    public BaseItem? GetItemById(Guid id) => lookup.GetItemById(id);

    public T? GetItemById<T>(Guid id)
        where T : BaseItem
        => lookup.GetItemById<T>(id);

    public bool AreEqualPaths(string first, string second) => _fileSystem.AreEqual(first, second);

    public LibraryOptions GetLibraryOptions(BaseItem item) => options.GetLibraryOptions(item);

    public CollectionType? GetConfiguredContentType(string path) => options.GetConfiguredContentType(path);

    private CollectionType? GetContentTypeOverride(string path, bool inherit) => options.GetConfiguredContentType(path, inherit);

    private BaseItem? ResolveItem(ItemResolveArgs args, IItemResolver[]? resolvers)
    {
        var item = (resolvers ?? EntityResolvers).Select(r => Resolve(args, r))
            .FirstOrDefault(i => i is not null);

        if (item is not null)
        {
            ResolverHelper.SetInitialItemValues(item, args, _fileSystem, this, _configurationManager.GetMetadataConfiguration().UseFileCreationTimeForDateAdded);
        }

        return item;
    }

    private BaseItem? Resolve(ItemResolveArgs args, IItemResolver resolver)
    {
        try
        {
            return resolver.ResolvePath(args);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in {Resolver} resolving {Path}", resolver.GetType().Name, args.Path);
            return null;
        }
    }

    public BaseItem? ResolvePath(
        FileSystemMetadata fileInfo,
        Folder? parent = null,
        IDirectoryService? directoryService = null,
        CollectionType? collectionType = null)
        => ResolvePath(fileInfo, directoryService ?? new DirectoryService(_fileSystem), null, parent, collectionType);

    private void SetAdditionalPartsFromStack(Video altVideo, string path)
    {
        if (altVideo.AdditionalParts is { Length: > 0 })
        {
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        IEnumerable<FileSystemMetadata> siblings;
        try
        {
            siblings = _fileSystem.GetFiles(directory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate siblings to detect stack for {Path}", path);
            return;
        }

        var stacks = StackResolver.Resolve(siblings, _namingOptions);
        foreach (var stack in stacks)
        {
            if (stack.Files.Count > 1
                && string.Equals(stack.Files[0], path, StringComparison.OrdinalIgnoreCase))
            {
                altVideo.AdditionalParts = stack.Files.Skip(1).ToArray();
                return;
            }
        }
    }

    /// <inheritdoc />
    public Video? ResolveAlternateVersion(string path, Type expectedVideoType, Folder? parent, CollectionType? collectionType)
    {
        // Identity and concrete type follow the primary, including in mixed-content libraries.
        var expectedId = GetNewItemId(path, expectedVideoType);
        var resolved = ResolvePath(
            _fileSystem.GetFileSystemInfo(path),
            parent,
            collectionType: collectionType) as Video;

        if (resolved is null)
        {
            return null;
        }

        // Ensure the alternate version has the same concrete type as the primary video.
        // ResolvePath may return a generic Video for files in mixed-content libraries
        // where collectionType is null, even though the primary is a Movie/Episode/etc.
        if (resolved.GetType() != expectedVideoType)
        {
            if (Activator.CreateInstance(expectedVideoType) is Video correctVideo)
            {
                correctVideo.Path = resolved.Path;
                correctVideo.Name = resolved.Name;
                correctVideo.VideoType = resolved.VideoType;
                correctVideo.ProductionYear = resolved.ProductionYear;
                correctVideo.ExtraType = resolved.ExtraType;
                resolved = correctVideo;
            }
        }

        resolved.Id = expectedId;
        SetAdditionalPartsFromStack(resolved, path);
        return resolved;
    }

    public BaseItem? ResolvePath(
        FileSystemMetadata fileInfo,
        IDirectoryService directoryService,
        IItemResolver[]? resolvers,
        Folder? parent = null,
        CollectionType? collectionType = null,
        LibraryOptions? libraryOptions = null)
    {
        ArgumentNullException.ThrowIfNull(fileInfo);

        var fullPath = fileInfo.FullName;

        if (collectionType is null && parent is not null)
        {
            collectionType = GetContentTypeOverride(fullPath, true);
        }

        var args = new ItemResolveArgs(_configurationManager.ApplicationPaths, this)
        {
            Parent = parent,
            FileInfo = fileInfo,
            CollectionType = collectionType,
            LibraryOptions = libraryOptions
        };

        // Return null if ignore rules deem that we should do so
        if (IgnoreFile(args.FileInfo, args.Parent))
        {
            return null;
        }

        // Gather child folder and files
        if (args.IsDirectory)
        {
            var isPhysicalRoot = args.IsPhysicalRoot;
            var isVf = args.IsVf;

            try
            {
                if (isPhysicalRoot || isVf)
                {
                    var discovery = FileData.ReadLibraryDirectory(args.FileInfo, directoryService, _fileSystem, _configurationManager.ApplicationPaths, _logger, isPhysicalRoot);
                    args.FileSystemChildren = discovery.Entries;
                    args.PhysicalLocations = discovery.PhysicalLocations;
                }
                else
                {
                    args.FileSystemChildren = directoryService.GetFileSystemEntries(args.Path);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (parent is not null && parent.IsPhysicalRoot)
                {
                    _logger.LogError(ex, "Error reading folder entries (physical root: {PhysicalRoot}, virtual folder: {VirtualFolder})", isPhysicalRoot, isVf);

                    args.FileSystemChildren = [];
                }
                else
                {
                    throw;
                }
            }
        }

        // Filter content based on ignore rules
        if (args.IsDirectory)
        {
            var filtered = args.GetActualFileSystemChildren().ToArray();
            args.FileSystemChildren = filtered ?? [];
        }

        return ResolveItem(args, resolvers);
    }

    public bool IgnoreFile(FileSystemMetadata file, BaseItem? parent)
        => EntityResolutionIgnoreRules.Any(r => r.ShouldIgnore(file, parent));

    public IEnumerable<BaseItem> ResolveFolderChildren(Folder parent, IReadOnlyList<FileSystemMetadata> files, IDirectoryService directoryService, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var collectionType = options.GetContentType(parent);
        var libraryOptions = options.GetLibraryOptions(parent);
        var fileList = new List<FileSystemMetadata>(files.Count);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IgnoreFile(file, parent))
            {
                fileList.Add(file);
            }
        }

        foreach (var resolver in MultiItemResolvers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = resolver.ResolveMultiple(parent, fileList, collectionType, directoryService);
            if (result?.Items.Count > 0)
            {
                var useFileCreationTime = _configurationManager.GetMetadataConfiguration().UseFileCreationTimeForDateAdded;
                var items = result.Items;
                items.RemoveAll(item =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return !ResolverHelper.SetInitialItemValues(item, parent, this, directoryService, useFileCreationTime);
                });
                items.AddRange(ResolveFileList(result.ExtraFiles, directoryService, parent, collectionType, libraryOptions, cancellationToken));
                return items;
            }
        }

        return ResolveFileList(fileList, directoryService, parent, collectionType, libraryOptions, cancellationToken);
    }

    private IEnumerable<BaseItem> ResolveFileList(
        IReadOnlyList<FileSystemMetadata> fileList,
        IDirectoryService directoryService,
        Folder parent,
        CollectionType? collectionType,
        LibraryOptions libraryOptions,
        CancellationToken cancellationToken)
    {
        // Given that fileList is a list we can save enumerator allocations by indexing
        for (var i = 0; i < fileList.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = fileList[i];
            BaseItem? result = null;
            try
            {
                result = ResolvePath(file, directoryService, EntityResolvers, parent, collectionType, libraryOptions);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving path {Path}", file.FullName);
            }

            if (result is not null)
            {
                yield return result;
            }
        }
    }
}
