using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Database.Implementations;
using Tesserafin.Database.Implementations.Locking;
using Tesserafin.Database.Providers.Sqlite;
using Tesserafin.Server.Implementations.Users;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Data;

/// <summary>
/// The migration that makes the scope of item display preferences unique, on a SQLite file migrated to
/// the schema before it and seeded with every class of duplicate: the row with the lowest id stays
/// active with its settings, every other row of its scope is archived in full and only then removed,
/// a failure leaves the database as it was, and the migration can run again and be reverted.
/// </summary>
public sealed class ItemDisplayPreferencesDeduplicationMigrationTests : IDisposable
{
    private const string PreviousMigration = "20260807161612_AddContentPacks";
    private const string UniqueIndex = "IX_ItemDisplayPreferences_UserId_ItemId_Client";
    private const string UserIndex = "IX_ItemDisplayPreferences_UserId";

    private readonly string _directory;
    private readonly string _databasePath;
    private readonly DbContextOptions<TesserafinDbContext> _dbOptions;
    private readonly Guid _itemIdentical = Guid.NewGuid();
    private readonly Guid _itemContradictory = Guid.NewGuid();
    private readonly Guid _itemSingle = Guid.NewGuid();
    private Guid _userId;

    public ItemDisplayPreferencesDeduplicationMigrationTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "tesserafin-dedup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "tesserafin.db");
        _dbOptions = new DbContextOptionsBuilder<TesserafinDbContext>()
            .UseSqlite($"Data Source={_databasePath}", f => f.MigrationsAssembly(typeof(SqliteDatabaseProvider).Assembly))
            .Options;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }

    [Fact]
    public void Up_KeepsTheLowestIdOfEachScopeAndArchivesEveryOtherRowInFull()
    {
        var before = SeedLegacyDatabase();

        // The read-only diagnostic of docs/data-model-reliability.md sees the three duplicated scopes.
        Assert.Equal(
            [
                (_itemContradictory, 3L, 3L, false),
                (_itemIdentical, 2L, 1L, false),
                (Guid.Empty, 2L, 2L, true),
            ],
            Diagnose().OrderBy(group => group.Global).ThenByDescending(group => group.Rows));

        using (var context = CreateDbContext())
        {
            context.Database.Migrate();
        }

        Assert.Empty(Diagnose());
        var after = ReadActive();
        var archive = ReadArchive();
        var scopes = before.GroupBy(row => (row.UserId, row.ItemId, row.Client)).ToList();

        // One row per scope, the lowest id, unchanged.
        Assert.Equal(scopes.Count, after.Count);
        Assert.Equal(scopes.Select(scope => scope.MinBy(row => row.Id)!).OrderBy(row => row.Id), after);

        // Every other row is in the archive, every column equal, and nothing else is.
        Assert.Equal(before.Count, after.Count + archive.Count);
        Assert.Equal(before.Except(after).OrderBy(row => row.Id), archive.Select(entry => entry.Row));
        foreach (var entry in archive)
        {
            var scope = scopes.Single(group => group.Any(row => row.Id == entry.Row.Id));
            Assert.Equal(scope.Min(row => row.Id), entry.KeptId);
            Assert.InRange(entry.ArchivedAt, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(1));
        }

        Assert.Equal(
            [
                ItemDisplayPreferencesDuplicateClass.Global,
                ItemDisplayPreferencesDuplicateClass.Contradictory,
                ItemDisplayPreferencesDuplicateClass.Contradictory,
                ItemDisplayPreferencesDuplicateClass.Identical,
            ],
            archive.Select(entry => entry.DuplicateClass).OrderDescending());
        Assert.All(
            archive.Where(entry => entry.Row.ItemId.Equals(_itemIdentical)),
            entry => Assert.Equal(ItemDisplayPreferencesDuplicateClass.Identical, entry.DuplicateClass));
        Assert.All(
            archive.Where(entry => entry.Row.ItemId.Equals(_itemContradictory)),
            entry => Assert.Equal(ItemDisplayPreferencesDuplicateClass.Contradictory, entry.DuplicateClass));
        Assert.All(
            archive.Where(entry => entry.Row.ItemId.Equals(Guid.Empty)),
            entry => Assert.Equal(ItemDisplayPreferencesDuplicateClass.Global, entry.DuplicateClass));

        // The scope is unique from now on, and the index on the user alone is gone (the new one covers it).
        Assert.Equal([UniqueIndex], ReadIndexes());
        var duplicate = Assert.Throws<DbUpdateException>(() =>
        {
            using var context = CreateDbContext();
            context.ItemDisplayPreferences.Add(new ItemDisplayPreferences(_userId, _itemSingle, "web"));
            context.SaveChanges();
        });
        Assert.Contains("UNIQUE", duplicate.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Up_AfterARestart_TheServerReadsTheKeptRows()
    {
        var before = SeedLegacyDatabase();
        using (var context = CreateDbContext())
        {
            context.Database.Migrate();
        }

        SqliteConnection.ClearAllPools();

        // A new manager on new connections, as after a restart, reads the settings the kept row held.
        var factory = new Mock<IDbContextFactory<TesserafinDbContext>>();
        factory.Setup(value => value.CreateDbContext()).Returns(CreateDbContext);
        var manager = new DisplayPreferencesManager(factory.Object);
        foreach (var scope in before.GroupBy(row => (row.UserId, row.ItemId, row.Client)))
        {
            var kept = scope.MinBy(row => row.Id)!;
            var read = manager.GetItemDisplayPreferences(scope.Key.UserId, scope.Key.ItemId, scope.Key.Client);
            Assert.Equal(kept, Row.From(read));
        }

        Assert.Equal(before.GroupBy(row => (row.UserId, row.ItemId, row.Client)).Count(), ReadActive().Count);
    }

    [Fact]
    public void Up_FailingAfterTheArchive_LeavesTheDatabaseAsItWas()
    {
        var before = SeedLegacyDatabase();
        Execute("""CREATE TRIGGER "FailDelete" BEFORE DELETE ON "ItemDisplayPreferences" BEGIN SELECT RAISE(ABORT, 'injected failure'); END;""");

        var failure = Assert.ThrowsAny<Exception>(() =>
        {
            using var context = CreateDbContext();
            context.Database.Migrate();
        });
        Assert.Contains("injected failure", failure.ToString(), StringComparison.Ordinal);

        // The archive written before the failing delete is rolled back with it.
        Assert.Equal(before, ReadActive());
        Assert.False(TableExists("ItemDisplayPreferencesArchive"));
        Assert.Equal([UserIndex], ReadIndexes());
        Assert.DoesNotContain(AppliedMigrations(), id => id.EndsWith("_DeduplicateItemDisplayPreferences", StringComparison.Ordinal));

        // The next start applies it.
        Execute("""DROP TRIGGER "FailDelete";""");
        using (var context = CreateDbContext())
        {
            context.Database.Migrate();
        }

        Assert.Equal(before.Count, ReadActive().Count + ReadArchive().Count);
        Assert.Equal([UniqueIndex], ReadIndexes());
    }

    [Fact]
    public void Up_RunAgainOnAMigratedDatabase_ChangesNothing()
    {
        SeedLegacyDatabase();
        using (var context = CreateDbContext())
        {
            context.Database.Migrate();
        }

        var active = ReadActive();
        var archive = ReadArchive();

        // A full-system restore of an earlier backup writes back a history without the migration.
        var migrationId = AppliedMigrations().Single(id => id.EndsWith("_DeduplicateItemDisplayPreferences", StringComparison.Ordinal));
        Execute($"""DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" = '{migrationId}';""");
        using (var context = CreateDbContext())
        {
            context.Database.Migrate();
        }

        Assert.Equal(active, ReadActive());
        Assert.Equal(archive, ReadArchive());
        Assert.Equal([UniqueIndex], ReadIndexes());
    }

    [Fact]
    public void Down_PutsEveryArchivedRowBackWithItsId()
    {
        var before = SeedLegacyDatabase();
        using (var context = CreateDbContext())
        {
            context.Database.Migrate();
            context.GetService<IMigrator>().Migrate(PreviousMigration);
        }

        Assert.Equal(before, ReadActive());
        Assert.False(TableExists("ItemDisplayPreferencesArchive"));
        Assert.Equal([UserIndex], ReadIndexes());
    }

    /// <summary>
    /// Migrates a new database to the schema before the migration and seeds it with every class of
    /// duplicate the diagnostic in docs/data-model-reliability.md describes, next to scopes that are
    /// already unique.
    /// </summary>
    private List<Row> SeedLegacyDatabase()
    {
        using var context = CreateDbContext();
        context.GetService<IMigrator>().Migrate(PreviousMigration);
        Assert.Equal([UserIndex], ReadIndexes());

        var user = new User("viewer", "auth", "reset");
        context.Users.Add(user);
        context.SaveChanges();
        _userId = user.Id;

        // Inserted one at a time, so that the ids follow this order.
        var rows = new[]
        {
            // Identical: two rows left at the creation defaults by the creation race.
            Prefs(_itemIdentical, "web"),
            Prefs(_itemIdentical, "web"),

            // Contradictory: the default row is the oldest and stays active.
            Prefs(_itemContradictory, "web"),
            Prefs(_itemContradictory, "web", "DateCreated", SortOrder.Descending, remember: true),
            Prefs(_itemContradictory, "web", "PremiereDate", SortOrder.Ascending, remember: true, ViewType.Movies),

            // Global, origin unknown: a default row and a remembered choice.
            Prefs(Guid.Empty, "web"),
            Prefs(Guid.Empty, "web", "PremiereDate", SortOrder.Descending, remember: true),

            // Already unique: another client of a duplicated item, a lone global row, a lone item row.
            Prefs(_itemIdentical, "android", "SortName", SortOrder.Descending, remember: true),
            Prefs(Guid.Empty, "tv", "DateCreated", SortOrder.Ascending, remember: true),
            Prefs(_itemSingle, "web", "Runtime", SortOrder.Ascending, remember: false, ViewType.Episodes),
        };
        foreach (var row in rows)
        {
            context.ItemDisplayPreferences.Add(row);
            context.SaveChanges();
        }

        var seeded = ReadActive();
        Assert.Equal(rows.Length, seeded.Count);
        return seeded;
    }

    private ItemDisplayPreferences Prefs(Guid itemId, string client, string sortBy = "SortName", SortOrder sortOrder = SortOrder.Ascending, bool remember = false, ViewType viewType = ViewType.Albums)
    {
        return new ItemDisplayPreferences(_userId, itemId, client)
        {
            SortBy = sortBy,
            SortOrder = sortOrder,
            RememberSorting = remember,
            ViewType = viewType,
        };
    }

    private List<Row> ReadActive()
    {
        using var context = CreateDbContext();
        return context.ItemDisplayPreferences.AsNoTracking().OrderBy(row => row.Id).AsEnumerable().Select(Row.From).ToList();
    }

    private List<(Row Row, int KeptId, ItemDisplayPreferencesDuplicateClass DuplicateClass, DateTime ArchivedAt)> ReadArchive()
    {
        using var context = CreateDbContext();
        return context.ItemDisplayPreferencesArchive.AsNoTracking().OrderBy(row => row.Id).AsEnumerable()
            .Select(row => (
                new Row(row.Id, row.UserId, row.ItemId, row.Client, row.ViewType, row.RememberIndexing, row.IndexBy, row.RememberSorting, row.SortBy, row.SortOrder),
                row.KeptId,
                row.DuplicateClass,
                DateTime.SpecifyKind(row.ArchivedAt, DateTimeKind.Utc)))
            .ToList();
    }

    private List<(Guid ItemId, long Rows, long DistinctSettings, bool Global)> Diagnose()
    {
        // Kept in step with docs/data-model-reliability.md.
        const string Diagnostic = """
            SELECT UserId, ItemId, Client,
                   COUNT(*) AS Rows,
                   COUNT(DISTINCT SortBy || '|' || SortOrder || '|' || ViewType || '|' || IFNULL(IndexBy, '-')
                       || '|' || RememberSorting || '|' || RememberIndexing) AS DistinctSettings,
                   SUM(SortBy = 'SortName' AND SortOrder = 0 AND ViewType = 0 AND IndexBy IS NULL
                       AND RememberSorting = 0 AND RememberIndexing = 0) AS DefaultRows,
                   ItemId = '00000000-0000-0000-0000-000000000000' AS GlobalScope,
                   MIN(Id) AS ReadRowId
            FROM ItemDisplayPreferences
            GROUP BY UserId, ItemId, Client
            HAVING COUNT(*) > 1
            ORDER BY UserId, ItemId, Client;
            """;

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = Diagnostic;
        using var reader = command.ExecuteReader();
        var groups = new List<(Guid, long, long, bool)>();
        while (reader.Read())
        {
            groups.Add((reader.GetGuid(1), reader.GetInt64(3), reader.GetInt64(4), reader.GetBoolean(6)));
        }

        return groups;
    }

    private List<string> ReadIndexes()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """SELECT "name" FROM "sqlite_master" WHERE "type" = 'index' AND "tbl_name" = 'ItemDisplayPreferences' AND "sql" IS NOT NULL ORDER BY "name";""";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private bool TableExists(string name)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """SELECT COUNT(*) FROM "sqlite_master" WHERE "type" = 'table' AND "name" = $name;""";
        command.Parameters.AddWithValue("$name", name);
        return (long)command.ExecuteScalar()! == 1;
    }

    private List<string> AppliedMigrations()
    {
        using var context = CreateDbContext();
        return context.Database.GetAppliedMigrations().ToList();
    }

    private void Execute(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Statements are written in this class; the only interpolated value is a migration id from this test's database.
        command.CommandText = sql;
#pragma warning restore CA2100
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        return connection;
    }

    private TesserafinDbContext CreateDbContext()
    {
        return new TesserafinDbContext(
            _dbOptions,
            NullLogger<TesserafinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    private sealed record Row(int Id, Guid UserId, Guid ItemId, string Client, ViewType ViewType, bool RememberIndexing, IndexingKind? IndexBy, bool RememberSorting, string SortBy, SortOrder SortOrder)
    {
        public static Row From(ItemDisplayPreferences row)
            => new(row.Id, row.UserId, row.ItemId, row.Client, row.ViewType, row.RememberIndexing, row.IndexBy, row.RememberSorting, row.SortBy, row.SortOrder);
    }
}
