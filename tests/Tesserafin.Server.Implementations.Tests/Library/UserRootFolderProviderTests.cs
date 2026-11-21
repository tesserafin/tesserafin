using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.IO;
using Tesserafin.Server.Core.Library;
using Tesserafin.Server.Implementations.Tests.Library.LibraryManager;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

/// <summary>
/// Standalone tests for <see cref="UserRootFolderProvider"/> (PR107): the lazy double-checked-lock
/// cache, the cache-hit lookup path, the resolve-fallback path (bounded replication of
/// <c>LibraryManager.ResolvePath</c> for this exact call, see the class remarks), thread-safety, and
/// the RFC invariant I1/DI-wiring proofs. Cross-checks against the real, delegating
/// <c>LibraryManager</c> facade and the historical <c>ResolvePath</c>-based construction it used to do
/// itself live in <c>LibraryManager/LibraryManagerUserRootFolderProviderTests.cs</c>, since that
/// parity comparison needs a real <c>LibraryManager</c> instance wired with a real resolver.
/// </summary>
/// <remarks>
/// Joins <see cref="LibraryManagerStaticStateFixture"/>'s non-parallel collection: the
/// resolve-fallback path exercises <c>Folder.DeepCopy&lt;Folder, UserRootFolder&gt;()</c> (reflection
/// over every public property, including <c>Children</c>), whose getter reads the process-wide
/// <see cref="BaseItem.ItemRepository"/> static - the same static-mutation hazard the other tests in
/// that collection coordinate around.
/// </remarks>
[Collection(LibraryManagerStaticStateFixture.Name)]
public sealed class UserRootFolderProviderTests : IDisposable
{
    // GetUserRootFolder does a real Directory.CreateDirectory(DefaultUserViewsPath) - matching the
    // historical LibraryManager.GetUserRootFolder exactly (see the class remarks) - so this must be a
    // real, writable path rather than an arbitrary mocked-looking string like "/data/...".
    private readonly string _userRootPath = Path.Combine(Path.GetTempPath(), "reefin-userrootfolder-tests-" + Guid.NewGuid().ToString("N"));

    private readonly Mock<IServerConfigurationManager> _configurationManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<IItemStore> _itemStoreMock;
    private readonly Mock<IItemLookupService> _itemLookupServiceMock;
    private readonly UserRootFolderProvider _provider;

    public UserRootFolderProviderTests()
    {
        // The resolve-fallback path's DeepCopy<Folder, UserRootFolder>() reflects over every public
        // property of the intermediate Folder, including Children - whose getter lazily calls
        // BaseItem.ItemRepository.GetItemList(...) (Folder.cs GetCachedChildren). Stub it to an empty
        // result: this provider never sets Children itself, so this is purely incidental plumbing the
        // reflection-based copy touches, not something UserRootFolderProvider depends on.

        _configurationManagerMock = new Mock<IServerConfigurationManager>();
        _configurationManagerMock.Setup(c => c.ApplicationPaths.DefaultUserViewsPath).Returns(_userRootPath);
        _configurationManagerMock.Setup(c => c.ApplicationPaths.ProgramDataPath).Returns(Path.GetTempPath());
        _configurationManagerMock.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        _configurationManagerMock.Setup(c => c.GetConfiguration("metadata")).Returns(new MetadataConfiguration());

        // Same DeepCopy-reflection hazard as BaseItem.ItemRepository above: BaseItem.SortName's getter
        // (also reflected over by DeepCopy) calls CreateSortName(), which reads
        // BaseItem.ConfigurationManager.Configuration.SortRemoveWords.
        BaseItem.ConfigurationManager = _configurationManagerMock.Object;

        _fileSystemMock = new Mock<IFileSystem>();
        _fileSystemMock
            .Setup(f => f.GetDirectoryInfo(_userRootPath))
            .Returns(new FileSystemMetadata
            {
                FullName = _userRootPath,
                Name = Path.GetFileName(_userRootPath),
                IsDirectory = true,
                Exists = true,
                CreationTimeUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                LastWriteTimeUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc)
            });

        // No default GetNewItemId setup: every test configures exactly the (path, type) pairs it
        // needs (typeof(UserRootFolder) for the cache-lookup id, typeof(Folder) for the
        // resolve-fallback id - see the historical quirk documented on UserRootFolderProvider).
        _itemStoreMock = new Mock<IItemStore>();

        _itemLookupServiceMock = new Mock<IItemLookupService>();

