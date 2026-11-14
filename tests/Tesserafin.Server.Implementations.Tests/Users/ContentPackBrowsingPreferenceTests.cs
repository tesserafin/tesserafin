using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Common;
using Tesserafin.Common.Net;
using Tesserafin.Controller;
using Tesserafin.Controller.Authentication;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Drawing;
using Tesserafin.Controller.Events;
using Tesserafin.Controller.Library;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Database.Implementations;
using Tesserafin.Database.Implementations.Locking;
using Tesserafin.Database.Providers.Sqlite;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Cryptography;
using Tesserafin.Server.Implementations.Users;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Users;

/// <summary>
/// The content pack browsing preference is a per-user, server-owned, cross-client setting carried by
/// the existing user configuration. It is stored and exposed in M1 and consumed by nothing yet.
/// </summary>
public sealed class ContentPackBrowsingPreferenceTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DbContextOptions<TesserafinDbContext> _dbOptions;
    private readonly UserManager _userManager;

    public ContentPackBrowsingPreferenceTests()
    {
        // A file so the "survives a reload" case can build a genuinely new manager over the same
        // stored bytes rather than reusing one process-local connection.
        _databasePath = Path.Combine(Path.GetTempPath(), $"tesserafin-pref-{Guid.NewGuid():N}.db");

        _dbOptions = new DbContextOptionsBuilder<TesserafinDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        using (var ctx = CreateDbContext())
        {
            ctx.Database.EnsureCreated();
        }

        _userManager = CreateUserManager();
    }

    public void Dispose()
    {
        _userManager.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    [Fact]
    public async Task NewUser_DefaultsToMediaFamilyFirst()
    {
        var user = await _userManager.CreateUserAsync("alice");

        Assert.Equal(ContentPackBrowsingPreference.MediaFamilyFirst, user.ContentPackBrowsingPreference);
        Assert.Equal(
            ContentPackBrowsingPreference.MediaFamilyFirst,
            _userManager.GetUserDto(user).Configuration.ContentPackBrowsingPreference);
    }

    [Fact]
    public async Task LegacyUserWithNoStoredValue_ResolvesToMediaFamilyFirst()
    {
        // An upgraded server's rows predate the column, so the migration's default is what they
        // read back as. Writing the column explicitly to 0 reproduces exactly that state.
        var user = await _userManager.CreateUserAsync("legacy");

        using (var ctx = CreateDbContext())
        {
            await ctx.Users
                .Where(u => u.Id.Equals(user.Id))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(u => u.ContentPackBrowsingPreference, ContentPackBrowsingPreference.MediaFamilyFirst),
                    TestContext.Current.CancellationToken);
        }

        using var reloaded = CreateUserManager();
        var reread = reloaded.GetUserById(user.Id);

        Assert.NotNull(reread);
        Assert.Equal(ContentPackBrowsingPreference.MediaFamilyFirst, reread.ContentPackBrowsingPreference);
    }

    [Fact]
    public async Task UpdateConfiguration_PersistsAcrossAReload()
    {
        var user = await _userManager.CreateUserAsync("alice");

        var configuration = _userManager.GetUserDto(user).Configuration;
        configuration.ContentPackBrowsingPreference = ContentPackBrowsingPreference.ContentPackFirst;
        await _userManager.UpdateConfigurationAsync(user.Id, configuration);

        using var reloaded = CreateUserManager();
        var reread = reloaded.GetUserById(user.Id);

        Assert.NotNull(reread);
        Assert.Equal(ContentPackBrowsingPreference.ContentPackFirst, reread.ContentPackBrowsingPreference);
        Assert.Equal(
            ContentPackBrowsingPreference.ContentPackFirst,
            reloaded.GetUserDto(reread).Configuration.ContentPackBrowsingPreference);
    }

    [Fact]
    public async Task TwoUsersHoldIndependentValues()
    {
        var alice = await _userManager.CreateUserAsync("alice");
        var bob = await _userManager.CreateUserAsync("bob");

        var aliceConfiguration = _userManager.GetUserDto(alice).Configuration;
        aliceConfiguration.ContentPackBrowsingPreference = ContentPackBrowsingPreference.ContentPackFirst;
        await _userManager.UpdateConfigurationAsync(alice.Id, aliceConfiguration);

        Assert.Equal(
            ContentPackBrowsingPreference.ContentPackFirst,
            _userManager.GetUserById(alice.Id)!.ContentPackBrowsingPreference);

        // One user's choice is not the household's choice.
        Assert.Equal(
            ContentPackBrowsingPreference.MediaFamilyFirst,
            _userManager.GetUserById(bob.Id)!.ContentPackBrowsingPreference);
    }

    [Fact]
    public async Task UpdatingThePreferenceTouchesNoPackOrMembership()
    {
        var user = await _userManager.CreateUserAsync("alice");
        var packId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        using (var ctx = CreateDbContext())
        {
            ctx.BaseItems.Add(new BaseItemEntity
            {
                Id = itemId,
                Type = "Tesserafin.Controller.Entities.Movies.Movie",
                Name = "Match"
            });

            ctx.ContentPacks.Add(new ContentPack
            {
                Id = packId,
                Name = "Sport",
                NormalizedName = ContentPack.Normalize("Sport"),
                SortOrder = 0,
                Origin = ContentPackOrigin.Manual,
                DateCreated = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });

            ctx.ContentPackMemberships.Add(new ContentPackMembership
            {
                PackId = packId,
                ItemId = itemId,
                Provenance = ContentPackMembershipProvenance.Manual,
                DateCreated = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
            });

            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var configuration = _userManager.GetUserDto(user).Configuration;
        configuration.ContentPackBrowsingPreference = ContentPackBrowsingPreference.ContentPackFirst;
        await _userManager.UpdateConfigurationAsync(user.Id, configuration);

        using (var ctx = CreateDbContext())
        {
            var pack = Assert.Single(ctx.ContentPacks);
            Assert.Equal(packId, pack.Id);
            Assert.Equal(0, pack.SortOrder);

            var membership = Assert.Single(ctx.ContentPackMemberships);
            Assert.Equal(packId, membership.PackId);
            Assert.Equal(itemId, membership.ItemId);
            Assert.Equal(ContentPackMembershipProvenance.Manual, membership.Provenance);
        }
    }

    [Fact]
    public void InvalidEnumValueIsRejectedByTheExistingRequestConvention()
    {
        const string Body = """{"ContentPackBrowsingPreference":"SomethingElse"}""";

        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<UserConfiguration>(Body, JsonDefaults.Options));
    }

    [Fact]
    public void OmittedValueDeserializesToMediaFamilyFirst()
    {
        var configuration = JsonSerializer.Deserialize<UserConfiguration>("{}", JsonDefaults.Options);

        Assert.NotNull(configuration);
        Assert.Equal(ContentPackBrowsingPreference.MediaFamilyFirst, configuration.ContentPackBrowsingPreference);
    }

    private UserManager CreateUserManager()
    {
        var factory = new Mock<IDbContextFactory<TesserafinDbContext>>();
        factory.Setup(f => f.CreateDbContext()).Returns(CreateDbContext);
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateDbContext);

        var cryptoProvider = new Mock<ICryptoProvider>();
        var configManager = new Mock<IServerConfigurationManager>();
        var appPaths = new Mock<IServerApplicationPaths>();
        appPaths.Setup(x => x.ProgramDataPath).Returns(Path.GetTempPath());
        configManager.Setup(x => x.ApplicationPaths).Returns(appPaths.Object);
        configManager.Setup(x => x.Configuration).Returns(new ServerConfiguration());

        var appHost = new Mock<IApplicationHost>();

        return new UserManager(
            factory.Object,
            new NoopEventManager(),
            new Mock<INetworkManager>().Object,
            appHost.Object,
            new Mock<IImageProcessor>().Object,
            NullLogger<UserManager>.Instance,
            configManager.Object,
            [new DefaultPasswordResetProvider(configManager.Object, appHost.Object)],
            [
                new DefaultAuthenticationProvider(NullLogger<DefaultAuthenticationProvider>.Instance, cryptoProvider.Object),
                new InvalidAuthProvider()
            ]);
    }

    private TesserafinDbContext CreateDbContext()
    {
        return new TesserafinDbContext(
            _dbOptions,
            NullLogger<TesserafinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    private sealed class NoopEventManager : IEventManager
    {
        public void Publish<T>(T eventArgs)
            where T : EventArgs
        {
        }

        public Task PublishAsync<T>(T eventArgs)
            where T : EventArgs
            => Task.CompletedTask;
    }
}
