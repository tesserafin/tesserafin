using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Data.Enums;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

public sealed class ItemWriteCommandsTests
{
    [Fact]
    public void Creation_PreparesOneTypedMultipartAlternate_ThenSavesCachesAndNotifies()
    {
        var context = new Context();
        var primary = context.Movie();
        var alternate = context.Alternate(primary);
        primary.LocalAlternateVersions = [alternate.Path, alternate.Path, string.Empty];
        var saved = false;
        context.Persistence.Setup(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>())).Callback((IReadOnlyList<BaseItem> graph, CancellationToken _) =>
        {
            Assert.Empty(context.Cache.Registered);
            Assert.Equal(2, graph.Count);
            Assert.Equal(primary.Id, graph[1].OwnerId);
            Assert.Equal(primary.Id, Assert.IsType<Movie>(graph[1]).PrimaryVersionId);
            Assert.Single(Assert.IsType<Movie>(graph[1]).AdditionalParts);
            saved = true;
        });
        context.Cache.BeforeRegister = () => Assert.True(saved);
        var notifications = 0;
        context.Events.ItemAdded += (_, args) =>
        {
            if (saved && context.Cache.Registered.Count == 2 && ReferenceEquals(primary, args.Item))
            {
                notifications++;
            }
        };

        context.Commands.CreateItems([primary], null, TestContext.Current.CancellationToken);

