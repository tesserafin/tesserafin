using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Drawing;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Drawing;
using Tesserafin.Model.IO;
using Tesserafin.Model.Net;

namespace Tesserafin.Server.Core.Library;

/// <summary>Prepares detached image snapshots and owns their explicit commit/discard lifecycle.</summary>
internal sealed class ItemImageService(
    IImageProcessor processor,
    IRemoteImageSource remote,
    IServerConfigurationManager configuration,
    IFileSystem files,
    IItemLookupService lookup,
    ActiveRecordings recordings,
    IItemPersistenceService persistence,
    IItemCacheStore cache,
    ILogger<ItemImageService> logger) : IItemImageService, IDisposable
{
    private readonly AsyncKeyedLocker<Guid> _updates = new();
    private readonly IItemPersistenceService _persistence = persistence;
    private readonly IItemCacheStore _cache = cache;
    private readonly IItemLookupService _lookup = lookup;
    private readonly ILogger<ItemImageService> _logger = logger;

    public event EventHandler<ItemChangeEventArgs>? ImagesChanged;

    public async Task<IItemImageUpdate> PrepareRefreshAsync(BaseItem item, bool forceUpdate, bool notify, CancellationToken cancellationToken)
    {
        var update = await AdmitAsync(item, notify, cancellationToken).ConfigureAwait(false);
        try
        {
            var parent = item.GetParent(_lookup);
            if (!IsLibraryItem(item) && parent is not null && !IsLibraryItem(parent))
            {
                return update;
            }

            foreach (var image in update.Draft.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!forceUpdate && !NeedsRefresh(image))
                {
                    continue;
                }

                var current = image;
                if (!image.IsLocalFile)
                {
                    try
                    {
                        current = await LocalizeAsync(item, image, update.CreatedFiles, cancellationToken).ConfigureAwait(false);
                        if (current is null)
                        {
                            update.Draft.Remove(image);
                            continue;
                        }

                        update.Draft[update.Draft.IndexOf(image)] = current;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
                    {
                        _logger.LogWarning(ex, "Unable to localize image for item {ItemId}", item.Id);
                        continue;
                    }
                }

                if (!files.FileExists(current.Path))
                {
                    update.Draft.Remove(current);
                    continue;
                }

                Enrich(item, current);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return update;
        }
        catch
        {
            await update.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IItemImageUpdate> PrepareLocalImageAsync(BaseItem item, ItemImageInfo image, int imageIndex, bool removeOnFailure, CancellationToken cancellationToken)
    {
        var update = await AdmitAsync(item, true, cancellationToken).ConfigureAwait(false);
        try
        {
            var current = update.Draft.Where(info => info.Type == image.Type).ElementAtOrDefault(imageIndex)
                ?? throw new ArgumentException("The image no longer belongs to this item.", nameof(image));
            update.SelectedType = image.Type;
            update.SelectedIndex = imageIndex;
            if (!current.IsLocalFile)
            {
                var local = await LocalizeAsync(item, current, update.CreatedFiles, cancellationToken).ConfigureAwait(false);
                if (local is null)
                {
                    if (removeOnFailure)
                    {
                        update.Draft.Remove(current);
                    }

                    update.Failure = new InvalidOperationException("Unable to convert any images to local");
                    return update;
                }

                update.Draft[update.Draft.IndexOf(current)] = local;
                current = local;
            }

            Enrich(item, current);
            cancellationToken.ThrowIfCancellationRequested();
            return update;
        }
        catch
        {
            await update.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void Dispose() => _updates.Dispose();

    private static ItemImageInfo Copy(ItemImageInfo image)
        => new() { Path = image.Path, Type = image.Type, Width = image.Width, Height = image.Height, BlurHash = image.BlurHash, DateModified = image.DateModified };

    private static bool Equal(IReadOnlyList<ItemImageInfo> first, IReadOnlyList<ItemImageInfo> second)
        => first.Count == second.Count && first.Zip(second).All(pair =>
            pair.First.Path == pair.Second.Path && pair.First.Type == pair.Second.Type
            && pair.First.Width == pair.Second.Width && pair.First.Height == pair.Second.Height
            && pair.First.BlurHash == pair.Second.BlurHash && pair.First.DateModified == pair.Second.DateModified);

    private async Task<PreparedUpdate> AdmitAsync(BaseItem item, bool notify, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var admission = await _updates.LockAsync(item.Id, cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new PreparedUpdate(this, item, item.ImageInfos.Select(Copy).ToArray(), admission!, notify);
        }
        catch
        {
            admission?.Dispose();
            throw;
        }
    }

    private bool IsLibraryItem(BaseItem item)
        => item is not LiveTvProgram and not LiveTvChannel
            && item.ChannelId.Equals(Guid.Empty)
            && (item is not Video || recordings.GetActiveRecordingInfo(item.Path) is null);

    private bool NeedsRefresh(ItemImageInfo image)
    {
        if (!image.IsLocalFile || image.Width == 0 || image.Height == 0 || string.IsNullOrEmpty(image.BlurHash))
        {
            return true;
        }

        try
        {
            return image.DateModified.Subtract(files.GetLastWriteTimeUtc(image.Path)).Duration().TotalSeconds > 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Cannot read image modification date");
            return false;
        }
    }

    private async Task<ItemImageInfo?> LocalizeAsync(BaseItem item, ItemImageInfo image, List<string> createdFiles, CancellationToken cancellationToken)
    {
        foreach (var url in image.Path.Split('|'))
        {
            RemoteImageContent content;
            try
            {
                content = await remote.GetAsync(url, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var extension = MimeTypes.ToExtension(content.ContentType);
            if (string.IsNullOrWhiteSpace(extension) || extension.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            {
                throw new InvalidOperationException("Unknown image content type.");
            }

            // Localization is an owned cache, not a rewrite of images next to source media.
            var directory = Path.Combine(item.GetInternalMetadataPath(configuration.ApplicationPaths.InternalMetadataPath), "images");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + extension);
            var temporary = path + ".tmp";
            createdFiles.Add(temporary);
            createdFiles.Add(path);
            await File.WriteAllBytesAsync(temporary, content.Bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path);
            return new ItemImageInfo { Path = path, Type = image.Type, DateModified = files.GetLastWriteTimeUtc(path) };
        }

        return null;
    }

    private void Enrich(BaseItem item, ItemImageInfo image)
    {
        ImageDimensions size;
        try
        {
            // The string overload always reads the file, including on a forced refresh.
            size = processor.GetImageDimensions(image.Path);
            image.Width = size.Width;
            image.Height = size.Height;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Cannot read image dimensions for item {ItemId}", item.Id);
            size = default;
            image.Width = 0;
            image.Height = 0;
        }

        try
        {
            image.BlurHash = processor.GetImageBlurHash(image.Path, size);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Cannot compute image BlurHash for item {ItemId}", item.Id);
            image.BlurHash = string.Empty;
        }

        try
        {
            image.DateModified = files.GetLastWriteTimeUtc(image.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Cannot read image modification date for item {ItemId}", item.Id);
        }
    }

    private sealed class PreparedUpdate(ItemImageService owner, BaseItem item, ItemImageInfo[] original, IDisposable admission, bool notify) : IItemImageUpdate
    {
        private readonly ItemImageInfo[] _publishedReference = item.ImageInfos;
        private readonly object _lifecycle = new();
        private int _state;
        private Task? _commit;
        private Task? _dispose;
        private bool _committed;

        public List<ItemImageInfo> Draft { get; } = original.Select(Copy).ToList();

        public List<string> CreatedFiles { get; } = [];

        public Exception? Failure { get; set; }

        public Tesserafin.Model.Entities.ImageType? SelectedType { get; set; }

        public int SelectedIndex { get; set; }

        public IReadOnlyList<ItemImageInfo> Images => Draft.Select(Copy).ToArray();

        public ItemImageInfo? Image => Draft.Where(image => image.Type == SelectedType).ElementAtOrDefault(SelectedIndex) is { } image ? Copy(image) : null;

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            lock (_lifecycle)
            {
                if (_state != 0)
                {
                    throw new InvalidOperationException("This image update has already been used or disposed.");
                }

                _state = 1;
                _commit = CommitCoreAsync(cancellationToken);
                return _commit;
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_lifecycle)
            {
                _state = 2;
                _dispose ??= DisposeCoreAsync(_commit);
                return new ValueTask(_dispose);
            }
        }

        private async Task CommitCoreAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(item.ImageInfos, _publishedReference) || !Equal(item.ImageInfos, original))
            {
                throw new IOException("Images changed while this update was being prepared.");
            }

            var changed = !Equal(Draft, original);
            if (changed)
            {
                if (!await owner._persistence.SaveImagesAsync(item.Id, Draft, cancellationToken).ConfigureAwait(false))
                {
                    owner._cache.Remove(item.Id);
                    throw new IOException("The item was removed before its images could be saved.");
                }

                // Do not observe cancellation between a committed transaction and cache publication.
                _committed = true;
                item.ImageInfos = Draft.Select(Copy).ToArray();
            }

            if (changed)
            {
                try
                {
                    owner._cache.Register(item);
                }
                catch
                {
                    owner._cache.Remove(item.Id);
                    throw;
                }
            }

            if (changed && notify && owner.IsLibraryItem(item))
            {
                try
                {
                    owner.ImagesChanged?.Invoke(owner, new ItemChangeEventArgs { Item = item, Parent = item.GetParent(owner._lookup), UpdateReason = ItemUpdateType.ImageUpdate });
                }
                catch (Exception ex)
                {
                    owner._logger.LogError(ex, "Error in image change event handler");
                }
            }

            if (Failure is not null)
            {
                ExceptionDispatchInfo.Capture(Failure).Throw();
            }
        }

        private async Task DisposeCoreAsync(Task? commit)
        {
            if (commit is not null)
            {
                try
                {
                    await commit.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The commit task owns its error; disposal only releases its files and slot.
                }
            }

            foreach (var path in CreatedFiles)
            {
                if (_committed && Draft.Any(image => image.Path == path))
                {
                    continue;
                }

                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    owner._logger.LogWarning(ex, "Unable to remove uncommitted image file");
                }
            }

            admission.Dispose();
        }
    }
}
