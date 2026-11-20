using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Persistence;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Model.MediaInfo;

namespace Tesserafin.Server.Core.Library;

/// <summary>Owns deletion ordering and reconciliation with explicit orchestration dependencies.</summary>
internal sealed class ItemDeletionCommands : IItemDeletionCommands, IDisposable
{
    private readonly IItemWriteCommands _writes;
    private readonly AsyncNonKeyedLocker _slot = new(1);
    private readonly IItemChangePublisher _changes;
    private readonly IItemLookupService _items;
    private readonly IItemRepository _repository;
    private readonly ILinkedChildrenService _linkedChildren;
    private readonly IItemPersistenceService _persistenceService;
    private readonly IItemCacheStore _itemCacheStore;
    private readonly IPathManager _pathManager;
    private readonly IFileSystem _fileSystem;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly Lazy<IExternalDataManager> _externalDataManagerFactory;
    private readonly Lazy<IMediaSourceManager> _sources;
    private readonly Lazy<IChannelManager> _channels;
    private readonly ActiveRecordings _recordings;
    private readonly ILogger<ItemDeletionCommands> _logger;

    public ItemDeletionCommands(
        IItemWriteCommands writes,
        IItemChangePublisher changes,
        IItemLookupService items,
        IItemRepository repository,
        ILinkedChildrenService linkedChildren,
        IItemPersistenceService persistenceService,
        IItemCacheStore itemCacheStore,
        IPathManager pathManager,
        IFileSystem fileSystem,
        IServerConfigurationManager configurationManager,
        Lazy<IExternalDataManager> externalDataManagerFactory,
        Lazy<IMediaSourceManager> sources,
        Lazy<IChannelManager> channels,
        ActiveRecordings recordings,
        ILogger<ItemDeletionCommands> logger)
    {
        _writes = writes;
        _changes = changes;
        _items = items;
        _repository = repository;
        _linkedChildren = linkedChildren;
        _persistenceService = persistenceService;
        _itemCacheStore = itemCacheStore;
        _pathManager = pathManager;
        _fileSystem = fileSystem;
        _configurationManager = configurationManager;
        _externalDataManagerFactory = externalDataManagerFactory;
        _sources = sources;
        _channels = channels;
        _recordings = recordings;
        _logger = logger;
    }

