using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Tesserafin.Data.Entities;
using Tesserafin.Database.Implementations;
using Tesserafin.Database.Implementations.DbConfiguration;
using Tesserafin.Database.Implementations.Locking;
using Tesserafin.Database.Providers.Sqlite;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Data;

/// <summary>
/// A save that fails inside an explicit transaction, after a statement that already ran, against a
/// migrated SQLite file configured by the production provider. The shape is the one of the
/// repositories that replace a set of rows (delete, then save, then commit): the failure must reach
/// the caller, so that the commit never runs and the deleted rows come back. Runs once per database
/// locking mode an administrator can select.
/// </summary>
public abstract class LockingBehaviorSaveFailureTests : IDisposable
{
    private const string Client = "web";

    private readonly string _directory;
    private readonly string _databasePath;
    private readonly IEntityFrameworkCoreLockingBehavior _locking;
    private readonly DbContextOptions<TesserafinDbContext> _dbOptions;
    private readonly Guid _userId;
    private readonly Guid _itemId = Guid.NewGuid();

    protected LockingBehaviorSaveFailureTests(IEntityFrameworkCoreLockingBehavior locking)
    {
        _locking = locking;
        _directory = Path.Combine(Path.GetTempPath(), "tesserafin-locking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "tesserafin.db");
        var builder = new DbContextOptionsBuilder<TesserafinDbContext>();
        new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance).Initialise(
            builder,
            new DatabaseConfigurationOptions
            {
                DatabaseType = "Tesserafin-SQLite",
                LockingBehavior = DatabaseLockingBehaviorTypes.NoLock,
                CustomProviderOptions = new CustomDatabaseOptions
                {
                    PluginName = "Tesserafin-SQLite",
                    PluginAssembly = string.Empty,
                    ConnectionString = string.Empty,
                    Options =
                    {
                        new CustomDatabaseOption { Key = "path", Value = _databasePath },

                        // A writer that finds the database locked gives up after one second instead of
                        // sixty, so that a lock left behind by a failed save shows as a failure here.
                        new CustomDatabaseOption { Key = "command-timeout", Value = "1" },
                    },
                },
            });
        locking.Initialise(builder);
        _dbOptions = builder.Options;

        using var context = CreateDbContext();
        context.Database.Migrate();
        var user = new User("viewer", "auth", "reset");
        context.Users.Add(user);
        context.SaveChanges();
        _userId = user.Id;
        context.CustomItemDisplayPreferences.AddRange(
            new CustomItemDisplayPreferences(_userId, _itemId, Client, "kept", "before"),
            new CustomItemDisplayPreferences(_userId, _itemId, Client, "also-kept", "before"));
        context.SaveChanges();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void SaveChanges_InjectedFailureAfterDelete_ReachesTheCallerAndRollsBack()
    {
        var failing = new FailingSaveInterceptor();
        Exception? error;
        using (var context = CreateDbContext(failing))
        {
            error = Record.Exception(() => ReplaceSet(context, ("new", "after")));
        }

        Assert.Equal(1, failing.Failures);
        AssertPreviousSetKept();
        Assert.IsType<InvalidOperationException>(error);
        AssertLaterWriteSucceeds();
    }

    [Fact]
    public async Task SaveChangesAsync_InjectedFailureAfterDelete_ReachesTheCallerAndRollsBack()
    {
        var failing = new FailingSaveInterceptor();
        var context = CreateDbContext(failing);
        Exception? error;
        await using (context.ConfigureAwait(false))
        {
            error = await Record.ExceptionAsync(() => ReplaceSetAsync(context, ("new", "after")));
        }

        Assert.Equal(1, failing.Failures);
        AssertPreviousSetKept();
        Assert.IsType<InvalidOperationException>(error);
        AssertLaterWriteSucceeds();
    }

    [Fact]
    public void SaveChanges_ConstraintViolationAfterDelete_ReachesTheCallerAndRollsBack()
    {
        // Two rows for one key break the unique index on (UserId, ItemId, Client, Key): SQLite itself
        // refuses the insert, after the delete of the previous set has run on the same transaction.
        Exception? error;
        using (var context = CreateDbContext())
        {
            error = Record.Exception(() => ReplaceSet(context, ("same", "a"), ("same", "b")));
        }

        AssertPreviousSetKept();
        AssertUniqueViolation(error);
        AssertLaterWriteSucceeds();
    }

    [Fact]
    public async Task SaveChangesAsync_ConstraintViolationAfterDelete_ReachesTheCallerAndRollsBack()
    {
        var context = CreateDbContext();
        Exception? error;
        await using (context.ConfigureAwait(false))
        {
            error = await Record.ExceptionAsync(() => ReplaceSetAsync(context, ("same", "a"), ("same", "b")));
        }

        AssertPreviousSetKept();
        AssertUniqueViolation(error);
        AssertLaterWriteSucceeds();
    }

    [Fact]
    public void SaveChanges_FailureInADisposedTransaction_LeavesNoLockBehind()
    {
        // The shape of MediaStreamRepository.SaveMediaStreams and ChapterRepository.SaveChapters: the
        // transaction is only disposed when the save throws, never rolled back explicitly. Disposing it
        // raises no transaction event: the pessimistic mode must still release its process-wide write
        // lock, or every other thread waits for it forever.
        var failing = new FailingSaveInterceptor();
        Exception? error;
        using (var context = CreateDbContext(failing))
        {
            error = Record.Exception(() =>
            {
                using var transaction = context.Database.BeginTransaction();
                Scope(context).ExecuteDelete();
                context.CustomItemDisplayPreferences.Add(NewRow(("new", "after")));
                context.SaveChanges();
                transaction.Commit();
            });
        }

        AssertPreviousSetKept();
        Assert.IsType<InvalidOperationException>(error);
        AssertLaterWriteSucceeds();
    }

    [Fact]
    public async Task SaveChangesAsync_FailureInADisposedTransaction_LeavesNoLockBehind()
    {
        var failing = new FailingSaveInterceptor();
        var context = CreateDbContext(failing);
        Exception? error;
        await using (context.ConfigureAwait(false))
        {
            error = await Record.ExceptionAsync(async () =>
            {
                var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    await Scope(context).ExecuteDeleteAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
                    context.CustomItemDisplayPreferences.Add(NewRow(("new", "after")));
                    await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
                }
            });
        }

        AssertPreviousSetKept();
        Assert.IsType<InvalidOperationException>(error);
        AssertLaterWriteSucceeds();
    }

