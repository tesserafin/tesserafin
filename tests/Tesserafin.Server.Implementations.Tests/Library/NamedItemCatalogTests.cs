using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.IO;
using Tesserafin.Server.Core.Library;
using Tesserafin.Server.Core.Library.Validators;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

public sealed class NamedItemCatalogTests
{
    [Fact]
    public void AbsentReadsAndIds_DoNotCreateDirectoriesRowsOrNotifications()
    {
        using var context = new Context();

        Assert.Null(context.Catalog.Find<Studio>("studio"));
        Assert.Null(context.Catalog.Find<Genre>("genre"));
        Assert.Null(context.Catalog.Find<MusicGenre>("music"));
        Assert.Null(context.Catalog.Find<MusicArtist>("artist"));
        Assert.Null(context.Catalog.Find<Year>("2026"));
        Assert.NotEqual(Guid.Empty, context.Catalog.GetId<Studio>("studio"));
        Assert.NotEqual(Guid.Empty, context.Catalog.GetId<Genre>("genre"));
        Assert.NotEqual(Guid.Empty, context.Catalog.GetId<MusicGenre>("music"));
        Assert.NotEqual(Guid.Empty, context.Catalog.GetId<MusicArtist>("artist"));

        Assert.Empty(Directory.EnumerateFileSystemEntries(context.Root));
        Assert.Empty(context.Stored);
        context.Persistence.VerifyNoOtherCalls();
        context.Events.VerifyNoOtherCalls();
    }

    [Fact]
    public void ExplicitCreation_SharesReadIdentity_AndRepeatedCallsPublishOnce()
    {
        using var context = new Context();
        AssertCreated<Studio>("studio");
        AssertCreated<Genre>("genre");
        AssertCreated<MusicGenre>("music");
        AssertCreated<MusicArtist>("artist");
        AssertCreated<Year>("2026");
        Assert.Equal(5, context.Stored.Count);
        context.Persistence.Verify(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()), Times.Exactly(5));
        context.Events.Verify(e => e.Added(It.IsAny<BaseItem>(), null), Times.Exactly(5));

