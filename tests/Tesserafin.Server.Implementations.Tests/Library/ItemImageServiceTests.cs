using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Threading.Tasks;
using System.Threading;
using System;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Drawing;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Drawing;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

public sealed class ItemImageServiceTests
{
    [Fact]
    public async Task PrepareLocalMetadata_LeavesPublishedObjectsUntouched_AndCommitSavesBeforeCacheAndEvent()
    {
        using var context = new Context();
        var item = context.LocalItem();
        var original = item.ImageInfos;
        var saved = false;
        var notified = 0;
        context.Persistence.Setup(p => p.SaveImagesAsync(item.Id, It.IsAny<IReadOnlyList<ItemImageInfo>>(), It.IsAny<CancellationToken>())).Callback(() =>
        {
            Assert.Same(original, item.ImageInfos);
            Assert.Equal(0, item.ImageInfos[0].Width);
            saved = true;
        }).ReturnsAsync(true);
        context.Cache.OnRegister = value =>
        {
            Assert.True(saved);
            Assert.Equal(20, value.ImageInfos[0].Width);
        };
        context.Changes.ItemUpdated += (_, _) => notified++;
        await using var update = await context.Service.PrepareRefreshAsync(item, false, true, TestContext.Current.CancellationToken);
        Assert.Same(original, item.ImageInfos);
        Assert.Equal(0, original[0].Width);
        Assert.Equal(20, update.Images[0].Width);
        Assert.False(saved);
        await update.CommitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(30, item.ImageInfos[0].Height);
        Assert.Equal("blur", item.ImageInfos[0].BlurHash);
        Assert.Equal(1, context.Cache.Registered);
        Assert.Equal(1, notified);
        Assert.True(File.Exists(context.Media));
    }

    [Fact]
    public async Task Commit_ParentLookupFailure_DoesNotFailCommittedImages()
    {
        using var context = new Context();
        var item = context.LocalItem();
        await using var update = await context.Service.PrepareRefreshAsync(item, false, true, TestContext.Current.CancellationToken);
        item.ParentId = Guid.NewGuid();
        context.Lookup.Setup(l => l.GetItemById(item.ParentId)).Throws(new InvalidOperationException("parent lookup unavailable"));
        var notifications = 0;
        context.Changes.ItemUpdated += (_, _) => notifications++;

        await update.CommitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(20, item.ImageInfos[0].Width);
        Assert.Equal(1, context.Cache.Registered);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task RemoteLocalization_UsesOwnedAtomicFile_AndNeverReentersItemPersistence()
    {
        using var context = new Context();
        var item = context.RemoteItem();
        var original = item.ImageInfos;
        await using var update = await context.Service.PrepareLocalImageAsync(item, original[0], 0, true, TestContext.Current.CancellationToken);
        var path = Assert.Single(update.Images).Path;
        Assert.True(File.Exists(path));
        Assert.StartsWith(context.Metadata, path, StringComparison.Ordinal);
        Assert.True(Assert.Single(update.Images).IsLocalFile);
        Assert.Same(original, item.ImageInfos);
        context.Persistence.Verify(p => p.SaveImagesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ItemImageInfo>>(), It.IsAny<CancellationToken>()), Times.Never);
        await update.CommitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(path, update.Image!.Path);
        Assert.Equal(path, item.ImageInfos[0].Path);
        Assert.Equal(new byte[] { 1, 3, 5, 7 }, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(context.Metadata, "*.tmp", SearchOption.AllDirectories));
        Assert.True(File.Exists(context.Media));
    }

    [Fact]
    public async Task FailedCommit_PreservesPublishedImageAndCache_CleansStagedFiles_AndReleasesAdmission()
    {
        using var context = new Context();
        var item = context.RemoteItem();
        var original = item.ImageInfos;
        var failure = new IOException("image row write failed");
        context.Persistence.Setup(p => p.SaveImagesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ItemImageInfo>>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        var update = await context.Service.PrepareLocalImageAsync(item, original[0], 0, true, TestContext.Current.CancellationToken);
        var path = update.Image!.Path;
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => update.CommitAsync(TestContext.Current.CancellationToken)));
        await update.DisposeAsync();
        Assert.Same(original, item.ImageInfos);
        Assert.False(File.Exists(path));
        Assert.Equal(0, context.Cache.Registered);
        context.Persistence.Setup(p => p.SaveImagesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ItemImageInfo>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await using var retry = await context.Service.PrepareLocalImageAsync(item, original[0], 0, true, TestContext.Current.CancellationToken);
        await retry.CommitAsync(TestContext.Current.CancellationToken);
        Assert.True(File.Exists(item.ImageInfos[0].Path));
    }

