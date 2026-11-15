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
using Tesserafin.Database.Implementations;
using Tesserafin.Database.Implementations.Locking;
using Tesserafin.Database.Providers.Sqlite;
using Tesserafin.Server.Implementations.FullSystemBackup;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.FullSystemBackup;

/// <summary>
/// Pins the stem of the archive <see cref="BackupService"/> writes.
/// </summary>
/// <remarks>
/// <para>
/// The pre-rename service built <c>reefin-backup-{timestamp}.zip</c>. That stem is the last
/// production filename carrying the old spelling, and it is operator-visible: it is what an
/// administrator sees in the backup directory and what they are asked to hand back to
/// <c>RestoreBackupAsync</c>.
/// </para>
/// <para>
/// This is a plain spelling correction, not a marker migration. <see cref="BackupService"/> selects
/// archives with an unanchored <c>*.zip</c> enumeration and restores whatever path it is given, so
/// archives already written under the old stem stay listable and restorable with no compatibility
/// glob. Nothing is renamed on disk and nothing is migrated.
/// </para>
/// <para>
/// The assertion is made against the file the service actually created rather than against the
/// source text, so it fails for a service that writes one name and reports another.
/// </para>
/// </remarks>
public sealed class BackupArchiveNamingTests : IDisposable
{
    private const string LegacyStem = "reefin-backup-";
    private const string CurrentStem = "tesserafin-backup-";

    private readonly DirectoryInfo _tmp;
    private readonly DbContextOptions<TesserafinDbContext> _dbOptions;
    private readonly string _backupPath;
    private readonly BackupService _sut;

    public BackupArchiveNamingTests()
    {
        _tmp = Directory.CreateTempSubdirectory("backup-archive-naming-");

        _dbOptions = new DbContextOptionsBuilder<TesserafinDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_tmp.FullName, "tesserafin.db")}")
            .Options;

        using (var ctx = CreateDbContext())
        {
            ctx.Database.EnsureCreated();
        }

        _backupPath = CreateRoot("backups");

        var paths = new Mock<IServerApplicationPaths>();
        paths.SetupGet(p => p.ConfigurationDirectoryPath).Returns(CreateRoot("Config"));
        paths.SetupGet(p => p.DataPath).Returns(CreateRoot("Data"));
        paths.SetupGet(p => p.BackupPath).Returns(_backupPath);
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
    public async Task CreateBackupAsync_WritesArchiveUnderTheCurrentStem()
    {
        var manifest = await _sut.CreateBackupAsync(new BackupOptionsDto
        {
            Database = true,
            Metadata = false,
            Subtitles = false,
            Trickplay = false
        });

        var reported = Path.GetFileName(manifest.Path);

        Assert.DoesNotContain(LegacyStem, reported, StringComparison.Ordinal);
        Assert.StartsWith(CurrentStem, reported, StringComparison.Ordinal);

        // The manifest is only a claim; assert against what landed in the backup directory.
        var written = Directory.EnumerateFiles(_backupPath, "*.zip")
            .Select(Path.GetFileName)
            .ToList();

        var onDisk = Assert.Single(written);
        Assert.DoesNotContain(LegacyStem, onDisk!, StringComparison.Ordinal);
        Assert.StartsWith(CurrentStem, onDisk!, StringComparison.Ordinal);
        Assert.Equal(reported, onDisk);
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
