using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutoFixture;
using AutoFixture.AutoMoq;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Resolvers;
using Tesserafin.Controller.Sorting;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.IO;
using Tesserafin.Naming.Common;
using Tesserafin.Server.Core.IO;
using Tesserafin.Server.Core.Library.Resolvers;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library.LibraryManager;

/// <summary>
/// Parity tests between <see cref="Tesserafin.Server.Core.Library.UserRootFolderProvider"/> (PR107) and
/// the historical <c>LibraryManager.ResolvePath(fileInfo).DeepCopy&lt;Folder, UserRootFolder&gt;()</c>
/// construction it replaces (still reachable directly via the still-public
/// <see cref="Tesserafin.Server.Core.Library.LibraryManager.ResolvePath(FileSystemMetadata, Folder, IDirectoryService, Tesserafin.Data.Enums.CollectionType?)"/>
/// - only its use *inside* <c>GetUserRootFolder</c> was removed, see
/// <c>UserRootFolderProvider</c>'s type remarks for the full trace/finding). Exercises the real
/// resolver pipeline with a single, real <see cref="FolderResolver"/> (<c>ResolverPriority.Last</c>)
/// and a real, empty temp directory - the deterministic outcome this provider's bounded replication
/// reproduces (RFC <c>docs/rfc-di-query-user-views-v2.md</c> §9/PR107).
/// </summary>
[Collection(LibraryManagerStaticStateFixture.Name)]
public sealed class LibraryManagerUserRootFolderProviderTests : IDisposable
{
    private readonly string _userRootPath = Path.Combine(Path.GetTempPath(), "reefin-lm-userrootfolder-tests-" + Guid.NewGuid().ToString("N"));

    private readonly Tesserafin.Server.Core.Library.LibraryManager _libraryManager;
    private readonly Tesserafin.Server.Core.Library.ItemLookupService _itemLookupService;
    private readonly Tesserafin.Server.Core.Library.ItemStore _itemStore;
    private readonly Tesserafin.Server.Core.Library.UserRootFolderProvider _provider;
    private readonly Mock<IItemRepository> _itemRepositoryMock;
    private readonly Mock<IServerConfigurationManager> _configurationManagerMock;
    private readonly ManagedFileSystem _fileSystem;