    [Fact]
    public async Task DownloadCancellation_LeavesRemoteImageAndDatabaseUntouched_AndAllowsRetry()
    {
        using var context = new Context();
        var item = context.RemoteItem();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        context.Remote.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(async (string _, CancellationToken token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new RemoteImageContent("image/png", new byte[] { 9 });
        });
        var prepare = context.Service.PrepareLocalImageAsync(item, item.ImageInfos[0], 0, true, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepare);
        Assert.False(item.ImageInfos[0].IsLocalFile);
        Assert.Equal(0, context.Cache.Registered);
        context.Persistence.Verify(p => p.SaveImagesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ItemImageInfo>>(), It.IsAny<CancellationToken>()), Times.Never);
        context.SetDownload();
        await using var retry = await context.Service.PrepareLocalImageAsync(item, item.ImageInfos[0], 0, true, TestContext.Current.CancellationToken);
        await retry.CommitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task QueuedCancellation_DoesNotProcessImages_AndDiscardDoesNotPublishMetadata()
    {
        using var context = new Context();
        var item = context.LocalItem();
        var first = await context.Service.PrepareRefreshAsync(item, false, false, TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var queued = context.Service.PrepareRefreshAsync(item, false, false, cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        context.Processor.Verify(p => p.GetImageDimensions(It.IsAny<string>()), Times.Once);
        await first.DisposeAsync();
        Assert.Equal(0, item.ImageInfos[0].Width);
        Assert.Equal(0, context.Cache.Registered);
        await using var retry = await context.Service.PrepareRefreshAsync(item, false, false, TestContext.Current.CancellationToken);
        await retry.CommitAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitCancellation_RespectsTheDatabasePublicationBoundary(bool afterDatabaseCommit)
    {
        using var context = new Context();
        var item = context.RemoteItem();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var update = await context.Service.PrepareLocalImageAsync(item, item.ImageInfos[0], 0, true, cancellation.Token);
        var path = update.Image!.Path;
        if (afterDatabaseCommit)
        {
            context.Persistence.Setup(p => p.SaveImagesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ItemImageInfo>>(), It.IsAny<CancellationToken>())).Callback(() => cancellation.Cancel()).ReturnsAsync(true);
            await update.CommitAsync(cancellation.Token);
        }
        else
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update.CommitAsync(cancellation.Token));
        }

        await update.DisposeAsync();
        Assert.Equal(afterDatabaseCommit, File.Exists(path));
        Assert.Equal(afterDatabaseCommit, item.ImageInfos[0].IsLocalFile);
        Assert.Equal(afterDatabaseCommit ? 1 : 0, context.Cache.Registered);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    public async Task PermanentRejection_RemovalIsPrepared_AndPersistedOnlyWhenRequested(HttpStatusCode status, bool remove)
    {
        using var context = new Context();
        var item = context.RemoteItem();
        context.Remote.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("rejected", null, status));
        await using var update = await context.Service.PrepareLocalImageAsync(item, item.ImageInfos[0], 0, remove, TestContext.Current.CancellationToken);
        Assert.Single(item.ImageInfos);
        await Assert.ThrowsAsync<InvalidOperationException>(() => update.CommitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(remove ? 0 : 1, item.ImageInfos.Length);
        Assert.Equal(remove ? 1 : 0, context.Cache.Registered);
        context.Persistence.Verify(p => p.SaveImagesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ItemImageInfo>>(), It.IsAny<CancellationToken>()), remove ? Times.Once : Times.Never);
    }

