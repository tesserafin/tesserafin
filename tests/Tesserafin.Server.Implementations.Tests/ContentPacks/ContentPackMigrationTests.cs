using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Tesserafin.Database.Implementations;
using Tesserafin.Database.Implementations.Locking;
using Tesserafin.Database.Providers.Sqlite;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.ContentPacks;

/// <summary>
/// The content pack migration must be additive. These tests read the operations the migration
/// actually emits, and then the schema a real migrated database ends up with, so neither a stray
/// data statement nor a missing index can ship.
/// </summary>
public sealed class ContentPackMigrationTests : IDisposable
{
    private const string MigrationTypeName = "Tesserafin.Server.Implementations.Migrations.AddContentPacks";

    private static readonly string[] DormantLibraryTables =
    [
        "Libraries",
        "LibraryItems",
        "LibraryRoot",
        "Collections",
        "CollectionItems",
        "MediaFiles",
        "MediaFileStream"
    ];

    private readonly string _databasePath;
    private readonly DbContextOptions<TesserafinDbContext> _dbOptions;

    public ContentPackMigrationTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"tesserafin-migrate-{Guid.NewGuid():N}.db");

        _dbOptions = new DbContextOptionsBuilder<TesserafinDbContext>()
            .UseSqlite(
                $"Data Source={_databasePath}",
                f => f.MigrationsAssembly(typeof(SqliteDatabaseProvider).Assembly))
            .Options;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    [Fact]
    public void Up_CreatesOnlyTheNewObjectsAndTouchesNoExistingData()
    {
        var operations = GetMigration().UpOperations;

        // Nothing that could read, transform or rewrite an existing row.
        Assert.Empty(operations.OfType<SqlOperation>());
        Assert.Empty(operations.OfType<UpdateDataOperation>());
        Assert.Empty(operations.OfType<DeleteDataOperation>());
        Assert.Empty(operations.OfType<InsertDataOperation>());
        Assert.Empty(operations.OfType<DropColumnOperation>());
        Assert.Empty(operations.OfType<DropTableOperation>());
        Assert.Empty(operations.OfType<DropIndexOperation>());
        Assert.Empty(operations.OfType<AlterColumnOperation>());
        Assert.Empty(operations.OfType<RenameColumnOperation>());
        Assert.Empty(operations.OfType<RenameTableOperation>());

        var createdTables = operations.OfType<CreateTableOperation>().Select(o => o.Name).ToArray();
        Assert.Equal(["ContentPacks", "ContentPackMemberships"], createdTables);

        // The one touch to an existing table is a new column with a default, which adds nothing to
        // read and rewrites no row: it is what carries the per-user browsing preference.
        var addedColumns = operations.OfType<AddColumnOperation>().ToArray();
        var addedColumn = Assert.Single(addedColumns);
        Assert.Equal("Users", addedColumn.Table);
        Assert.Equal("ContentPackBrowsingPreference", addedColumn.Name);
        Assert.Equal(0, addedColumn.DefaultValue);
        Assert.False(addedColumn.IsNullable);

        var createdIndexes = operations.OfType<CreateIndexOperation>().ToArray();
        Assert.Equal(3, createdIndexes.Length);
        Assert.Contains(createdIndexes, o => o.Name == "IX_ContentPacks_NormalizedName" && o.IsUnique);
        Assert.Contains(createdIndexes, o => o.Name == "IX_ContentPacks_SortOrder");
        Assert.Contains(createdIndexes, o => o.Name == "IX_ContentPackMemberships_ItemId");

        // Every operation is accounted for by one of the shapes above.
        Assert.Equal(
            operations.Count,
            createdTables.Length + addedColumns.Length + createdIndexes.Length);
    }

    [Fact]
    public void Up_DoesNotReviveTheDormantLibrariesSchema()
    {
        var operations = GetMigration().UpOperations;

        var names = operations.OfType<CreateTableOperation>().Select(o => o.Name)
            .Concat(operations.OfType<AddColumnOperation>().Select(o => o.Table));

        foreach (var name in names)
        {
            Assert.DoesNotContain(name, DormantLibraryTables, StringComparer.Ordinal);
        }
    }

    [Fact]
    public void Down_RemovesOnlyTheNewObjects()
    {
        var operations = GetMigration().DownOperations;

        Assert.Equal(
            ["ContentPackMemberships", "ContentPacks"],
            operations.OfType<DropTableOperation>().Select(o => o.Name).ToArray());

        var dropped = Assert.Single(operations.OfType<DropColumnOperation>());
        Assert.Equal("Users", dropped.Table);
        Assert.Equal("ContentPackBrowsingPreference", dropped.Name);

        Assert.Equal(3, operations.Count);
    }

    [Fact]
    public void MigratingAnEmptyDatabase_CreatesTheTablesEmptyWithEveryIndex()
    {
        using var ctx = CreateDbContext();
        ctx.Database.Migrate();

        Assert.Empty(ctx.ContentPacks);
        Assert.Empty(ctx.ContentPackMemberships);

        var indexes = ReadIndexNames(ctx);
        Assert.Contains("IX_ContentPacks_NormalizedName", indexes);
        Assert.Contains("IX_ContentPacks_SortOrder", indexes);
        Assert.Contains("IX_ContentPackMemberships_ItemId", indexes);

        // The unique constraint is the thing that decides a same-name race, so its uniqueness is
        // asserted against the live schema rather than against the migration source.
        using var command = ctx.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT \"unique\" FROM pragma_index_list('ContentPacks') WHERE name = 'IX_ContentPacks_NormalizedName'";
        ctx.Database.OpenConnection();
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void MigratingAPopulatedDatabase_LeavesTheExistingRowsAlone()
    {
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        using (var ctx = CreateDbContext())
        {
            // Bring the database up to the state immediately before this migration. The rows are
            // written with raw SQL on purpose: the EF model already knows about the new column, so
            // going through it would not reproduce a genuinely pre-M1 database.
            var migrator = ctx.Database.GetService<IMigrator>();
            migrator.Migrate(PreviousMigrationId(ctx));

            InsertPreMigrationRow(ctx, "Users", new Dictionary<string, object>
            {
                ["Id"] = userId.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
                ["Username"] = "existing",
                ["NormalizedUsername"] = "EXISTING",
                ["AuthenticationProviderId"] = "auth",
                ["PasswordResetProviderId"] = "reset"
            });

            InsertPreMigrationRow(ctx, "BaseItems", new Dictionary<string, object>
            {
                ["Id"] = itemId.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
                ["Type"] = "Tesserafin.Controller.Entities.Movies.Movie",
                ["Name"] = "Untouched"
            });
        }

        using (var ctx = CreateDbContext())
        {
            ctx.Database.Migrate();

            // The pre-existing user is still there, still itself, and reads back as the default —
            // the migration added a column, it did not rewrite anybody.
            var user = ctx.Users.Single(u => u.Username == "existing");
            Assert.Equal("existing", user.Username);
            Assert.Equal(
                global::Tesserafin.Data.Enums.ContentPackBrowsingPreference.MediaFamilyFirst,
                user.ContentPackBrowsingPreference);

            Assert.Equal(itemId, ctx.BaseItems.Single(e => e.Name == "Untouched").Id);

            // The new tables arrive empty even though the database was not.
            Assert.Empty(ctx.ContentPacks);
            Assert.Empty(ctx.ContentPackMemberships);
        }
    }

    private static Migration GetMigration()
    {
        var type = typeof(SqliteDatabaseProvider).Assembly.GetType(MigrationTypeName);
        Assert.NotNull(type);

        return (Migration)Activator.CreateInstance(type)!;
    }

    private static string PreviousMigrationId(TesserafinDbContext ctx)
    {
        var all = ctx.Database.GetMigrations().OrderBy(m => m, StringComparer.Ordinal).ToArray();
        var index = Array.FindIndex(all, m => m.EndsWith("_AddContentPacks", StringComparison.Ordinal));
        Assert.True(index > 0, "AddContentPacks must not be the first migration.");
        return all[index - 1];
    }

#pragma warning disable CA2100 // The table name is a test constant, and every value is a parameter.
    private static void InsertPreMigrationRow(TesserafinDbContext ctx, string table, Dictionary<string, object> explicitValues)
    {
        ctx.Database.OpenConnection();

        var columns = new List<(string Name, string Type)>();
        using (var info = ctx.Database.GetDbConnection().CreateCommand())
        {
            info.CommandText = $"SELECT name, type, \"notnull\" FROM pragma_table_info('{table}')";
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                if (reader.GetInt32(2) == 1 || explicitValues.ContainsKey(name))
                {
                    columns.Add((name, reader.GetString(1)));
                }
            }
        }

        using var insert = ctx.Database.GetDbConnection().CreateCommand();
        var names = string.Join(", ", columns.Select(c => $"\"{c.Name}\""));
        var placeholders = string.Join(", ", columns.Select((c, i) => $"@p{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        insert.CommandText = $"INSERT INTO \"{table}\" ({names}) VALUES ({placeholders})";

        for (var i = 0; i < columns.Count; i++)
        {
            var parameter = insert.CreateParameter();
            parameter.ParameterName = $"@p{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            parameter.Value = explicitValues.TryGetValue(columns[i].Name, out var value)
                ? value
                : columns[i].Type.Equals("TEXT", StringComparison.OrdinalIgnoreCase) ? string.Empty : 0;
            insert.Parameters.Add(parameter);
        }

        insert.ExecuteNonQuery();
    }
#pragma warning restore CA2100

    private static IReadOnlyList<string> ReadIndexNames(TesserafinDbContext ctx)
    {
        var names = new List<string>();
        ctx.Database.OpenConnection();

        using var command = ctx.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND name IS NOT NULL";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private TesserafinDbContext CreateDbContext()
    {
        return new TesserafinDbContext(
            _dbOptions,
            NullLogger<TesserafinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }
}
