using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.SystemBackupService;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Database.Implementations;
using Tesserafin.Database.Implementations.Locking;
using Tesserafin.Database.Providers.Sqlite;
using Tesserafin.Server.Implementations.FullSystemBackup;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.FullSystemBackup;

/// <summary>
/// Content packs, their memberships and the per-user browsing preference must survive a full-system
/// backup and restore. The backup enumerates <c>DbSet</c> properties reflectively, so this is a test
/// obligation rather than new backup code — and this test is what proves the reflection really does
/// reach the new tables.
/// </summary>
public sealed class ContentPackBackupRoundTripTests : IDisposable
{
    private readonly DirectoryInfo _tmp;
    private readonly string _databasePath;
    private readonly DbContextOptions<TesserafinDbContext> _dbOptions;
    private readonly BackupService _sut;

    public ContentPackBackupRoundTripTests()
    {
        _tmp = Directory.CreateTempSubdirectory("content-pack-backup-");
        _databasePath = Path.Combine(_tmp.FullName, "tesserafin.db");

        _dbOptions = new DbContextOptionsBuilder<TesserafinDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        using (var ctx = CreateDbContext())
        {
            ctx.Database.EnsureCreated();
        }

        var paths = new Mock<IServerApplicationPaths>();
        paths.SetupGet(p => p.ConfigurationDirectoryPath).Returns(CreateRoot("Config"));
        paths.SetupGet(p => p.DataPath).Returns(CreateRoot("Data"));
        paths.SetupGet(p => p.BackupPath).Returns(CreateRoot("backups"));
        paths.SetupGet(p => p.RootFolderPath).Returns(CreateRoot("Root"));
        paths.SetupGet(p => p.InternalMetadataPath).Returns(CreateRoot("metadata"));
        paths.SetupGet(p => p.DefaultInternalMetadataPath).Returns(CreateRoot("metadata-default"));
        paths.SetupGet(p => p.CachePath).Returns(CreateRoot("cache"));
        paths.SetupGet(p => p.LogDirectoryPath).Returns(CreateRoot("log"));
        paths.SetupGet(p => p.ProgramDataPath).Returns(_tmp.FullName);

        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(new Version(99, 0, 0, 0));

        var factory = new Mock<IDbContextFactory<TesserafinDbContext>>();
        factory.Setup(f => f.CreateDbContext()).Returns(CreateDbContext);
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateDbContext);

        var databaseProvider = new SqliteDatabaseProvider(paths.Object, NullLogger<SqliteDatabaseProvider>.Instance)
        {
            DbContextFactory = factory.Object
        };

