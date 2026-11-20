using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using AutoFixture.AutoMoq;
using Moq;
using Tesserafin.Controller;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.MediaSegments;
using Tesserafin.Controller.Persistence;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

public sealed class ItemDeletionCommandsTests
{
    [Fact]
    public async Task RequiredSourceFailure_LeavesCatalogueCacheParentAndAlternatesUntouched_ThenAllowsRetry()
    {
        using var context = new Context();
        var item = context.Video();
        var notifications = 0;
        context.Changes.ItemRemoved += (_, _) => notifications++;
        var alternate = context.Video();
        alternate.SetPrimaryVersionId(item.Id);
        context.Library.Setup(l => l.GetLocalAlternateVersionIds(item)).Returns([alternate.Id]);
        context.Items.Setup(i => i.GetItemById(alternate.Id)).Returns(alternate);
        var parent = new Folder { Id = Guid.NewGuid() };
        item.ParentId = parent.Id;
        context.Files.Setup(f => f.DeleteFile(item.Path)).Throws(new IOException("required source is locked"));
        await Assert.ThrowsAsync<IOException>(() => context.Commands.DeleteAsync(item, new DeleteOptions { DeleteFileLocation = true }, parent, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(item.Path));
        Assert.Equal(parent.Id, item.ParentId);
        Assert.Equal(item.Id, alternate.PrimaryVersionId);
        Assert.False(alternate.Saved);
        Assert.Equal(0, notifications);
        context.Persistence.Verify(p => p.DeleteItem(It.IsAny<IReadOnlyList<Guid>>()), Times.Never);
        Assert.Empty(context.Cache.Removed);
        context.Writes.Verify(w => w.RerouteLinkedChildReferencesAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        context.Files.Setup(f => f.DeleteFile(item.Path)).Callback(() => File.Delete(item.Path));
        await context.Commands.DeleteAsync(item, new DeleteOptions { DeleteFileLocation = true }, parent, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(item.Path));
        Assert.True(File.Exists(alternate.Path));
        Assert.Null(alternate.PrimaryVersionId);
        Assert.True(alternate.Saved);
        Assert.Equal(1, notifications);
        context.Writes.Verify(w => w.RerouteLinkedChildReferencesAsync(item.Id, alternate.Id, CancellationToken.None), Times.Once);
        context.Persistence.Verify(p => p.DeleteItem(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(item.Id))), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogueOnlyDeletion_PreservesSourceBytes_AndInvalidatesAfterPersistence(bool bulk)
    {
        using var context = new Context();
        var item = context.Video();
        var original = await File.ReadAllBytesAsync(item.Path, TestContext.Current.CancellationToken);
        var persisted = false;
        context.Persistence.Setup(p => p.DeleteItem(It.IsAny<IReadOnlyList<Guid>>())).Callback(() => persisted = true);
        context.Cache.OnMutation = () => Assert.True(persisted);
        var notifications = 0;
        context.Changes.ItemRemoved += (_, _) =>
        {
            if (persisted && context.Cache.Removed.Contains(item.Id))
            {
                notifications++;
            }
        };
        if (bulk)
        {
            await context.Commands.DeleteUnreferencedAsync([item], false, TestContext.Current.CancellationToken);
        }
        else
        {
            await context.Commands.DeleteAsync(item, new DeleteOptions { DeleteFileLocation = false }, null, TestContext.Current.CancellationToken);
        }

        Assert.True(persisted);
        Assert.Equal(bulk ? 0 : 1, notifications);
        Assert.Equal(original, await File.ReadAllBytesAsync(item.Path, TestContext.Current.CancellationToken));
        context.Files.Verify(f => f.DeleteFile(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task PrimaryDeletion_PromotesSurvivor_DropsMissingAlternate_AndRepointsRemainingVersion()
    {
        using var context = new Context();
        var item = context.Video();
        var survivor = context.Video();
        var remaining = context.Video();
        var missing = context.Video();
        File.Delete(missing.Path);
        foreach (var alternate in new[] { survivor, remaining, missing })
        {
            alternate.OwnerId = item.Id;
            alternate.SetPrimaryVersionId(item.Id);
            context.Items.Setup(i => i.GetItemById(alternate.Id)).Returns(alternate);
        }

        item.LocalAlternateVersions = [survivor.Path, remaining.Path, missing.Path];
        context.Library.Setup(l => l.GetLocalAlternateVersionIds(item)).Returns([survivor.Id, remaining.Id, missing.Id]);
        await context.Commands.DeleteAsync(item, new DeleteOptions { DeleteFileLocation = false }, null, TestContext.Current.CancellationToken);
        Assert.Null(survivor.PrimaryVersionId);
        Assert.Equal(Guid.Empty, survivor.OwnerId);
        Assert.Equal(survivor.Id, remaining.PrimaryVersionId);
        Assert.Equal(survivor.Id, remaining.OwnerId);
        Assert.True(survivor.Saved);
        Assert.True(remaining.Saved);
        Assert.DoesNotContain(survivor.Path, survivor.LocalAlternateVersions);
        Assert.DoesNotContain(missing.Path, survivor.LocalAlternateVersions);
        context.Writes.Verify(w => w.RerouteLinkedChildReferencesAsync(item.Id, survivor.Id, CancellationToken.None), Times.Once);
        context.Persistence.Verify(p => p.DeleteItem(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(missing.Id))), Times.Once);
        Assert.True(File.Exists(survivor.Path));
        Assert.True(File.Exists(remaining.Path));
    }

    [Fact]
    public async Task QueuedCancellation_DoesNoWork_AndCancellationAfterAdmissionFinishesReconciliationWithoutMutatingOptions()
    {
        using var context = new Context();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = context.Video();
        item.ChannelId = Guid.NewGuid();
        using var firstCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        context.Channels.Setup(c => c.DeleteItem(item)).Returns(async () =>
        {
            entered.SetResult();
            await release.Task;
        });
        var options = new DeleteOptions { DeleteFileLocation = true, DeleteFromExternalProvider = true };
        var first = context.Commands.DeleteAsync(item, options, null, firstCancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var queuedItem = context.Video();
        using var queuedCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var queued = context.Commands.DeleteAsync(queuedItem, options, null, queuedCancellation.Token);
        try
        {
            await queuedCancellation.CancelAsync();
            await firstCancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
        finally
        {
            release.SetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        Assert.True(options.DeleteFileLocation);
        Assert.True(File.Exists(item.Path));
        Assert.True(File.Exists(queuedItem.Path));
        context.Persistence.Verify(p => p.DeleteItem(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(item.Id))), Times.Once);
        context.Persistence.Verify(p => p.DeleteItem(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(queuedItem.Id))), Times.Never);
    }

    [Fact]
    public async Task ObsoleteGenericAlternate_IsCleanedExplicitly_WithoutDeletingSource()
    {
        using var context = new Context();
        var obsolete = context.Video();
        var correctedId = Guid.NewGuid();
        context.Library.Setup(l => l.GetNewItemId(obsolete.Path, typeof(Video))).Returns(obsolete.Id);
        context.Library.Setup(l => l.GetNewItemId(obsolete.Path, typeof(Tesserafin.Controller.Entities.Movies.Movie))).Returns(correctedId);
        context.Items.Setup(l => l.GetItemById(obsolete.Id)).Returns(obsolete);
        await context.Commands.DeleteObsoleteAlternateAsync(obsolete.Path, typeof(Tesserafin.Controller.Entities.Movies.Movie), TestContext.Current.CancellationToken);
        Assert.True(File.Exists(obsolete.Path));
        context.Persistence.Verify(p => p.DeleteItem(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(obsolete.Id))), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InternalEntityCleanup_UsesPathContainment_NotSiblingPrefix(bool insideRoot)
    {
        using var context = new Context();
        var file = context.Video().Path;
        var root = System.IO.Path.GetDirectoryName(file)!;
        var managedRoot = insideRoot ? root : root[..^1];
        var item = new StubGenre { Id = Guid.NewGuid(), Name = "genre", Path = file };
        context.Configuration.Setup(c => c.ApplicationPaths.GenrePath).Returns(managedRoot);
        context.Files.Setup(f => f.ContainsSubPath(managedRoot, file)).Returns(insideRoot);
        await context.Commands.DeleteAsync(item, new DeleteOptions { DeleteFileLocation = false }, null, TestContext.Current.CancellationToken);
        Assert.Equal(!insideRoot, File.Exists(file));
        context.Files.Verify(f => f.ContainsSubPath(managedRoot, file), Times.Once);
    }

    [Fact]
    public void ManagerAndEntities_HaveNoDeletionFacadeOrGlobalService()
    {
        var manager = typeof(Tesserafin.Server.Core.Library.LibraryManager);
        foreach (var name in new[] { "DeleteItem", "DeleteItemsUnsafeFast" })
        {
            Assert.Null(manager.GetMethod(name));
            Assert.Null(typeof(ILibraryManager).GetMethod(name));
        }

        var dependencies = manager.GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.Contains(typeof(IItemWriteCommands), dependencies);
        Assert.DoesNotContain(typeof(IItemDeletionCommands), dependencies);
        Assert.DoesNotContain(typeof(Lazy<IExternalDataManager>), dependencies);
        Assert.DoesNotContain(typeof(Tesserafin.Controller.IO.IPathManager), dependencies);
        Assert.DoesNotContain(typeof(BaseItem).GetProperties(), p => p.PropertyType == typeof(IItemDeletionCommands));
        Assert.DoesNotContain(typeof(BaseItem).GetFields(), f => f.FieldType == typeof(IItemDeletionCommands));
    }

    private sealed class Context : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("deletion-unit-").FullName;

        public Context()
        {
            var fixture = new Fixture().Customize(new AutoMoqCustomization());
            fixture.Register<IItemChangePublisher>(() => Changes);
            Configuration = fixture.Freeze<Mock<IServerConfigurationManager>>();
            Configuration.SetupGet(c => c.ApplicationPaths).Returns(Mock.Of<IServerApplicationPaths>());
            Library = fixture.Freeze<Mock<ILibraryManager>>();
            Writes = fixture.Freeze<Mock<IItemWriteCommands>>();
            Writes.Setup(w => w.RerouteLinkedChildReferencesAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            Items = fixture.Freeze<Mock<IItemLookupService>>();
            Persistence = fixture.Freeze<Mock<IItemPersistenceService>>();
            fixture.Inject<IItemCacheStore>(Cache);
            Files = fixture.Freeze<Mock<IFileSystem>>();
            Files.Setup(f => f.FileExists(It.IsAny<string>())).Returns((string path) => File.Exists(path));
            Files.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns((string path) => Directory.Exists(path));
            Files.Setup(f => f.DeleteFile(It.IsAny<string>())).Callback((string path) => File.Delete(path));
            Channels = fixture.Freeze<Mock<IChannelManager>>();
            fixture.Register(() => new Lazy<IChannelManager>(() => Channels.Object));
            fixture.Register(() => new Lazy<IMediaSourceManager>(() => Mock.Of<IMediaSourceManager>()));
            fixture.Register(() => new Lazy<IMediaSegmentManager>(() => Mock.Of<IMediaSegmentManager>()));
            fixture.Register(() => new Lazy<IExternalDataManager>(() => Mock.Of<IExternalDataManager>()));
            fixture.Inject<Microsoft.Extensions.Logging.ILogger<ItemDeletionCommands>>(Microsoft.Extensions.Logging.Abstractions.NullLogger<ItemDeletionCommands>.Instance);
            Commands = fixture.Create<ItemDeletionCommands>();
        }

        public ItemChangeEvents Changes { get; } = new(Microsoft.Extensions.Logging.Abstractions.NullLogger<ItemChangeEvents>.Instance);

        public Mock<IServerConfigurationManager> Configuration { get; }

        public Mock<ILibraryManager> Library { get; }

        public Mock<IItemWriteCommands> Writes { get; }

        public Mock<IItemLookupService> Items { get; }

        public Mock<IItemPersistenceService> Persistence { get; }

        public RecordingCache Cache { get; } = new();

        public Mock<IFileSystem> Files { get; }

        public Mock<IChannelManager> Channels { get; }

        public ItemDeletionCommands Commands { get; }

        public StubVideo Video()
        {
            var id = Guid.NewGuid();
            var path = System.IO.Path.Combine(_root, id + ".mp4");
            File.WriteAllBytes(path, [1, 3, 5, 7]);
            return new StubVideo(System.IO.Path.Combine(_root, "metadata-" + id)) { Id = id, Name = "video", Path = path };
        }

        public void Dispose()
        {
            Commands.Dispose();
            Directory.Delete(_root, true);
        }
    }

    private sealed class RecordingCache : IItemCacheStore
    {
        public List<Guid> Removed { get; } = [];

        public Action? OnMutation { get; set; }

        public void Register(BaseItem item) => throw new InvalidOperationException("Deletion must not register items.");

        public void Remove(Guid id)
        {
            OnMutation?.Invoke();
            Removed.Add(id);
        }

        public void RemoveRange(IEnumerable<Guid> ids)
        {
            OnMutation?.Invoke();
            Removed.AddRange(ids);
        }
    }

    private sealed class StubGenre : Genre
    {
        public override string GetInternalMetadataPath(string basePath) => Path + ".metadata";

        public override IEnumerable<FileSystemMetadata> GetDeletePaths() => [new FileSystemMetadata { FullName = Path }];
    }

    private sealed class StubVideo(string metadata) : Video
    {
        public override SourceType SourceType => ChannelId.Equals(Guid.Empty) ? SourceType.Library : SourceType.Channel;

        public bool Saved { get; private set; }

        public override string GetInternalMetadataPath(string basePath) => metadata;

        public override IEnumerable<FileSystemMetadata> GetDeletePaths() => [new FileSystemMetadata { FullName = Path }];

        public override IReadOnlyList<MediaSourceInfo> GetMediaSources(bool enablePathSubstitution, IMediaSourceManager mediaSourceManager, IMediaSegmentManager mediaSegmentManager, IChannelManager channelManager) => [];

        public override Task UpdateToRepositoryAsync(IItemWriteCommands writes, ItemUpdateType updateReason, CancellationToken cancellationToken)
        {
            Assert.False(cancellationToken.IsCancellationRequested);
            Saved = true;
            return Task.CompletedTask;
        }
    }
}