        void AssertCreated<T>(string name)
            where T : BaseItem, new()
        {
            var id = context.Catalog.GetId<T>(name);
            var item = context.Commands.GetOrCreateNamedItem<T>(name, TestContext.Current.CancellationToken);
            Assert.Equal(id, item.Id);
            Assert.Equal(name, item.Name);
            Assert.True(Directory.Exists(item.Path));
            Assert.NotEqual(default, item.DateCreated);
            Assert.Equal(id, context.Catalog.Find<T>(name)!.Id);
            Assert.Equal(id, context.Commands.GetOrCreateNamedItem<T>(name, TestContext.Current.CancellationToken).Id);
        }
    }

    [Fact]
    public void ArtistLookupAndMaterialization_PreferARealFolder_WithoutWriting()
    {
        using var context = new Context();
        var named = new MusicArtist { Id = Guid.NewGuid(), Name = "artist" };
        var folder = new MusicArtist { Id = Guid.NewGuid(), ParentId = Guid.NewGuid(), Name = "artist" };
        context.Stored[named.Id] = named;
        context.Stored[folder.Id] = folder;

        Assert.Equal(folder.Id, context.Catalog.Find<MusicArtist>("artist")!.Id);
        Assert.Equal(folder.Id, context.Catalog.GetId<MusicArtist>("artist"));
        Assert.Equal(folder.Id, context.Commands.GetOrCreateNamedItem<MusicArtist>("artist", TestContext.Current.CancellationToken).Id);

        context.Persistence.VerifyNoOtherCalls();
        context.Events.VerifyNoOtherCalls();
        Assert.Empty(Directory.EnumerateFileSystemEntries(context.Root));
    }

    [Fact]
    public void FailedPersistence_DoesNotPublishAnItem_AndTheSameIdentityCanBeRetried()
    {
        using var context = new Context();
        context.Persistence.Setup(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>())).Throws(new IOException("save rejected"));
        var id = context.Catalog.GetId<Genre>("retry");

        Assert.Throws<IOException>(() => context.Commands.GetOrCreateNamedItem<Genre>("retry", TestContext.Current.CancellationToken));

        Assert.Empty(context.Stored);
        Assert.Null(context.Catalog.Find<Genre>("retry"));
        context.Events.VerifyNoOtherCalls();
        // The managed directory is outside the SQL transaction and can remain for a retry.
        Assert.True(Directory.Exists(context.Identity.GetPath<Genre>("retry")));
        context.AcceptWrites();
        Assert.Equal(id, context.Commands.GetOrCreateNamedItem<Genre>("retry", TestContext.Current.CancellationToken).Id);
        context.Events.Verify(e => e.Added(It.IsAny<BaseItem>(), null), Times.Once);
    }

    [Fact]
    public async Task ConcurrentMaterialization_PublishesOneRowAndOneAddedNotification()
    {
        using var context = new Context();
        var cancellationToken = TestContext.Current.CancellationToken;

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(
            () => context.Commands.GetOrCreateNamedItem<Genre>("shared", cancellationToken),
            cancellationToken)));

        Assert.Single(results.Select(item => item.Id).Distinct());
        Assert.Single(context.Stored);
        context.Persistence.Verify(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()), Times.Once);
        context.Events.Verify(e => e.Added(It.IsAny<BaseItem>(), null), Times.Once);
    }

    [Fact]
    public async Task CancelledAdmission_CreatesNeitherDirectoryNorRow()
    {
        using var context = new Context();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Assert.ThrowsAny<OperationCanceledException>(() => context.Commands.GetOrCreateNamedItem<Studio>("cancelled", cancellation.Token));

        Assert.Empty(Directory.EnumerateFileSystemEntries(context.Root));
        context.Persistence.VerifyNoOtherCalls();
        context.Events.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void UnusablePaths_AreRejectedBeforeMaterialization(string name)
    {
        using var context = new Context();

        Assert.ThrowsAny<ArgumentException>(() => context.Commands.GetOrCreateNamedItem<Studio>(name, TestContext.Current.CancellationToken));

        Assert.Empty(Directory.EnumerateFileSystemEntries(context.Root));
        context.Persistence.VerifyNoOtherCalls();
    }

    [Fact]
    public void Identities_RespectNormalizationAndCasePolicy_AndYearsAreCanonical()
    {
        using var context = new Context();
        context.Configuration.Configuration.EnableCaseSensitiveItemIds = true;
        context.Configuration.Configuration.EnableNormalizedItemByNameIds = true;
        Assert.Equal(context.Catalog.GetId<Genre>("Name"), context.Catalog.GetId<Genre>("name"));
        Assert.Equal(context.Catalog.GetId<Genre>(" Name. "), context.Catalog.GetId<Genre>("Name"));
        context.Configuration.Configuration.EnableNormalizedItemByNameIds = false;
        Assert.NotEqual(context.Catalog.GetId<Genre>("Name"), context.Catalog.GetId<Genre>("name"));
        Assert.Equal(context.Catalog.GetId<Year>("002026"), context.Catalog.GetId<Year>("2026"));
        Assert.Throws<ArgumentOutOfRangeException>(() => context.Catalog.Find<Year>("0"));
        Assert.Throws<ArgumentOutOfRangeException>(() => context.Catalog.Find<Year>("-1"));
        Assert.Throws<ArgumentOutOfRangeException>(() => context.Catalog.Find<Year>("year"));
        Assert.Throws<ArgumentException>(() => context.Commands.GetOrCreateNamedItem<Video>("unsupported", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PostScan_ExplicitlyMaterializesYears_AndHonorsCancellation()
    {
        using var context = new Context();
        context.Repository.Setup(r => r.GetProductionYearsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 2025, 2026 });
        var task = new YearsPostScanTask(context.Repository.Object, context.Commands);

        await task.Run(Mock.Of<IProgress<double>>(), TestContext.Current.CancellationToken);

        Assert.NotNull(context.Catalog.Find<Year>("2025"));
        Assert.NotNull(context.Catalog.Find<Year>("2026"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.Run(Mock.Of<IProgress<double>>(), cancellation.Token));
        context.Persistence.Verify(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public void Ownership_RemovesManagerFactoriesAndWriterDependency_AndKeepsTheCatalogReadOnly()
    {
        foreach (var owner in new[] { typeof(ILibraryManager), typeof(Tesserafin.Server.Core.Library.LibraryManager) })
        {
            var names = new[] { "CreateItemByName", "GetArtist", "GetGenre", "GetMusicGenre", "GetStudio", "GetYear", "GetGenreId", "GetMusicGenreId", "GetStudioId" };
            Assert.DoesNotContain(owner.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), method => names.Contains(method.Name));
        }

        Assert.DoesNotContain(typeof(Tesserafin.Server.Core.Library.LibraryManager).GetConstructors().Single().GetParameters(), p => p.ParameterType == typeof(IItemWriteCommands));
        foreach (var owner in new[] { typeof(NamedItemCatalog), typeof(NamedItemIdentity) })
        {
            var dependencies = owner.GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();
            Assert.DoesNotContain(typeof(ILibraryManager), dependencies);
            Assert.DoesNotContain(typeof(IProviderManager), dependencies);
            Assert.DoesNotContain(typeof(IItemWriteCommands), dependencies);
            Assert.DoesNotContain(typeof(IServiceProvider), dependencies);
        }
    }

    private sealed class Context : IDisposable
    {
        public Context()
        {
            var configuration = new Mock<IServerConfigurationManager>();
            configuration.SetupGet(c => c.Configuration).Returns(new ServerConfiguration());
            configuration.SetupGet(c => c.ApplicationPaths.ProgramDataPath).Returns(Root);
            configuration.SetupGet(c => c.ApplicationPaths.StudioPath).Returns(Path.Combine(Root, "studios"));
            configuration.SetupGet(c => c.ApplicationPaths.GenrePath).Returns(Path.Combine(Root, "genres"));
            configuration.SetupGet(c => c.ApplicationPaths.MusicGenrePath).Returns(Path.Combine(Root, "music"));
            configuration.SetupGet(c => c.ApplicationPaths.ArtistsPath).Returns(Path.Combine(Root, "artists"));
            configuration.SetupGet(c => c.ApplicationPaths.YearPath).Returns(Path.Combine(Root, "years"));
            Configuration = configuration.Object;
            var files = new Mock<IFileSystem>();
            files.Setup(f => f.GetValidFilename(It.IsAny<string>())).Returns((string name) => name);
            Identity = new NamedItemIdentity(Configuration, files.Object);
            Repository.Setup(r => r.RetrieveItem(It.IsAny<Guid>())).Returns((Guid id) => Stored.GetValueOrDefault(id)!);
            var lookup = new ItemLookupService(Repository.Object, Configuration);
            var links = new Mock<ILinkedChildrenService>();
            links.Setup(l => l.FindArtists(It.IsAny<IReadOnlyList<string>>())).Returns(() => Stored.Values.OfType<MusicArtist>()
                .GroupBy(artist => artist.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase));
            Catalog = new NamedItemCatalog(Identity, lookup, links.Object);
            Commands = new ItemWriteCommands(new ItemVersionGraphPlanner(Mock.Of<IItemResolutionService>(), Mock.Of<ILibraryItemOptions>(), lookup, links.Object), Persistence.Object, lookup, lookup, links.Object, Mock.Of<IItemMetadataExporter>(), files.Object, Events.Object, new ActiveRecordings(), Catalog, Identity, Mock.Of<IItemResolutionService>(), Mock.Of<IItemRepository>(), Mock.Of<ILibraryItemOptions>(), new FolderChildReconciler(lookup, Mock.Of<IFileSystem>(), NullLogger<FolderChildReconciler>.Instance), NullLogger<ItemWriteCommands>.Instance);
            AcceptWrites();
        }

        public string Root { get; } = Directory.CreateTempSubdirectory("named-catalog-").FullName;

        public Dictionary<Guid, BaseItem> Stored { get; } = new();

        public Mock<IItemPersistenceService> Persistence { get; } = new();

        public Mock<IItemRepository> Repository { get; } = new();

        public Mock<IItemChangePublisher> Events { get; } = new();

        public IServerConfigurationManager Configuration { get; }

        public NamedItemIdentity Identity { get; }

        public NamedItemCatalog Catalog { get; }

        public ItemWriteCommands Commands { get; }

        public void AcceptWrites() => Persistence.Setup(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<BaseItem> items, CancellationToken _) =>
            {
                foreach (var item in items)
                {
                    Stored[item.Id] = item;
                }
            });

        public void Dispose() => Directory.Delete(Root, true);
    }
}