        _sut = new BackupService(
            NullLogger<BackupService>.Instance,
            factory.Object,
            host.Object,
            paths.Object,
            databaseProvider,
            new Mock<IHostApplicationLifetime>().Object,
            new Mock<ILibraryManager>().Object);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _tmp.Delete(true);
    }

    [Fact]
    public async Task BackupAndRestore_PreservesPacksMembershipsProvenanceAndPreferences()
    {
        var sport = Guid.NewGuid();
        var concerts = Guid.NewGuid();
        var shared = Guid.NewGuid();
        var seeded = Guid.NewGuid();
        var mediaFamilyUser = Guid.NewGuid();
        var contentPackUser = Guid.NewGuid();

        using (var ctx = CreateDbContext())
        {
            // Two packs whose stored order is the reverse of their creation order, one with a
            // description and one without, and both M1 provenance values in the membership rows.
            ctx.ContentPacks.Add(Pack(sport, "Sport", null, 1, ContentPackOrigin.Manual));
            ctx.ContentPacks.Add(Pack(concerts, "Concerts", "Live music", 0, ContentPackOrigin.SystemSeed));

            ctx.BaseItems.Add(Item(shared, "Shared match"));
            ctx.BaseItems.Add(Item(seeded, "Seeded album"));

            // One item in several packs, so the many-to-many shape has to survive too.
            ctx.ContentPackMemberships.Add(Membership(sport, shared, ContentPackMembershipProvenance.Manual));
            ctx.ContentPackMemberships.Add(Membership(concerts, shared, ContentPackMembershipProvenance.Manual));
            ctx.ContentPackMemberships.Add(Membership(concerts, seeded, ContentPackMembershipProvenance.SystemSeed));

            ctx.Users.Add(User(mediaFamilyUser, "legacy", ContentPackBrowsingPreference.MediaFamilyFirst));
            ctx.Users.Add(User(contentPackUser, "switcher", ContentPackBrowsingPreference.ContentPackFirst));

            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var manifest = await _sut.CreateBackupAsync(new BackupOptionsDto
        {
            Database = true,
            Metadata = false,
            Subtitles = false,
            Trickplay = false
        });

        // Wipe every row the round trip is supposed to bring back.
        using (var ctx = CreateDbContext())
        {
            await ctx.ContentPackMemberships.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            await ctx.ContentPacks.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            Assert.Empty(ctx.ContentPacks);
        }

        await _sut.RestoreBackupAsync(manifest.Path);

        using (var ctx = CreateDbContext())
        {
            var packs = ctx.ContentPacks.OrderBy(e => e.SortOrder).ToList();
            Assert.Equal(2, packs.Count);

            Assert.Equal(concerts, packs[0].Id);
            Assert.Equal("Concerts", packs[0].Name);
            Assert.Equal("Live music", packs[0].Description);
            Assert.Equal(ContentPackOrigin.SystemSeed, packs[0].Origin);

            Assert.Equal(sport, packs[1].Id);
            Assert.Equal("Sport", packs[1].Name);
            Assert.Null(packs[1].Description);
            Assert.Equal(ContentPackOrigin.Manual, packs[1].Origin);

            var memberships = ctx.ContentPackMemberships.ToList();
            Assert.Equal(3, memberships.Count);
            Assert.Equal(2, memberships.Count(m => m.ItemId.Equals(shared)));
            Assert.Equal(
                ContentPackMembershipProvenance.SystemSeed,
                memberships.Single(m => m.ItemId.Equals(seeded)).Provenance);
            Assert.All(
                memberships.Where(m => m.ItemId.Equals(shared)),
                m => Assert.Equal(ContentPackMembershipProvenance.Manual, m.Provenance));

            // The items the memberships point at are still there, so no relationship is dangling.
            Assert.Single(ctx.BaseItems.Where(e => e.Id.Equals(shared)));
            Assert.Single(ctx.BaseItems.Where(e => e.Id.Equals(seeded)));

            // Two users, two different preferences, both intact.
            Assert.Equal(
                ContentPackBrowsingPreference.MediaFamilyFirst,
                ctx.Users.Single(u => u.Id.Equals(mediaFamilyUser)).ContentPackBrowsingPreference);
            Assert.Equal(
                ContentPackBrowsingPreference.ContentPackFirst,
                ctx.Users.Single(u => u.Id.Equals(contentPackUser)).ContentPackBrowsingPreference);
        }
    }

    private static ContentPack Pack(Guid id, string name, string? description, int sortOrder, ContentPackOrigin origin) => new()
    {
        Id = id,
        Name = name,
        NormalizedName = ContentPack.Normalize(name),
        Description = description,
        SortOrder = sortOrder,
        Origin = origin,
        DateCreated = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private static ContentPackMembership Membership(Guid packId, Guid itemId, ContentPackMembershipProvenance provenance) => new()
    {
        PackId = packId,
        ItemId = itemId,
        Provenance = provenance,
        DateCreated = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
    };

    private static BaseItemEntity Item(Guid id, string name) => new()
    {
        Id = id,
        Type = "Tesserafin.Controller.Entities.Movies.Movie",
        Name = name
    };

    private static User User(Guid id, string name, ContentPackBrowsingPreference preference) => new(name, "auth", "reset")
    {
        Id = id,
        ContentPackBrowsingPreference = preference
    };

    private string CreateRoot(string name) => Directory.CreateDirectory(Path.Combine(_tmp.FullName, name)).FullName;

    private TesserafinDbContext CreateDbContext()
    {
        return new TesserafinDbContext(
            _dbOptions,
            NullLogger<TesserafinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }
}