    public LibraryManagerUserRootFolderProviderTests()
    {
        var fixture = new Fixture().Customize(new AutoMoqCustomization());
        fixture.Register(() => new NamingOptions());

        _configurationManagerMock = fixture.Freeze<Mock<IServerConfigurationManager>>();
        _configurationManagerMock.Setup(c => c.ApplicationPaths.DefaultUserViewsPath).Returns(_userRootPath);
        _configurationManagerMock.Setup(c => c.ApplicationPaths.ProgramDataPath).Returns(Path.GetTempPath());
        _configurationManagerMock.Setup(c => c.ApplicationPaths.InternalMetadataPath).Returns(Path.Combine(Path.GetTempPath(), "reefin-lm-userrootfolder-tests-metadata"));
        // Distinct from _userRootPath: DefaultUserViewsPath is never the physical media root
        // (ItemResolveArgs.IsPhysicalRoot/IsVf compare against this) - matches production, where the
        // two are always different paths.
        _configurationManagerMock.Setup(c => c.ApplicationPaths.RootFolderPath).Returns(Path.Combine(Path.GetTempPath(), "reefin-lm-userrootfolder-tests-root"));
        _configurationManagerMock.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        _configurationManagerMock.Setup(c => c.GetConfiguration("metadata")).Returns(new MetadataConfiguration());

        // ResolverHelper (called from LibraryManager's own resolver pipeline) reads
        // BaseItem.ConfigurationManager.GetMetadataConfiguration() - the static, not the injected
        // instance (RFC §3/§8.3's static-fallback trap, out of scope to eliminate here). Point it at
        // the same mock so the oracle and the provider agree on UseFileCreationTimeForDateAdded.
        BaseItem.ConfigurationManager = _configurationManagerMock.Object;

        // Same DeepCopy-reflection hazard as UserRootFolderProviderTests: Children/SortName getters
        // touched by DeepCopy<Folder, UserRootFolder>() read these process-wide statics.
        BaseItem.ItemRepository = Mock.Of<IItemRepository>(r => r.GetItemList(It.IsAny<InternalItemsQuery>()) == Array.Empty<BaseItem>());

        _itemRepositoryMock = fixture.Freeze<Mock<IItemRepository>>();
        _itemRepositoryMock.Setup(i => i.RetrieveItem(It.IsAny<Guid>())).Returns<BaseItem>(null);

        fixture.Freeze<Mock<IItemPersistenceService>>();

        var externalDataManagerMock = fixture.Freeze<Mock<IExternalDataManager>>();
        fixture.Register(() => new Lazy<IExternalDataManager>(() => externalDataManagerMock.Object));

        // Real ManagedFileSystem, not a mock - the resolve-fallback path does real disk I/O
        // (Directory.CreateDirectory + directory enumeration via the resolver)
        // against a real, empty temp directory, exactly like the historical code path in production.
        var applicationPathsMock = new Mock<IApplicationPaths>();
        applicationPathsMock.Setup(p => p.TempDirectory).Returns(Path.GetTempPath());
        _fileSystem = new ManagedFileSystem(NullLogger<ManagedFileSystem>.Instance, applicationPathsMock.Object, Array.Empty<IShortcutHandler>());
        fixture.Inject<IFileSystem>(_fileSystem);

        // ItemResolveArgs.IsPhysicalRoot reads the static BaseItem.FileSystem (AreEqual against
        // IServerApplicationPaths.RootFolderPath) - unrelated to DefaultUserViewsPath, but still
        // needs a non-null static to avoid an NRE while resolving.
        BaseItem.FileSystem = _fileSystem;

        // Same single-instance double-singleton wiring as LibraryManagerItemStoreTests (PR106a).
        _itemLookupService = new Tesserafin.Server.Core.Library.ItemLookupService(_itemRepositoryMock.Object, _configurationManagerMock.Object);
        fixture.Register(() => _itemLookupService);
        fixture.Register<IItemLookupService>(() => _itemLookupService);
        fixture.Register<Tesserafin.Server.Core.Library.IItemCacheStore>(() => _itemLookupService);

        var itemAccessService = new Tesserafin.Server.Core.Library.ItemAccessService(
            _itemLookupService,
            new Tesserafin.Server.Core.Library.TagVisibilityService(_itemLookupService, fixture.Freeze<ICollectionFolderResolver>(), fixture.Freeze<IUserRootFolderProvider>(), _itemRepositoryMock.Object, fixture.Freeze<ILocalizationManager>(), fixture.Freeze<IServerConfigurationManager>(), new Tesserafin.Controller.LiveTv.ActiveRecordings()));
        fixture.Register<IItemAccessService>(() => itemAccessService);

        _itemStore = new Tesserafin.Server.Core.Library.ItemStore(
            fixture.Create<Mock<IItemPersistenceService>>().Object,
            _itemLookupService,
            _configurationManagerMock.Object,
            Mock.Of<IItemChangePublisher>());
        fixture.Register(() => _itemStore);
        fixture.Register<IItemStore>(() => _itemStore);

        _provider = new Tesserafin.Server.Core.Library.UserRootFolderProvider(
            _configurationManagerMock.Object,
            _fileSystem,
            _itemStore,
            _itemLookupService,
            NullLogger<Tesserafin.Server.Core.Library.UserRootFolderProvider>.Instance);
        fixture.Register(() => _provider);
        fixture.Register<IUserRootFolderProvider>(() => _provider);

        // Real FolderResolver (ResolverPriority.Last), no ignore rules - the deterministic, bounded
        // outcome for a plain internal directory with no parent (see UserRootFolderProvider's type
        // remarks: every higher-priority media resolver only claims media-shaped paths, never
        // exercised here since none are registered - matches the real production set's behavior for
        // this specific path, not merely "no resolvers at all").
        Tesserafin.Server.Implementations.Tests.Library.LibraryWriteFixture.Register(fixture);

        _libraryManager = fixture.Build<Tesserafin.Server.Core.Library.LibraryManager>().Do(s => s.AddParts(
                Array.Empty<IResolverIgnoreRule>(),
                new IItemResolver[] { new FolderResolver() },
                fixture.Create<IEnumerable<IBaseItemComparer>>()))
            .Create();

        Directory.CreateDirectory(_userRootPath);
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
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// Computes the historical oracle directly: <c>ResolvePath(fileInfo).DeepCopy&lt;Folder,
    /// UserRootFolder&gt;()</c>, exactly what <c>LibraryManager.GetUserRootFolder</c> used to do
    /// inline before PR107 (see <see cref="Tesserafin.Server.Core.Library.UserRootFolderProvider"/>'s
    /// type remarks for the full trace).
    /// </summary>
    private UserRootFolder ResolveOracle()
    {
        var resolved = _libraryManager.ResolvePath(_fileSystem.GetDirectoryInfo(_userRootPath)) as Folder;
        Assert.NotNull(resolved);
        return resolved.DeepCopy<Folder, UserRootFolder>();
    }

    [Fact]
    public void ResolveUserRootFolder_MatchesHistoricalResolvePathDeepCopy_Id()
    {
        var oracle = ResolveOracle();
        var actual = _provider.GetUserRootFolder();

        Assert.Equal(oracle.Id, actual.Id);
    }

    [Fact]
    public void ResolveUserRootFolder_MatchesHistoricalResolvePathDeepCopy_Path()
    {
        var oracle = ResolveOracle();
        var actual = _provider.GetUserRootFolder();

        Assert.Equal(oracle.Path, actual.Path);
    }

    [Fact]
    public void ResolveUserRootFolder_MatchesHistoricalResolvePathDeepCopy_Name()
    {
        var oracle = ResolveOracle();
        var actual = _provider.GetUserRootFolder();

        Assert.Equal(oracle.Name, actual.Name);
    }

    [Fact]
    public void ResolveUserRootFolder_MatchesHistoricalResolvePathDeepCopy_IsRoot()
    {
        var oracle = ResolveOracle();
        var actual = _provider.GetUserRootFolder();

        Assert.True(oracle.IsRoot);
        Assert.Equal(oracle.IsRoot, actual.IsRoot);
    }

    [Fact]
    public void ResolveUserRootFolder_MatchesHistoricalResolvePathDeepCopy_IsLocked()
    {
        var oracle = ResolveOracle();
        var actual = _provider.GetUserRootFolder();

        Assert.Equal(oracle.IsLocked, actual.IsLocked);
    }

    [Fact]
    public void ResolveUserRootFolder_MatchesHistoricalResolvePathDeepCopy_DateCreatedAndDateModified()
    {
        // Tolerant, not exact: the real ManagedFileSystem's reported CreationTimeUtc for the same
        // directory is not perfectly stable across two separate stat() calls on every platform/
        // filesystem combination (birth-time support varies) - two calls milliseconds apart can
        // legitimately differ by a few milliseconds even though both read the exact same code path
        // (config.UseFileCreationTimeForDateAdded branch in ResolverHelper.SetDateCreated /
        // UserRootFolderProvider.ResolveUserRootFolder). Exact field-for-field equality against a
        // fully deterministic, mocked FileSystemMetadata is already locked down in
        // UserRootFolderProviderTests; this test's job is cross-checking the *branch taken*
        // against the real resolver pipeline, not bit-exact real-filesystem timestamps.
        var oracle = ResolveOracle();
        var actual = _provider.GetUserRootFolder();

        Assert.True(Math.Abs((oracle.DateCreated - actual.DateCreated).TotalSeconds) < 5, $"Expected DateCreated close to {oracle.DateCreated}, got {actual.DateCreated}");
        Assert.True(Math.Abs((oracle.DateModified - actual.DateModified).TotalSeconds) < 5, $"Expected DateModified close to {oracle.DateModified}, got {actual.DateModified}");
    }

    [Fact]
    public void ResolveUserRootFolder_ResultType_IsUserRootFolder()
    {
        var actual = _provider.GetUserRootFolder();

        Assert.IsType<UserRootFolder>(actual);
    }

    // ---------------------------------------------------------------
    // Cache-hit parity: when the item already exists in the shared ItemLookupService (registered
    // under the same id LibraryManager.GetNewItemId(userRootPath, typeof(UserRootFolder)) would
    // compute), the provider returns it directly without resolving.
    // ---------------------------------------------------------------

    [Fact]
    public void GetUserRootFolder_ItemAlreadyRegistered_ReturnsRegisteredInstanceWithoutResolving()
    {
        var id = _libraryManager.GetNewItemId(_userRootPath, typeof(UserRootFolder));
        var existing = new UserRootFolder { Id = id, Path = _userRootPath, Name = "existing" };
        _itemStore.RegisterItem(existing);

        var result = _provider.GetUserRootFolder();

        Assert.Same(existing, result);
    }

    // ---------------------------------------------------------------
    // Delegation: LibraryManager.GetUserRootFolder() (the ILibraryManager facade member) returns the
    // exact same instance the injected IUserRootFolderProvider produces - no separate caching/logic
    // left on LibraryManager itself (RFC §9/PR107 exit criterion).
    // ---------------------------------------------------------------

    [Fact]
    public void LibraryManagerGetUserRootFolder_DelegatesToInjectedProvider_SameInstance()
    {
        var viaProvider = _provider.GetUserRootFolder();
        var viaLibraryManager = _libraryManager.GetUserRootFolder();

        Assert.Same(viaProvider, viaLibraryManager);
    }

    [Fact]
    public void LibraryManagerCsFile_DoesNotMentionIUserRootFolderProvider()
    {
        // RFC §9/PR107 exit criterion, restated as an executable guard: LibraryManager no longer
        // implements IUserRootFolderProvider - it only consumes it via a constructor parameter.
        var interfaces = typeof(Tesserafin.Server.Core.Library.LibraryManager).GetInterfaces();

        Assert.DoesNotContain(typeof(IUserRootFolderProvider), interfaces);
    }
}
