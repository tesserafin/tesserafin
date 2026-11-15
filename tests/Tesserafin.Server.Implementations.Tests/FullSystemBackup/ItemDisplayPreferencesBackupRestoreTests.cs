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
/// A full-system backup taken before the scope of item display preferences became unique can hold
/// duplicates. Restoring it into the current schema must apply the rule of the migration (lowest
/// original id active, every other row archived in full) instead of failing on the unique index after
/// the database was purged.
/// </summary>
public sealed class ItemDisplayPreferencesBackupRestoreTests : IDisposable
{
    private readonly DirectoryInfo _tmp;
    private readonly DbContextOptions<TesserafinDbContext> _dbOptions;
    private readonly BackupService _sut;

    public ItemDisplayPreferencesBackupRestoreTests()
    {
        _tmp = Directory.CreateTempSubdirectory("item-prefs-backup-");
        _dbOptions = new DbContextOptionsBuilder<TesserafinDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_tmp.FullName, "tesserafin.db")}")
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
            new Mock<ILibraryScanCoordinator>().Object);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _tmp.Delete(true);
    }

    [Fact]
    public async Task RestoringABackupWithDuplicates_KeepsTheLowestIdAndArchivesTheRest()
    {
        var item = Guid.NewGuid();
        var single = Guid.NewGuid();
        Guid userId;
        int keptContradictory;
        int keptGlobal;

        // The database the backup is taken from has no unique index yet.
        using (var ctx = CreateDbContext())
        {
            await ctx.Database.ExecuteSqlRawAsync("""DROP INDEX "IX_ItemDisplayPreferences_UserId_ItemId_Client";""", TestContext.Current.CancellationToken);
            var user = new User("viewer", "auth", "reset");
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            userId = user.Id;

            ItemDisplayPreferences Add(Guid itemId, string sortBy, bool remember)
            {
                var row = new ItemDisplayPreferences(userId, itemId, "web") { SortBy = sortBy, RememberSorting = remember };
                ctx.ItemDisplayPreferences.Add(row);
                ctx.SaveChanges();
                return row;
            }

            keptContradictory = Add(item, "SortName", false).Id;
            Add(item, "DateCreated", true);
            keptGlobal = Add(Guid.Empty, "SortName", false).Id;
            Add(Guid.Empty, "SortName", false);
            Add(single, "Runtime", true);
        }

        var manifest = await _sut.CreateBackupAsync(new BackupOptionsDto
        {
            Database = true,
            Metadata = false,
            Subtitles = false,
            Trickplay = false
        });

        // Restored into the current schema, unique index included.
        using (var ctx = CreateDbContext())
        {
            await ctx.ItemDisplayPreferences.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            await ctx.Database.ExecuteSqlRawAsync(
                """CREATE UNIQUE INDEX "IX_ItemDisplayPreferences_UserId_ItemId_Client" ON "ItemDisplayPreferences" ("UserId", "ItemId", "Client");""",
                TestContext.Current.CancellationToken);
        }

        await _sut.RestoreBackupAsync(manifest.Path);

        using (var ctx = CreateDbContext())
        {
            var active = ctx.ItemDisplayPreferences.AsNoTracking().ToList();
            Assert.Equal(3, active.Count);
            Assert.Equal("SortName", active.Single(row => row.ItemId.Equals(item)).SortBy);
            Assert.False(active.Single(row => row.ItemId.Equals(item)).RememberSorting);
            Assert.Equal("Runtime", active.Single(row => row.ItemId.Equals(single)).SortBy);
            Assert.Single(active, row => row.ItemId.Equals(Guid.Empty));

            var archived = ctx.ItemDisplayPreferencesArchive.AsNoTracking().OrderBy(row => row.Id).ToList();
            Assert.Equal(2, archived.Count);

            var contradictory = archived.Single(row => row.ItemId.Equals(item));
            Assert.Equal(keptContradictory, contradictory.KeptId);
            Assert.Equal(ItemDisplayPreferencesDuplicateClass.Contradictory, contradictory.DuplicateClass);
            Assert.Equal("DateCreated", contradictory.SortBy);
            Assert.True(contradictory.RememberSorting);
            Assert.Equal(userId, contradictory.UserId);
            Assert.Equal("web", contradictory.Client);

            var global = archived.Single(row => row.ItemId.Equals(Guid.Empty));
            Assert.Equal(keptGlobal, global.KeptId);
            Assert.Equal(keptGlobal + 1, global.Id);
            Assert.Equal(ItemDisplayPreferencesDuplicateClass.Global, global.DuplicateClass);
        }
    }

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