        _provider = new UserRootFolderProvider(
            _configurationManagerMock.Object,
            _fileSystemMock.Object,
            _itemStoreMock.Object,
            _itemLookupServiceMock.Object,
            NullLogger<UserRootFolderProvider>.Instance,
            new Tesserafin.Server.Implementations.Item.ItemReadServices(Mock.Of<IItemRepository>(r => r.GetItemList(It.IsAny<InternalItemsQuery>()) == Array.Empty<BaseItem>()), new Tesserafin.Controller.LiveTv.ActiveRecordings(), new(), Mock.Of<Tesserafin.Controller.IServerApplicationPaths>()));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_userRootPath))
            {
                Directory.Delete(_userRootPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; nothing in the temp dir matters past this test's lifetime.
        }
    }

    // ---------------------------------------------------------------
    // Cache-hit path: an existing UserRootFolder is returned as-is, no resolve fallback.
    // ---------------------------------------------------------------

    [Fact]
    public void GetUserRootFolder_ExistingItemInLookupService_ReturnsCachedItemWithoutResolving()
    {
        var lookupId = Guid.NewGuid();
        var existing = new UserRootFolder { Id = lookupId, Path = _userRootPath, Name = "default" };

        _itemStoreMock
            .Setup(s => s.GetNewItemId(_userRootPath, typeof(UserRootFolder)))
            .Returns(lookupId);
        _itemLookupServiceMock.Setup(l => l.GetItemById(lookupId)).Returns(existing);

        var result = _provider.GetUserRootFolder();

        Assert.Same(existing, result);
        _fileSystemMock.Verify(f => f.GetDirectoryInfo(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void GetUserRootFolder_ExistingItemWrongType_FallsBackToResolve()
    {
        // GetItemById returns a BaseItem that is not a UserRootFolder (e.g. a plain Folder somehow
        // registered under that id) - the historical "as UserRootFolder" cast fails silently and the
        // resolve-fallback branch runs, exactly like LibraryManager.GetUserRootFolder.
        var lookupId = Guid.NewGuid();
        _itemStoreMock
            .Setup(s => s.GetNewItemId(_userRootPath, typeof(UserRootFolder)))
            .Returns(lookupId);
        _itemStoreMock
            .Setup(s => s.GetNewItemId(_userRootPath, typeof(Folder)))
            .Returns(Guid.NewGuid());
        _itemLookupServiceMock.Setup(l => l.GetItemById(lookupId)).Returns(new Folder { Id = lookupId });

        var result = _provider.GetUserRootFolder();

        Assert.IsType<UserRootFolder>(result);
        _fileSystemMock.Verify(f => f.GetDirectoryInfo(_userRootPath), Times.Once);
    }

    [Fact]
    public void GetUserRootFolder_LookupThrows_FallsBackToResolveAndLogsWithoutThrowing()
    {
        var lookupId = Guid.NewGuid();
        _itemStoreMock
            .Setup(s => s.GetNewItemId(_userRootPath, typeof(UserRootFolder)))
            .Returns(lookupId);
        _itemStoreMock
            .Setup(s => s.GetNewItemId(_userRootPath, typeof(Folder)))
            .Returns(Guid.NewGuid());
        _itemLookupServiceMock.Setup(l => l.GetItemById(lookupId)).Throws(new InvalidOperationException("boom"));

        var result = _provider.GetUserRootFolder();

        Assert.IsType<UserRootFolder>(result);
    }

    // ---------------------------------------------------------------
    // Resolve-fallback path: field-by-field replication of the bounded ResolvePath subset (see
    // UserRootFolderProvider's type remarks for the full trace this reproduces).
    // ---------------------------------------------------------------

    [Fact]
    public void GetUserRootFolder_NoExistingItem_ResolvesFolderWithExpectedFields()
    {
        _itemLookupServiceMock.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((BaseItem?)null);
        _itemStoreMock
            .Setup(s => s.GetNewItemId(_userRootPath, typeof(Folder)))
            .Returns(Guid.Parse("11111111-1111-1111-1111-111111111111"));

        var result = _provider.GetUserRootFolder();

        Assert.IsType<UserRootFolder>(result);
        Assert.Equal(_userRootPath, result.Path);
        Assert.Equal(Path.GetFileName(_userRootPath), result.Name);
        Assert.True(result.IsRoot);
        Assert.False(result.IsLocked);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), result.Id);

        // UseFileCreationTimeForDateAdded defaults to true (MetadataConfiguration ctor) -> DateCreated
        // comes from the directory's own CreationTimeUtc, DateModified from LastWriteTimeUtc.
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), result.DateCreated);
        Assert.Equal(new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), result.DateModified);
    }

    [Fact]
    public void GetUserRootFolder_NoExistingItem_IdComputedWithFolderTypeNotUserRootFolderType()
    {
        // Preserved historical quirk (see UserRootFolderProvider.ResolveUserRootFolder remarks):
        // ResolverHelper.SetInitialItemValues computes the id from item.GetType() *before* the
        // outer DeepCopy<Folder, UserRootFolder>() call - i.e. typeof(Folder), not
        // typeof(UserRootFolder). This differs from the id used for the cache lookup itself.
        var lookupId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var resolvedId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        _itemStoreMock
            .Setup(s => s.GetNewItemId(_userRootPath, typeof(UserRootFolder)))
            .Returns(lookupId);
        _itemStoreMock
            .Setup(s => s.GetNewItemId(_userRootPath, typeof(Folder)))
            .Returns(resolvedId);
        _itemLookupServiceMock.Setup(l => l.GetItemById(lookupId)).Returns((BaseItem?)null);

        var result = _provider.GetUserRootFolder();

        Assert.Equal(resolvedId, result.Id);
        Assert.NotEqual(lookupId, result.Id);
    }

    [Fact]
    public void GetUserRootFolder_UseFileCreationTimeForDateAddedDisabled_DateCreatedIsUtcNow()
    {
        _configurationManagerMock.Setup(c => c.GetConfiguration("metadata")).Returns(new MetadataConfiguration { UseFileCreationTimeForDateAdded = false });
        _itemLookupServiceMock.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((BaseItem?)null);

        var before = DateTime.UtcNow;
        var result = _provider.GetUserRootFolder();
        var after = DateTime.UtcNow;

        Assert.InRange(result.DateCreated, before, after);
    }

    [Fact]
    public void GetUserRootFolder_DirectoryCreationTimeIsMinValue_DateCreatedFallsBackToUtcNow()
    {
        _fileSystemMock
            .Setup(f => f.GetDirectoryInfo(_userRootPath))
            .Returns(new FileSystemMetadata
            {
                FullName = _userRootPath,
                Name = Path.GetFileName(_userRootPath),
                IsDirectory = true,
                Exists = true,
                CreationTimeUtc = DateTime.MinValue,
                LastWriteTimeUtc = DateTime.MinValue
            });
        _itemLookupServiceMock.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((BaseItem?)null);

        var before = DateTime.UtcNow;
        var result = _provider.GetUserRootFolder();
        var after = DateTime.UtcNow;

        Assert.InRange(result.DateCreated, before, after);
    }

    // ---------------------------------------------------------------
    // Caching: two calls return the exact same instance, and no re-resolution happens.
    // ---------------------------------------------------------------

    [Fact]
    public void GetUserRootFolder_CalledTwice_ReturnsSameInstanceAndResolvesOnlyOnce()
    {
        _itemLookupServiceMock.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((BaseItem?)null);

        var first = _provider.GetUserRootFolder();
        var second = _provider.GetUserRootFolder();

        Assert.Same(first, second);
        _fileSystemMock.Verify(f => f.GetDirectoryInfo(_userRootPath), Times.Once);
    }

    [Fact]
    public void GetUserRootFolder_CalledTwice_CachedItemFromLookup_ReturnsSameInstanceAndLooksUpOnlyOnce()
    {
        var lookupId = Guid.NewGuid();
        var existing = new UserRootFolder { Id = lookupId, Path = _userRootPath, Name = "default" };
        _itemStoreMock
            .Setup(s => s.GetNewItemId(_userRootPath, typeof(UserRootFolder)))
            .Returns(lookupId);
        _itemLookupServiceMock.Setup(l => l.GetItemById(lookupId)).Returns(existing);

        var first = _provider.GetUserRootFolder();
        var second = _provider.GetUserRootFolder();

        Assert.Same(first, second);
        _itemLookupServiceMock.Verify(l => l.GetItemById(lookupId), Times.Once);
    }

    // ---------------------------------------------------------------
    // Basic thread-safety: concurrent first-access calls must all observe the same instance and the
    // resolve path must only run once (double-checked locking).
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetUserRootFolder_ConcurrentFirstAccess_AllCallersObserveSameInstance()
    {
        _itemLookupServiceMock.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((BaseItem?)null);

        var tasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => _provider.GetUserRootFolder()))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Same(results[0], r));
        _fileSystemMock.Verify(f => f.GetDirectoryInfo(_userRootPath), Times.Once);
    }

    // ---------------------------------------------------------------
    // Path-moved tail: if the cached item's Path differs from the current DefaultUserViewsPath, it is
    // patched in place rather than triggering re-resolution (matches historical behavior - the branch
    // only fires on the resolve-fallback result, whose Path is always freshly set to the current path,
    // so this is characterized against the cache-hit path where a stale Path can actually occur).
    // ---------------------------------------------------------------

    [Fact]
    public void GetUserRootFolder_CachedItemHasStalePath_PathIsResetInPlace()
    {
        var lookupId = Guid.NewGuid();
        var existing = new UserRootFolder { Id = lookupId, Path = "/old/moved/path", Name = "default" };
        _itemStoreMock
            .Setup(s => s.GetNewItemId(_userRootPath, typeof(UserRootFolder)))
            .Returns(lookupId);
        _itemLookupServiceMock.Setup(l => l.GetItemById(lookupId)).Returns(existing);

        var result = _provider.GetUserRootFolder();

        Assert.Same(existing, result);
        Assert.Equal(_userRootPath, result.Path);
    }

    // ---------------------------------------------------------------
    // Guard: cache-hit lookup id is computed with typeof(UserRootFolder), never
    // typeof(Folder) - the inverse of the resolve-fallback quirk above.
    // ---------------------------------------------------------------

    [Fact]
    public void GetUserRootFolder_LookupUsesUserRootFolderType()
    {
        _itemLookupServiceMock.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((BaseItem?)null);

        _provider.GetUserRootFolder();

        _itemStoreMock.Verify(s => s.GetNewItemId(_userRootPath, typeof(UserRootFolder)), Times.Once);
    }

    // ---------------------------------------------------------------
    // DI wiring (RFC I1): IUserRootFolderProvider must resolve to the autonomous
    // UserRootFolderProvider class, not a factory casting ILibraryManager (the historical
    // ApplicationHost.cs:568 alias, RFC §0/§1's "piège n°1").
    // ---------------------------------------------------------------

    [Fact]
    public void DiWiring_ConstructorGraph_NoEagerEdgeToSccMembers()
    {
        var forbiddenSccTypes = new[]
        {
            typeof(ILibraryManager),
            typeof(IUserViewManager),
            typeof(IChannelManager),
            typeof(ILiveTvManager)
        };

        var ctor = typeof(UserRootFolderProvider).GetConstructors().Single();
        var directParameterTypes = ctor.GetParameters().Select(p => p.ParameterType).ToArray();

        foreach (var forbidden in forbiddenSccTypes)
        {
            Assert.DoesNotContain(forbidden, directParameterTypes);
        }
    }

    [Fact]
    public void DiWiring_ApplicationHostStyleRegistration_UserRootFolderProviderResolvesToAutonomousImplementation()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_configurationManagerMock.Object);
        services.AddSingleton(_fileSystemMock.Object);
        services.AddSingleton(_itemStoreMock.Object);
        services.AddSingleton(_itemLookupServiceMock.Object);
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<UserRootFolderProvider>>(NullLogger<UserRootFolderProvider>.Instance);
        services.AddSingleton(Mock.Of<Tesserafin.Controller.Library.IItemReadServices>());
        services.AddSingleton<IUserRootFolderProvider, UserRootFolderProvider>();

        using var provider = services.BuildServiceProvider();

        var resolved = provider.GetRequiredService<IUserRootFolderProvider>();

        Assert.IsType<UserRootFolderProvider>(resolved);
    }

    [Fact]
    public void DiWiring_ApplicationHostStyleRegistration_UserRootFolderProviderIsSingleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_configurationManagerMock.Object);
        services.AddSingleton(_fileSystemMock.Object);
        services.AddSingleton(_itemStoreMock.Object);
        services.AddSingleton(_itemLookupServiceMock.Object);
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<UserRootFolderProvider>>(NullLogger<UserRootFolderProvider>.Instance);
        services.AddSingleton(Mock.Of<Tesserafin.Controller.Library.IItemReadServices>());
        services.AddSingleton<IUserRootFolderProvider, UserRootFolderProvider>();

        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IUserRootFolderProvider>();
        var second = provider.GetRequiredService<IUserRootFolderProvider>();

        Assert.Same(first, second);
    }
}