    [Fact]
    public async Task AlternateUrls_TryNextAfterRejection_ButTransientFailurePreservesTheOriginalErrorAndSlot()
    {
        using var context = new Context();
        var item = context.RemoteItem();
        item.ImageInfos[0].Path = "https://image.invalid/first.png|https://image.invalid/second.png";
        context.Remote.Setup(r => r.GetAsync("https://image.invalid/first.png", It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("not found", null, HttpStatusCode.NotFound));
        await using (var update = await context.Service.PrepareLocalImageAsync(item, item.ImageInfos[0], 0, true, TestContext.Current.CancellationToken))
        {
            await update.CommitAsync(TestContext.Current.CancellationToken);
        }

        context.Remote.Verify(r => r.GetAsync("https://image.invalid/second.png", It.IsAny<CancellationToken>()), Times.Once);
        var other = context.RemoteItem();
        var failure = new HttpRequestException("server unavailable", null, HttpStatusCode.ServiceUnavailable);
        context.Remote.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<HttpRequestException>(() => context.Service.PrepareLocalImageAsync(other, other.ImageInfos[0], 0, true, TestContext.Current.CancellationToken)));
        Assert.False(other.ImageInfos[0].IsLocalFile);
        context.SetDownload();
        await using var retry = await context.Service.PrepareLocalImageAsync(other, other.ImageInfos[0], 0, true, TestContext.Current.CancellationToken);
        await retry.CommitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MissingAndCorruptLocalImages_ArePrunedOrInvalidated_WithoutChangingSourceBytes()
    {
        using var context = new Context();
        var item = context.LocalItem();
        item.ImageInfos = [.. item.ImageInfos, new ItemImageInfo { Path = Path.Combine(context.Root, "missing.png"), Type = ImageType.Backdrop }];
        var path = item.ImageInfos[0].Path;
        context.Processor.Setup(p => p.GetImageDimensions(path)).Throws(new IOException("corrupt image"));
        context.Processor.Setup(p => p.GetImageBlurHash(path, It.IsAny<ImageDimensions>())).Throws(new InvalidOperationException("corrupt image"));
        await using var update = await context.Service.PrepareRefreshAsync(item, true, true, TestContext.Current.CancellationToken);
        Assert.Equal(2, item.ImageInfos.Length);
        await update.CommitAsync(TestContext.Current.CancellationToken);
        var kept = Assert.Single(item.ImageInfos);
        Assert.Equal(0, kept.Width);
        Assert.Equal(0, kept.Height);
        Assert.Equal(string.Empty, kept.BlurHash);
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(context.Media));
    }