    public async Task DeleteAsync(BaseItem item, DeleteOptions options, BaseItem? parent, CancellationToken cancellationToken)
    {
        using (await _slot.LockAsync(cancellationToken).ConfigureAwait(false))
        {
            await DeleteCoreAsync(item, options, parent, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteUnreferencedAsync(IReadOnlyCollection<BaseItem> items, bool deleteSourceFiles, CancellationToken cancellationToken)
    {
        using (await _slot.LockAsync(cancellationToken).ConfigureAwait(false))
        {
            await DeleteUnreferencedCoreAsync(items, deleteSourceFiles, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose() => _slot.Dispose();

    public async Task DeleteObsoleteAlternateAsync(string path, Type expectedType, CancellationToken cancellationToken)
    {
        using (await _slot.LockAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (expectedType == typeof(Video))
            {
                return;
            }

            var oldId = ItemIdentity.Create(_configurationManager, path, typeof(Video));
            var expectedId = ItemIdentity.Create(_configurationManager, path, expectedType);
            if (!oldId.Equals(expectedId) && _items.GetItemById(oldId) is Video obsolete)
            {
                var parent = obsolete.GetOwner(_items) ?? obsolete.GetParent(_items);
                await DeleteCoreAsync(obsolete, new DeleteOptions { DeleteFileLocation = false }, parent, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task DeleteAsync(BaseItem item, DeleteOptions options, CancellationToken cancellationToken)
    {
        using (await _slot.LockAsync(cancellationToken).ConfigureAwait(false))
        {
            ArgumentNullException.ThrowIfNull(item);
            cancellationToken.ThrowIfCancellationRequested();
            var parent = item.GetOwner(_items) ?? item.GetParent(_items);
            await DeleteCoreAsync(item, options, parent, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task DeleteUnreferencedCoreAsync(IReadOnlyCollection<BaseItem> items, bool deleteSourceFiles, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (items.Count == 0)
        {
            return Task.CompletedTask;
        }

        var pathMaps = items.Select(e =>
            (Item: e,
            InternalPath: GetInternalMetadataPaths(e),
            DeletePaths: deleteSourceFiles ? e.GetDeletePaths(_fileSystem) : [])).ToArray();

        foreach (var (item, internalPaths, pathsToDelete) in pathMaps)
        {
            foreach (var metadataPath in internalPaths)
            {
                if (!Directory.Exists(metadataPath))
                {
                    continue;
                }

                _logger.LogDebug(
                    "Deleting metadata path, Type: {Type}, Name: {Name}, Path: {Path}, Id: {Id}",
                    item.GetType().Name,
                    item.Name ?? "Unknown name",
                    metadataPath,
                    item.Id);

                try
                {
                    Directory.Delete(metadataPath, true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error deleting {MetadataPath}", metadataPath);
                }
            }

            foreach (var fileSystemInfo in pathsToDelete)
            {
                DeleteItemPath(item, false, fileSystemInfo);
            }
        }

        var externalDataManager = _externalDataManagerFactory.Value;
        foreach (var (item, _, _) in pathMaps)
        {
            externalDataManager.DeleteExternalItemFiles(item);
        }

        _persistenceService.DeleteItem([.. pathMaps.Select(f => f.Item.Id)]);
        _itemCacheStore.RemoveRange(pathMaps.Select(f => f.Item.Id));
        return Task.CompletedTask;
    }

    private async Task DeleteCoreAsync(BaseItem item, DeleteOptions options, BaseItem? parent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();

        var isChannelItem = IsChannelItem(item);
        var deleteSource = options.DeleteFileLocation && !isChannelItem;
        if (isChannelItem && options.DeleteFromExternalProvider)
        {
            try
            {
                await _channels.Value.DeleteItem(item).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                // channel no longer installed
            }
        }

        if (item is LiveTvProgram)
        {
            _logger.LogDebug(
                "Removing item, Type: {Type}, Name: {Name}, Path: {Path}, Id: {Id}",
                item.GetType().Name,
                item.Name ?? "Unknown name",
                item.Path ?? string.Empty,
                item.Id);
        }
        else
        {
            _logger.LogInformation(
                "Removing item, Type: {Type}, Name: {Name}, Path: {Path}, Id: {Id}",
                item.GetType().Name,
                item.Name ?? "Unknown name",
                item.Path ?? string.Empty,
                item.Id);
        }

        var children = item.IsFolder
            ? ((Folder)item).GetRecursiveCatalogueChildren(_repository, _items)
            : [];

        var metadataPaths = GetMetadataPaths(item, children).ToArray();
        if ((deleteSource && IsFileProtocol(item)) || IsInternalItem(item))
        {
            // Assume only the first is required
            // Add this flag to GetDeletePaths if required in the future
            var isRequiredForDelete = true;

            foreach (var fileSystemInfo in item.GetDeletePaths(_fileSystem))
            {
                DeleteItemPath(item, isRequiredForDelete, fileSystemInfo);

                isRequiredForDelete = false;
            }
        }

        foreach (var metadataPath in metadataPaths)
        {
            if (!Directory.Exists(metadataPath))
            {
                continue;
            }

            _logger.LogDebug(
                "Deleting metadata path, Type: {Type}, Name: {Name}, Path: {Path}, Id: {Id}",
                item.GetType().Name,
                item.Name ?? "Unknown name",
                metadataPath,
                item.Id);

            try
            {
                Directory.Delete(metadataPath, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting {MetadataPath}", metadataPath);
            }
        }

        // If deleting a primary version video, clear PrimaryVersionId from alternate versions
        // OwnerId check: items with OwnerId set are alternate versions or extras, not primaries
        if (item is Video video && !video.PrimaryVersionId.HasValue && video.OwnerId.Equals(Guid.Empty))
        {
            var localAlternatesInOrder = _linkedChildren.GetLinkedChildrenIds(video.Id, (int)LinkedChildType.LocalAlternateVersion);
            var localAlternateIds = localAlternatesInOrder.ToHashSet();
            var linkedAlternates = _linkedChildren.GetLinkedChildrenIds(video.Id, (int)LinkedChildType.LinkedAlternateVersion)
                .Select(_items.GetItemById)
                .OfType<Video>()
                .OrderBy(alternate => alternate.SortName);
            var allAlternateVersions = localAlternatesInOrder.Select(_items.GetItemById)
                .OfType<Video>()
                .Concat(linkedAlternates)
                .DistinctBy(alternate => alternate.Id);

            // Partition alternates by whether their files still exist on disk
            var alternateVersions = new List<Video>();
            var missingAlternates = new List<Video>();
            foreach (var alt in allAlternateVersions)
            {
                if (!string.IsNullOrEmpty(alt.Path) && !_fileSystem.FileExists(alt.Path))
                {
                    missingAlternates.Add(alt);
                }
                else
                {
                    alternateVersions.Add(alt);
                }
            }

            // Delete alternates whose files no longer exist to avoid ghost items.
            // Clear PrimaryVersionId first so DeleteItem doesn't try to update the primary being deleted.
            foreach (var missing in missingAlternates)
            {
                _logger.LogInformation(
                    "Deleting missing alternate version {Name} ({Path})",
                    missing.Name ?? "Unknown name",
                    missing.Path ?? string.Empty);
                missing.SetPrimaryVersionId(null);
                missing.OwnerId = Guid.Empty;
                missing.LocalAlternateVersions = [];
                missing.LinkedAlternateVersions = [];
                await DeleteCoreAsync(missing, new DeleteOptions { DeleteFileLocation = false }, missing.GetOwner(_items) ?? missing.GetParent(_items), CancellationToken.None).ConfigureAwait(false);
            }

            if (alternateVersions.Count > 0)
            {
                _logger.LogInformation(
                    "Clearing PrimaryVersionId from {Count} alternate versions of {Name}",
                    alternateVersions.Count,
                    item.Name ?? "Unknown name");

                // Promote the first alternate version to be the new primary
                var newPrimary = alternateVersions[0];
                newPrimary.SetPrimaryVersionId(null);
                newPrimary.OwnerId = Guid.Empty;

                // Transfer alternate version arrays from old primary to new primary
                // so UpdateToRepositoryAsync creates correct LinkedChildren entries
                var missingPaths = missingAlternates.Select(alt => alt.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var missingIds = missingAlternates.Select(alt => alt.Id).ToHashSet();
                newPrimary.LocalAlternateVersions = video.LocalAlternateVersions
                    .Where(p => !string.Equals(p, newPrimary.Path, StringComparison.OrdinalIgnoreCase) && !missingPaths.Contains(p))
                    .ToArray();
                newPrimary.LinkedAlternateVersions = video.LinkedAlternateVersions
                    .Where(lc => !lc.ItemId.HasValue || (!lc.ItemId.Value.Equals(newPrimary.Id) && !missingIds.Contains(lc.ItemId.Value)))
                    .ToArray();

                await newPrimary.UpdateToRepositoryAsync(_writes, ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);

                // Re-route playlist/collection references from deleted primary to new primary
                await _writes.RerouteLinkedChildReferencesAsync(video.Id, newPrimary.Id, CancellationToken.None).ConfigureAwait(false);

                // Update remaining alternates to point to new primary
                foreach (var alternate in alternateVersions.Skip(1))
                {
                    alternate.SetPrimaryVersionId(newPrimary.Id);
                    // Only set OwnerId for local alternates; linked alternates are independent items
                    alternate.OwnerId = localAlternateIds.Contains(alternate.Id) ? newPrimary.Id : Guid.Empty;
                    await alternate.UpdateToRepositoryAsync(_writes, ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        else if (item is Video alternateVideo && alternateVideo.PrimaryVersionId.HasValue)
        {
            // If deleting an alternate version, re-route references to its primary
            await _writes.RerouteLinkedChildReferencesAsync(alternateVideo.Id, alternateVideo.PrimaryVersionId.Value, CancellationToken.None).ConfigureAwait(false);

            // Remove deleted alternate from primary's LinkedAlternateVersions
            if (_items.GetItemById(alternateVideo.PrimaryVersionId.Value) is Video primaryVideo)
            {
                primaryVideo.LinkedAlternateVersions = primaryVideo.LinkedAlternateVersions
                    .Where(lc => !lc.ItemId.HasValue || !lc.ItemId.Value.Equals(alternateVideo.Id))
                    .ToArray();
                await primaryVideo.UpdateToRepositoryAsync(_writes, ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
            }
        }

        item.SetParent(null);

        var externalDataManager = _externalDataManagerFactory.Value;
        externalDataManager.DeleteExternalItemFiles(item);
        foreach (var child in children)
        {
            externalDataManager.DeleteExternalItemFiles(child);
        }

        _persistenceService.DeleteItem([item.Id, .. children.Select(f => f.Id)]);
        _itemCacheStore.Remove(item.Id);
        _itemCacheStore.RemoveRange(children.Select(child => child.Id));

        if (parent is Folder folder)
        {
            folder.Children = null;
            folder.UserData = null;
        }

        _changes.Removed(item, parent);
    }

    private void DeleteItemPath(BaseItem item, bool isRequiredForDelete, FileSystemMetadata fileSystemInfo)
    {
        if (_fileSystem.DirectoryExists(fileSystemInfo.FullName) || _fileSystem.FileExists(fileSystemInfo.FullName))
        {
            try
            {
                _logger.LogInformation(
                    "Deleting item path, Type: {Type}, Name: {Name}, Path: {Path}, Id: {Id}",
                    item.GetType().Name,
                    item.Name ?? "Unknown name",
                    fileSystemInfo.FullName,
                    item.Id);

                if (fileSystemInfo.IsDirectory)
                {
                    Directory.Delete(fileSystemInfo.FullName, true);
                }
                else
                {
                    _fileSystem.DeleteFile(fileSystemInfo.FullName);
                }
            }
            catch (DirectoryNotFoundException)
            {
                _logger.LogInformation(
                    "Directory not found, only removing from database, Type: {Type}, Name: {Name}, Path: {Path}, Id: {Id}",
                    item.GetType().Name,
                    item.Name ?? "Unknown name",
                    fileSystemInfo.FullName,
                    item.Id);
            }
            catch (FileNotFoundException)
            {
                _logger.LogInformation(
                    "File not found, only removing from database, Type: {Type}, Name: {Name}, Path: {Path}, Id: {Id}",
                    item.GetType().Name,
                    item.Name ?? "Unknown name",
                    fileSystemInfo.FullName,
                    item.Id);
            }
            catch (IOException)
            {
                if (isRequiredForDelete)
                {
                    throw;
                }
            }
            catch (UnauthorizedAccessException)
            {
                if (isRequiredForDelete)
                {
                    throw;
                }
            }
        }
    }

    private bool IsFileProtocol(BaseItem item)
        => !string.IsNullOrEmpty(item.Path) && _sources.Value.GetPathProtocol(item.Path) == MediaProtocol.File;

    private bool IsChannelItem(BaseItem item)
        => item is Video video
            ? !video.ChannelId.Equals(Guid.Empty) && _recordings.GetActiveRecordingInfo(video.Path) is null
            : item.SourceType == SourceType.Channel;

    private bool IsInternalItem(BaseItem item)
    {
        if (!IsFileProtocol(item))
        {
            return false;
        }

        var pathToCheck = item switch
        {
            Genre => _configurationManager.ApplicationPaths.GenrePath,
            MusicArtist => _configurationManager.ApplicationPaths.ArtistsPath,
            MusicGenre => _configurationManager.ApplicationPaths.MusicGenrePath,
            Person => _configurationManager.ApplicationPaths.PeoplePath,
            Studio => _configurationManager.ApplicationPaths.StudioPath,
            Year => _configurationManager.ApplicationPaths.YearPath,
            _ => null
        };

        var itemPath = item.Path;
        if (!string.IsNullOrEmpty(pathToCheck) && !string.IsNullOrEmpty(itemPath))
        {
            return _fileSystem.AreEqual(pathToCheck, itemPath) || _fileSystem.ContainsSubPath(pathToCheck, itemPath);
        }

        return false;
    }

    private List<string> GetMetadataPaths(BaseItem item, IEnumerable<BaseItem> children)
    {
        var list = GetInternalMetadataPaths(item);
        foreach (var child in children)
        {
            list.AddRange(GetInternalMetadataPaths(child));
        }

        return list;
    }

    private List<string> GetInternalMetadataPaths(BaseItem item)
    {
        var list = new List<string>
        {
            item.GetInternalMetadataPath(_configurationManager.ApplicationPaths.InternalMetadataPath)
        };

        if (item is Video video)
        {
            // Trickplay
            list.Add(_pathManager.GetTrickplayDirectory(video));

            // Chapter Images
            list.Add(_pathManager.GetChapterImageFolderPath(video));

            foreach (var sourceId in GetMediaSourceIds(video))
            {
                var subtitleFolder = _pathManager.GetSubtitleFolderPath(sourceId);
                if (subtitleFolder is not null)
                {
                    list.Add(subtitleFolder);
                }

                var attachmentFolder = _pathManager.GetAttachmentFolderPath(sourceId);
                if (attachmentFolder is not null)
                {
                    list.Add(attachmentFolder);
                }
            }
        }

        return list;
    }

    private IEnumerable<string> GetMediaSourceIds(Video video)
    {
        if (IsChannelItem(video))
        {
            var channelSources = _channels.Value.GetStaticMediaSources(video, CancellationToken.None).ToArray();
            if (channelSources.Length > 0)
            {
                return channelSources.Select(source => source.Id);
            }
        }

        return video.GetCatalogueMediaSourceIds(_items, _linkedChildren)
            .Select(id => id.ToString("N", CultureInfo.InvariantCulture));
    }
}