        Assert.Equal(1, notifications);
        context.Resolution.Verify(r => r.ResolveAlternateVersion(alternate.Path, typeof(Movie), null, null), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationFailure_DoesNotExportSaveOrPublish(bool wrongType)
    {
        var context = new Context();
        var primary = context.Movie();
        var alternate = context.Alternate(primary);
        primary.LocalAlternateVersions = [alternate.Path];
        if (wrongType)
        {
            context.Resolution.Setup(r => r.ResolveAlternateVersion(alternate.Path, typeof(Movie), null, null)).Returns(new Video { Id = alternate.Id });
        }
        else
        {
            context.Resolution.Setup(r => r.ResolveAlternateVersion(alternate.Path, typeof(Movie), null, null)).Throws(new IOException("resolution failed"));
        }

        await Assert.ThrowsAnyAsync<Exception>(() => context.Commands.UpdateItemAsync(primary, null, ItemUpdateType.MetadataEdit, TestContext.Current.CancellationToken));

        Assert.Equal(DateTime.UnixEpoch, primary.DateLastSaved);
        Assert.Empty(context.Cache.Registered);
        context.Persistence.Verify(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()), Times.Never);
        context.Metadata.Verify(m => m.SaveAsync(It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Fact]
    public async Task FailedUpdate_KeepsOwnedDatesAndPropagatedVersionMetadataUnpublished_ThenRetries()
    {
        var context = new Context();
        var primary = context.Movie();
        var alternate = context.Movie();
        primary.Overview = "new description";
        alternate.Overview = "old description";
        context.Lookup.Setup(l => l.GetItemById(alternate.Id)).Returns(alternate);
        context.Linked.Setup(l => l.GetLinkedChildrenIds(primary.Id, (int)Tesserafin.Controller.Entities.LinkedChildType.LocalAlternateVersion)).Returns([alternate.Id]);
        var notifications = 0;
        context.Events.ItemUpdated += (_, _) => notifications++;
        context.Persistence.Setup(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>())).Callback((IReadOnlyList<BaseItem> graph, CancellationToken _) =>
        {
            Assert.Equal(2, graph.Count);
            Assert.All(graph, item => Assert.Equal("new description", item.Overview));
            Assert.Equal("old description", alternate.Overview);
            Assert.Equal(DateTime.UnixEpoch, primary.DateLastSaved);
            Assert.Equal(DateTime.UnixEpoch, alternate.DateLastSaved);
            throw new InvalidOperationException("SQL write rejected");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Commands.UpdateItemAsync(primary, null, ItemUpdateType.MetadataEdit, TestContext.Current.CancellationToken));
        Assert.Equal("old description", alternate.Overview);
        Assert.Equal(DateTime.UnixEpoch, primary.DateLastSaved);
        Assert.Empty(context.Cache.Registered);
        Assert.Equal(0, notifications);

        context.Persistence.Setup(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()));
        await context.Commands.UpdateItemAsync(primary, null, ItemUpdateType.MetadataEdit, TestContext.Current.CancellationToken);
        Assert.Equal("new description", alternate.Overview);
        Assert.NotEqual(DateTime.UnixEpoch, primary.DateLastSaved);
        Assert.Equal(2, context.Cache.Registered.Count);
        Assert.Equal(2, notifications);
    }

    [Fact]
    public async Task Update_PreparesAnExistingPathVersionBeforeItsFirstLinkedRowExists()
    {
        var context = new Context();
        var primary = context.Movie();
        var alternate = context.Alternate(primary);
        primary.LocalAlternateVersions = [alternate.Path];
        primary.Overview = "new metadata";
        context.Lookup.Setup(l => l.GetItemById(alternate.Id)).Returns(alternate);

        await context.Commands.UpdateItemAsync(primary, null, ItemUpdateType.MetadataEdit, TestContext.Current.CancellationToken);

        Assert.Equal("new metadata", alternate.Overview);
        context.Persistence.Verify(p => p.SaveItems(It.Is<IReadOnlyList<BaseItem>>(graph => graph.Count == 2), It.IsAny<CancellationToken>()), Times.Once);
        context.Resolution.Verify(r => r.ResolveAlternateVersion(It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<Folder>(), It.IsAny<CollectionType?>()), Times.Never);
    }

    [Fact]
    public async Task BulkGuideRefresh_DoesNotIssueAPerProgramVersionQuery()
    {
        var context = new Context();

        await context.Commands.UpdateItemsAsync([new LiveTvProgram { Id = Guid.NewGuid(), Name = "guide program" }], null, ItemUpdateType.MetadataImport, TestContext.Current.CancellationToken);

        context.Linked.Verify(l => l.GetLinkedChildrenIds(It.IsAny<Guid>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public void CacheFailure_EvictsTheSavedGraph_WithoutNotification()
    {
        var context = new Context();
        var primary = context.Movie();
        var alternate = context.Alternate(primary);
        primary.LocalAlternateVersions = [alternate.Path];
        context.Cache.BeforeRegister = () => throw new InvalidOperationException("cache failure");
        var notifications = 0;
        context.Events.ItemAdded += (_, _) => notifications++;

        Assert.Throws<InvalidOperationException>(() => context.Commands.CreateItems([primary], null, TestContext.Current.CancellationToken));

        Assert.Equal(0, notifications);
        Assert.Equal(new[] { primary.Id, alternate.Id }, context.Cache.Removed);
        context.Persistence.Verify(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void DuplicateIdsAndCancelledAdmission_DoNotWrite()
    {
        var context = new Context();
        var primary = context.Movie();
        Assert.Throws<ArgumentException>(() => context.Commands.CreateItems([primary, primary], null, TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => context.Commands.CreateItems([primary], null, cancelled.Token));

        context.Persistence.Verify(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(context.Cache.Registered);
    }

    [Fact]
    public void Ownership_HasNoManagerOrProviderDependency_AndOldFacadesAreRemoved()
    {
        foreach (var owner in new[] { typeof(ItemWriteCommands), typeof(ItemVersionGraphPlanner), typeof(ItemResolutionService), typeof(LibraryItemOptions) })
        {
            var dependencies = owner.GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();
            Assert.DoesNotContain(typeof(ILibraryManager), dependencies);
            Assert.DoesNotContain(typeof(Lazy<ILibraryManager>), dependencies);
            Assert.DoesNotContain(typeof(IProviderManager), dependencies);
            Assert.DoesNotContain(typeof(Lazy<IProviderManager>), dependencies);
        }

        foreach (var name in new[] { "CreateItem", "CreateItems", "UpdateItemAsync", "UpdateItemsAsync", "ReattachUserDataAsync", "RunMetadataSavers" })
        {
            Assert.Null(typeof(ILibraryManager).GetMethod(name));
            Assert.Null(typeof(Tesserafin.Server.Core.Library.LibraryManager).GetMethod(name));
        }

        Assert.Null(typeof(IProviderManager).GetMethod("SaveMetadataAsync", [typeof(BaseItem), typeof(ItemUpdateType)]));
    }

    private sealed class Context
    {
        public Context()
        {
            Linked.Setup(l => l.GetLinkedChildrenIds(It.IsAny<Guid>(), It.IsAny<int?>())).Returns([]);
            Metadata.Setup(m => m.SaveAsync(It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>>())).Returns(Task.CompletedTask);
            Commands = new ItemWriteCommands(new ItemVersionGraphPlanner(Resolution.Object, Mock.Of<ILibraryItemOptions>(), Lookup.Object), Persistence.Object, Cache, Lookup.Object, Linked.Object, Metadata.Object, Events, new ActiveRecordings(), NullLogger<ItemWriteCommands>.Instance);
        }

        public Mock<IItemResolutionService> Resolution { get; } = new();

        public Mock<IItemLookupService> Lookup { get; } = new();

        public Mock<ILinkedChildrenService> Linked { get; } = new();

        public Mock<IItemPersistenceService> Persistence { get; } = new();

        public Mock<IItemMetadataExporter> Metadata { get; } = new();

        public RecordingCache Cache { get; } = new();

        public ItemChangeEvents Events { get; } = new(NullLogger<ItemChangeEvents>.Instance);

        public ItemWriteCommands Commands { get; }

        public Movie Movie() => new() { Id = Guid.NewGuid(), Name = "write test", Path = "/media/" + Guid.NewGuid() + ".mkv", DateLastSaved = DateTime.UnixEpoch };

        public Movie Alternate(Movie primary)
        {
            var alternate = Movie();
            alternate.AdditionalParts = [alternate.Path + ".part2.mkv"];
            Resolution.Setup(r => r.GetNewItemId(alternate.Path, primary.GetType())).Returns(alternate.Id);
            Resolution.Setup(r => r.ResolveAlternateVersion(alternate.Path, primary.GetType(), null, null)).Returns(alternate);
            return alternate;
        }
    }

    private sealed class RecordingCache : IItemCacheStore
    {
        public List<BaseItem> Registered { get; } = [];

        public List<Guid> Removed { get; } = [];

        public Action? BeforeRegister { get; set; }

        public void Register(BaseItem item)
        {
            BeforeRegister?.Invoke();
            Registered.Add(item);
        }

        public void Remove(Guid id) => Removed.Add(id);

        public void RemoveRange(IEnumerable<Guid> ids) => Removed.AddRange(ids);
    }
}