    [Fact]
    public void SaveChanges_Success_ReplacesTheSet()
    {
        using (var context = CreateDbContext())
        {
            ReplaceSet(context, ("new", "after"));
        }

        Assert.Equal(new Dictionary<string, string?> { ["new"] = "after" }, ReadSet());
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, true);
        }
    }

    /// <summary>
    /// Holds the write lock of the database file from a connection EF Core does not know about.
    /// </summary>
    /// <returns>The connection; disposing it releases the lock.</returns>
    protected SqliteConnection HoldWriteLock()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "BEGIN IMMEDIATE;";
        command.ExecuteNonQuery();
        return connection;
    }

    protected void ReplaceSetHoldingTheLockFor(TimeSpan duration)
    {
        var holder = HoldWriteLock();
        using var release = new Timer(_ => holder.Dispose(), null, duration, Timeout.InfiniteTimeSpan);
        using var context = CreateDbContext();
        context.CustomItemDisplayPreferences.Add(new CustomItemDisplayPreferences(_userId, _itemId, Client, "added", "while-locked"));
        context.SaveChanges();
    }

    protected Dictionary<string, string?> ReadSet()
    {
        using var context = CreateDbContext();
        return Scope(context)
            .AsNoTracking()
            .ToDictionary(row => row.Key, row => row.Value);
    }

    // Rolls back explicitly on failure, as DisplayPreferencesManager does; the repositories that only
    // dispose the transaction are covered by SaveChanges_FailureInADisposedTransaction_LeavesNoLockBehind.
    private void ReplaceSet(TesserafinDbContext context, params (string Key, string Value)[] rows)
    {
        using var transaction = context.Database.BeginTransaction();
        try
        {
            Scope(context).ExecuteDelete();
            context.CustomItemDisplayPreferences.AddRange(rows.Select(NewRow));
            context.SaveChanges();
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private async Task ReplaceSetAsync(TesserafinDbContext context, params (string Key, string Value)[] rows)
    {
        var transaction = await context.Database.BeginTransactionAsync().ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            try
            {
                await Scope(context).ExecuteDeleteAsync().ConfigureAwait(false);
                context.CustomItemDisplayPreferences.AddRange(rows.Select(NewRow));
                await context.SaveChangesAsync().ConfigureAwait(false);
                await transaction.CommitAsync().ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    private IQueryable<CustomItemDisplayPreferences> Scope(TesserafinDbContext context)
    {
        return context.CustomItemDisplayPreferences
            .Where(row => row.UserId.Equals(_userId) && row.ItemId.Equals(_itemId) && row.Client == Client);
    }

    private CustomItemDisplayPreferences NewRow((string Key, string Value) row)
    {
        return new CustomItemDisplayPreferences(_userId, _itemId, Client, row.Key, row.Value);
    }

    private static void AssertUniqueViolation(Exception? error)
    {
        var update = Assert.IsType<DbUpdateException>(error);
        Assert.Contains("UNIQUE constraint failed", update.InnerException?.Message, StringComparison.Ordinal);
    }

    private void AssertPreviousSetKept()
    {
        Assert.Equal(
            new Dictionary<string, string?> { ["kept"] = "before", ["also-kept"] = "before" },
            ReadSet());
    }

    private void AssertLaterWriteSucceeds()
    {
        // With a one-second busy timeout, a transaction left open by the failed save would make this
        // write fail with "database is locked" in every mode but the optimistic one, and make the
        // optimistic one retry for seconds; it would also leave the process-wide lock of the
        // pessimistic mode held by the failed writer. That lock is reentrant for the thread that holds
        // it, so the write runs on another thread.
        Exception? error = null;
        var writer = new Thread(() =>
        {
            try
            {
                using var context = CreateDbContext();
                context.CustomItemDisplayPreferences.Add(new CustomItemDisplayPreferences(_userId, _itemId, Client, "later", "write"));
                context.SaveChanges();
            }
            catch (Exception e)
            {
                error = e;
            }
        })
        {
            IsBackground = true,
        };
        writer.Start();
        Assert.True(writer.Join(TimeSpan.FromSeconds(10)), "A write from another thread is still blocked after the failed save.");
        Assert.Null(error);
        Assert.Equal("write", ReadSet()["later"]);
    }

    private TesserafinDbContext CreateDbContext(params IInterceptor[] interceptors)
    {
        var options = interceptors.Length == 0
            ? _dbOptions
            : new DbContextOptionsBuilder<TesserafinDbContext>(_dbOptions).AddInterceptors(interceptors).Options;
        return new TesserafinDbContext(
            options,
            NullLogger<TesserafinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            _locking);
    }

    // The three modes are nested deliberately: each one only passes its locking behavior to the base
    // class, and SA1402 forbids a second type in this file. xUnit needs test classes public, so the
    // accessibility escape hatch CA1034 offers is closed; suppressed as in PlaybackSessionsControllerTests.
#pragma warning disable CA1034 // Nested types should not be visible

    /// <summary>
    /// The default mode.
    /// </summary>
    public sealed class NoLock : LockingBehaviorSaveFailureTests
    {
        public NoLock()
            : base(new NoLockBehavior(NullLogger<NoLockBehavior>.Instance))
        {
        }
    }

    /// <summary>
    /// A process-wide reader/writer lock around commands and transactions.
    /// </summary>
    public sealed class Pessimistic : LockingBehaviorSaveFailureTests
    {
        public Pessimistic()
            : base(new PessimisticLockBehavior(NullLogger<PessimisticLockBehavior>.Instance, NullLoggerFactory.Instance))
        {
        }
    }

    /// <summary>
    /// Retries of commands and transactions that find the database locked.
    /// </summary>
    public sealed class Optimistic : LockingBehaviorSaveFailureTests
    {
        public Optimistic()
            : base(new OptimisticLockBehavior(NullLogger<OptimisticLockBehavior>.Instance))
        {
        }

        [Fact]
        public void SaveChanges_DatabaseLockedForLongerThanTheBusyTimeout_IsRetriedAndSucceeds()
        {
            // The holder keeps the lock past the one-second busy timeout, so the first attempt fails
            // with "database is locked" and only a retry can store the row.
            ReplaceSetHoldingTheLockFor(TimeSpan.FromMilliseconds(1500));

            Assert.Equal("while-locked", ReadSet()["added"]);
        }
    }

#pragma warning restore CA1034

    /// <summary>
    /// Fails the first save of the context, after any statement the caller ran before it.
    /// </summary>
    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public int Failures { get; private set; }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Failures++;
            throw new InvalidOperationException("Injected save failure.");
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Failures++;
            throw new InvalidOperationException("Injected save failure.");
        }
    }
}