    [Fact]
    public async Task ConflictingImageEdit_RejectsBeforePersistence_AndCleansItsOwnFile()
    {
        using var context = new Context();
        var item = context.RemoteItem();
        var update = await context.Service.PrepareLocalImageAsync(item, item.ImageInfos[0], 0, true, TestContext.Current.CancellationToken);
        var staged = update.Image!.Path;
        item.ImageInfos = [new ItemImageInfo { Path = "https://image.invalid/new.png", Type = ImageType.Primary }];
        var concurrent = item.ImageInfos;
        await Assert.ThrowsAsync<IOException>(() => update.CommitAsync(TestContext.Current.CancellationToken));
        await update.DisposeAsync();
        Assert.Same(concurrent, item.ImageInfos);
        Assert.False(File.Exists(staged));
        context.Persistence.Verify(p => p.SaveImagesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ItemImageInfo>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConcurrentDispose_DrainsAnAdmittedCommitBeforeReleasingFilesAndAdmission()
    {
        using var context = new Context();
        var item = context.RemoteItem();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Persistence.Setup(p => p.SaveImagesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ItemImageInfo>>(), It.IsAny<CancellationToken>())).Returns(release.Task);
        var update = await context.Service.PrepareLocalImageAsync(item, item.ImageInfos[0], 0, true, TestContext.Current.CancellationToken);
        var path = update.Image!.Path;
        var commit = update.CommitAsync(TestContext.Current.CancellationToken);
        var dispose = update.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        Assert.True(File.Exists(path));
        release.SetResult(true);
        await Task.WhenAll(commit, dispose).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(File.Exists(path));
        Assert.True(item.ImageInfos[0].IsLocalFile);
        await using var next = await context.Service.PrepareRefreshAsync(item, false, false, TestContext.Current.CancellationToken);
        await next.CommitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void ImageOwner_HasNoManagerProviderOrRecursiveWriteDependency_AndOldFacadesAreGone()
    {
        var owner = typeof(ItemImageService);
        var parameters = owner.GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.DoesNotContain(typeof(ILibraryManager), parameters);
        Assert.DoesNotContain(typeof(IProviderManager), parameters);
        Assert.DoesNotContain(typeof(Lazy<IProviderManager>), parameters);
        var manager = typeof(Tesserafin.Server.Core.Library.LibraryManager);
        foreach (var method in new[] { "UpdateImagesAsync", "ConvertImageToLocal" })
        {
            Assert.Null(manager.GetMethod(method));
            Assert.Null(typeof(ILibraryManager).GetMethod(method));
        }

        var dependencies = manager.GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.Contains(typeof(IItemWriteCommands), dependencies);
        Assert.DoesNotContain(typeof(IUserDataManager), dependencies);
        Assert.DoesNotContain(typeof(Tesserafin.Controller.MediaEncoding.IMediaEncoder), dependencies);
        Assert.DoesNotContain(typeof(IImageProcessor), dependencies);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tesserafin.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var source = File.ReadAllText(Path.Combine(directory.FullName, "Tesserafin.Server.Core/Library/ItemImageService.cs"));
        foreach (var forbidden in new[] { "ILibraryManager", "IProviderManager", "UpdateToRepositoryAsync", "SaveItems(", "BaseItem.", "GetAwaiter()" })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }
    }

    private sealed class Context : IDisposable
    {
        public Context()
        {
            Metadata = Path.Combine(Root, "metadata");
            Media = Path.Combine(Root, "source.mp4");
            File.WriteAllBytes(Media, [7, 5, 3, 1]);
            var configuration = new Mock<IServerConfigurationManager>();
            configuration.Setup(c => c.ApplicationPaths.InternalMetadataPath).Returns(Metadata);
            var files = new Mock<IFileSystem>();
            files.Setup(f => f.FileExists(It.IsAny<string>())).Returns((string path) => File.Exists(path));
            files.Setup(f => f.GetLastWriteTimeUtc(It.IsAny<string>())).Returns((string path) => File.GetLastWriteTimeUtc(path));
            Processor.Setup(p => p.GetImageDimensions(It.IsAny<string>())).Returns(new ImageDimensions(20, 30));
            Processor.Setup(p => p.GetImageBlurHash(It.IsAny<string>(), It.IsAny<ImageDimensions>())).Returns("blur");
            Persistence.Setup(p => p.SaveImagesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ItemImageInfo>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            SetDownload();
            Service = new ItemImageService(Processor.Object, Remote.Object, configuration.Object, files.Object, Lookup.Object, new ActiveRecordings(), Persistence.Object, Cache, NullLogger<ItemImageService>.Instance, Changes);
        }

        public string Root { get; } = Directory.CreateTempSubdirectory("image-unit-").FullName;

        public string Metadata { get; }

        public string Media { get; }

        public Mock<IItemLookupService> Lookup { get; } = new();

        public Mock<IImageProcessor> Processor { get; } = new();

        public Mock<IRemoteImageSource> Remote { get; } = new();

        public Mock<IItemPersistenceService> Persistence { get; } = new();

        public RecordingCache Cache { get; } = new();

        public ItemChangeEvents Changes { get; } = new(NullLogger<ItemChangeEvents>.Instance);

        public ItemImageService Service { get; }

        public NoRecursiveMovie LocalItem()
        {
            var item = RemoteItem();
            var path = Path.Combine(Root, Guid.NewGuid() + ".png");
            File.WriteAllBytes(path, [1, 2, 3]);
            item.ImageInfos[0].Path = path;
            return item;
        }

        public NoRecursiveMovie RemoteItem() => new() { Id = Guid.NewGuid(), Name = "film", Path = Media, ImageInfos = [new ItemImageInfo { Path = "https://image.invalid/poster.png", Type = ImageType.Primary }] };

        public void SetDownload() => Remote.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new RemoteImageContent("image/png", new byte[] { 1, 3, 5, 7 }));

        public void Dispose()
        {
            Service.Dispose();
            Directory.Delete(Root, true);
        }
    }

    private sealed class RecordingCache : IItemCacheStore
    {
        public int Registered { get; private set; }

        public Action<BaseItem>? OnRegister { get; set; }

        public void Register(BaseItem item)
        {
            OnRegister?.Invoke(item);
            Registered++;
        }

        public void Remove(Guid id)
        {
        }

        public void RemoveRange(IEnumerable<Guid> ids)
        {
        }
    }

    private sealed class NoRecursiveMovie : Movie
    {
        public override Task UpdateToRepositoryAsync(IItemWriteCommands writes, ItemUpdateType updateReason, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Image processing reentered whole-item persistence.");
    }
}
