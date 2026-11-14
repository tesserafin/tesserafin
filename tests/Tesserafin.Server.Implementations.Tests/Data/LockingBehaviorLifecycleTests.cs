using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Tesserafin.Data.Entities;
using Tesserafin.Database.Implementations;
using Tesserafin.Database.Implementations.DbConfiguration;
using Tesserafin.Database.Implementations.Locking;
using Tesserafin.Database.Providers.Sqlite;
using Xunit;

// The synchronous and asynchronous paths are both under test, in the same scenarios on purpose.
#pragma warning disable CA1849 // Call async methods when in an async method

namespace Tesserafin.Server.Implementations.Tests.Data;

/// <summary>
/// The lifecycle of an explicit transaction under each database locking mode, against a migrated
/// SQLite file configured by the production provider: every way a transaction can end must release
/// whatever the mode holds for it, including when the asynchronous flow that started the transaction
/// resumes on another thread before it ends. Every scenario runs under a deadline, and is followed by
/// a write from another thread under a deadline, so that a lock left behind fails the test instead of
/// suspending the suite.
/// </summary>
public abstract class LockingBehaviorLifecycleTests : IDisposable
{
    private const string Client = "web";

    private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(20);

    private readonly string _directory;
    private readonly string _databasePath;
    private readonly IEntityFrameworkCoreLockingBehavior _locking;
    private readonly DbContextOptions<TesserafinDbContext> _dbOptions;
    private readonly Guid _userId;
    private readonly Guid _itemId = Guid.NewGuid();

    protected LockingBehaviorLifecycleTests(IEntityFrameworkCoreLockingBehavior locking)
    {
        _locking = locking;
        _directory = Path.Combine(Path.GetTempPath(), "tesserafin-lock-lifecycle-" + Guid.NewGuid().ToString("N"));
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
                    },
                },
            });
        var seedOptions = new DbContextOptionsBuilder<TesserafinDbContext>(builder.Options).Options;
        locking.Initialise(builder);
        _dbOptions = builder.Options;

        // Seeded without the mode under test: a lock leaked by an earlier test of the same mode is
        // process-wide, and must fail that mode's tests under their deadline, not hang this constructor.
        using var context = new TesserafinDbContext(
            seedOptions,
            NullLogger<TesserafinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
        context.Database.Migrate();
        var user = new User("viewer", "auth", "reset");
        context.Users.Add(user);
        context.SaveChanges();
        _userId = user.Id;
        context.CustomItemDisplayPreferences.AddRange(NewRow(_itemId, "kept"), NewRow(_itemId, "also-kept"));
        context.SaveChanges();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Commit_ReplacesTheSetAndReleases()
    {
        Run(() =>
        {
            using var context = CreateDbContext();
            using var transaction = context.Database.BeginTransaction();
            ReplaceSet(context, "new");
            transaction.Commit();
        });
    }

    [Fact]
    public void Rollback_KeepsTheSetAndReleases()
    {
        Run(
            () =>
            {
                using var context = CreateDbContext();
                using var transaction = context.Database.BeginTransaction();
                ReplaceSet(context, "new");
                transaction.Rollback();
            },
            committed: false);
    }

    [Fact]
    public void DisposeAfterException_KeepsTheSetAndReleases()
    {
        Run(
            () =>
            {
                using var context = CreateDbContext();
                Assert.Throws<InvalidOperationException>(new Action(() =>
                {
                    using var transaction = context.Database.BeginTransaction();
                    ReplaceSet(context, "new");
                    throw new InvalidOperationException("Failure before the commit.");
                }));
            },
            committed: false);
    }

    [Fact]
    public Task CommitAsync_AcrossThreads_ReplacesTheSetAndReleases()
    {
        return RunAsync(async threads =>
        {
            var context = CreateDbContext();
            await using (context.ConfigureAwait(false))
            {
                var transaction = await context.Database.BeginTransactionAsync().ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    await threads.SwitchThread();
                    await ReplaceSetAsync(context, "new").ConfigureAwait(false);
                    await threads.SwitchThread();
                    await transaction.CommitAsync().ConfigureAwait(false);
                }
            }
        });
    }

    [Fact]
    public Task RollbackAsync_AcrossThreads_KeepsTheSetAndReleases()
    {
        return RunAsync(
            async threads =>
            {
                var context = CreateDbContext();
                await using (context.ConfigureAwait(false))
                {
                    var transaction = await context.Database.BeginTransactionAsync().ConfigureAwait(false);
                    await using (transaction.ConfigureAwait(false))
                    {
                        await threads.SwitchThread();
                        await ReplaceSetAsync(context, "new").ConfigureAwait(false);
                        await threads.SwitchThread();
                        await transaction.RollbackAsync().ConfigureAwait(false);
                    }
                }
            },
            committed: false);
    }

    [Fact]
    public Task DisposeAsyncAfterException_AcrossThreads_KeepsTheSetAndReleases()
    {
        return RunAsync(
            async threads =>
            {
                var context = CreateDbContext();
                await using (context.ConfigureAwait(false))
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    {
                        var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
                        await using (transaction.ConfigureAwait(false))
                        {
                            await threads.SwitchThread();
                            await ReplaceSetAsync(context, "new").ConfigureAwait(false);
                            await threads.SwitchThread();
                            throw new InvalidOperationException("Failure before the commit.");
                        }
                    }).ConfigureAwait(false);
                }
            },
            committed: false);
    }

    [Fact]
    public Task CancellationBeforeCommit_AcrossThreads_KeepsTheSetAndReleases()
    {
        return RunAsync(
            async threads =>
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                var context = CreateDbContext();
                await using (context.ConfigureAwait(false))
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    {
                        var transaction = await context.Database.BeginTransactionAsync(cancellation.Token).ConfigureAwait(false);
                        await using (transaction.ConfigureAwait(false))
                        {
                            await threads.SwitchThread();
                            await Scope(context, _itemId).ExecuteDeleteAsync(cancellation.Token).ConfigureAwait(false);
                            context.CustomItemDisplayPreferences.Add(NewRow(_itemId, "new"));
                            await cancellation.CancelAsync().ConfigureAwait(false);
                            await context.SaveChangesAsync(cancellation.Token).ConfigureAwait(false);
                            await transaction.CommitAsync(cancellation.Token).ConfigureAwait(false);
                        }
                    }).ConfigureAwait(false);
                }
            },
            committed: false);
    }

    [Fact]
    public void DisposeWithConnectionOpenedByTheCaller_ReleasesBeforeTheContextEnds()
    {
        // The connection stays open after the transaction, so closing it cannot be what releases the
        // lock: the write from another thread runs while this context is still alive.
        Run(
            () =>
            {
                using var context = CreateDbContext();
                context.Database.OpenConnection();
                using (var transaction = context.Database.BeginTransaction())
                {
                    ReplaceSet(context, "new");
                }

                AssertWriteFromAnotherThreadSucceeds("while-open");
                Assert.Equal(System.Data.ConnectionState.Open, context.Database.GetDbConnection().State);
            },
            committed: false);
    }

    [Fact]
    public Task ContextReusedAfterTheTransaction_WritesAndReleases()
    {
        return RunAsync(async threads =>
        {
            var context = CreateDbContext();
            await using (context.ConfigureAwait(false))
            {
                var transaction = await context.Database.BeginTransactionAsync().ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    await ReplaceSetAsync(context, "new").ConfigureAwait(false);
                    await threads.SwitchThread();
                    await transaction.CommitAsync().ConfigureAwait(false);
                }

                await threads.SwitchThread();
                context.CustomItemDisplayPreferences.Add(NewRow(_itemId, "after-transaction"));
                await context.SaveChangesAsync().ConfigureAwait(false);
                context.CustomItemDisplayPreferences.Add(NewRow(_itemId, "after-transaction-sync"));
                context.SaveChanges();
            }

            Assert.Equal(["after-transaction", "after-transaction-sync", "new"], ReadSet(_itemId));
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public Task DisposingAnEndedTransactionAgain_LeavesTheNextTransactionItsLock(bool firstDisposeAsync, bool secondDisposeAsync)
    {
        return RunAsync(async threads =>
        {
            var context = CreateDbContext();
            await using (context.ConfigureAwait(false))
            {
                var earlier = await context.Database.BeginTransactionAsync().ConfigureAwait(false);
                await earlier.CommitAsync().ConfigureAwait(false);
                await DisposeTransaction(earlier, firstDisposeAsync).ConfigureAwait(false);

                var next = await context.Database.BeginTransactionAsync().ConfigureAwait(false);
                await using (next.ConfigureAwait(false))
                {
                    await threads.SwitchThread();
                    await DisposeTransaction(earlier, secondDisposeAsync).ConfigureAwait(false);
                    await AssertNextTransactionKeepsOthersOut(context, next).ConfigureAwait(false);
                }
            }
        });
    }

    [Fact]
    public async Task ConcurrentTransactions_FromDistinctThreads_AllCommit()
    {
        const int Writers = 6;
        const int Rounds = 8;
        var items = Enumerable.Range(0, Writers).Select(_ => Guid.NewGuid()).ToArray();
        var threads = new ThreadSwitcher();
        var writers = items.Select(item => Task.Run(async () =>
        {
            for (var round = 0; round < Rounds; round++)
            {
                var context = CreateDbContext();
                await using (context.ConfigureAwait(false))
                {
                    var transaction = await context.Database.BeginTransactionAsync().ConfigureAwait(false);
                    await using (transaction.ConfigureAwait(false))
                    {
                        await threads.SwitchThread();
                        await Scope(context, item).ExecuteDeleteAsync().ConfigureAwait(false);
                        context.CustomItemDisplayPreferences.Add(NewRow(item, "round-" + round));
                        await context.SaveChangesAsync().ConfigureAwait(false);
                        await transaction.CommitAsync().ConfigureAwait(false);
                    }
                }

                // A synchronous writer between the asynchronous ones.
                using (var syncContext = CreateDbContext())
                {
                    using var transaction = syncContext.Database.BeginTransaction();
                    Scope(syncContext, item).ExecuteDelete();
                    syncContext.CustomItemDisplayPreferences.Add(NewRow(item, "sync-" + round));
                    syncContext.SaveChanges();
                    transaction.Commit();
                }
            }
        }));

        await Task.WhenAll(writers).WaitAsync(_deadline * 2, TestContext.Current.CancellationToken);

        foreach (var item in items)
        {
            Assert.Equal(["sync-" + (Rounds - 1)], ReadSet(item));
        }

        Assert.True(threads.Switches > 0, "No continuation ran on another thread.");
        AssertWriteFromAnotherThreadSucceeds("later");
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, true);
        }
    }

    private void Run(Action scenario, bool committed = true)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                scenario();
            }
            catch (Exception e)
            {
                error = e;
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        Assert.True(thread.Join(_deadline), "The scenario is still blocked.");
        Assert.Null(error);
        AssertOutcome(committed);
    }

    private async Task RunAsync(Func<ThreadSwitcher, Task> scenario, bool committed = true)
    {
        var threads = new ThreadSwitcher();
        await Task.Run(() => scenario(threads)).WaitAsync(_deadline, TestContext.Current.CancellationToken);
        Assert.True(threads.Switches > 0, "No continuation ran on another thread.");
        AssertOutcome(committed);
    }

    private void AssertOutcome(bool committed)
    {
        // First, under its deadline: a read on this thread would wait forever for a leaked lock.
        AssertWriteFromAnotherThreadSucceeds("later");
        var set = ReadSet(_itemId);
        if (committed)
        {
            Assert.Contains("new", set);
            Assert.DoesNotContain("kept", set);
        }
        else
        {
            Assert.Contains("kept", set);
            Assert.Contains("also-kept", set);
            Assert.DoesNotContain("new", set);
        }
    }

    private void AssertWriteFromAnotherThreadSucceeds(string key)
    {
        StartWriteFromAnotherThread(key).AssertSucceeded();
        Assert.Contains(key, ReadSet(_itemId));
    }

    /// <summary>
    /// The transaction a context began after an earlier one ended must keep every other connection out
    /// until it ends, whatever happened to the earlier transaction since.
    /// </summary>
    private async Task AssertNextTransactionKeepsOthersOut(TesserafinDbContext context, IDbContextTransaction next)
    {
        var writer = StartWriteFromAnotherThread("concurrent");

        // Time for the writer to reach the lock: it must still be waiting once the transaction is done.
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken).ConfigureAwait(false);
        await ReplaceSetAsync(context, "new").ConfigureAwait(false);
        Assert.False(writer.IsCompleted, "A write from another connection ran inside the next transaction.");
        await next.CommitAsync().ConfigureAwait(false);

        writer.AssertSucceeded();
        Assert.Equal(["concurrent", "concurrent-bare", "new"], ReadSet(_itemId));
    }

    private BackgroundWrite StartWriteFromAnotherThread(string key)
    {
        // A lock that is reentrant for the thread holding it hides a leak from that thread, so the
        // write runs on a thread of its own, both through a transaction and through a bare save.
        return new BackgroundWrite(() =>
        {
            using var context = CreateDbContext();
            using (var transaction = context.Database.BeginTransaction())
            {
                context.CustomItemDisplayPreferences.Add(NewRow(_itemId, key));
                context.SaveChanges();
                transaction.Commit();
            }

            context.CustomItemDisplayPreferences.Add(NewRow(_itemId, key + "-bare"));
            context.SaveChanges();
        });
    }

    private static async Task DisposeTransaction(IDbContextTransaction transaction, bool disposeAsync)
    {
        if (disposeAsync)
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            transaction.Dispose();
        }
    }

    private void ReplaceSet(TesserafinDbContext context, string key)
    {
        Scope(context, _itemId).ExecuteDelete();
        context.CustomItemDisplayPreferences.Add(NewRow(_itemId, key));
        context.SaveChanges();
    }

    private async Task ReplaceSetAsync(TesserafinDbContext context, string key)
    {
        await Scope(context, _itemId).ExecuteDeleteAsync().ConfigureAwait(false);
        context.CustomItemDisplayPreferences.Add(NewRow(_itemId, key));
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private List<string> ReadSet(Guid itemId)
    {
        using var context = CreateDbContext();
        return Scope(context, itemId).AsNoTracking().Select(row => row.Key).OrderBy(key => key).ToList();
    }

    private IQueryable<CustomItemDisplayPreferences> Scope(TesserafinDbContext context, Guid itemId)
    {
        return context.CustomItemDisplayPreferences
            .Where(row => row.UserId.Equals(_userId) && row.ItemId.Equals(itemId) && row.Client == Client);
    }

    private CustomItemDisplayPreferences NewRow(Guid itemId, string key)
    {
        return new CustomItemDisplayPreferences(_userId, itemId, Client, key, "value");
    }

    private TesserafinDbContext CreateDbContext()
    {
        return new TesserafinDbContext(
            _dbOptions,
            NullLogger<TesserafinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            _locking);
    }

    // Nested as in LockingBehaviorSaveFailureTests: each mode only passes its locking behavior.
#pragma warning disable CA1034 // Nested types should not be visible

    /// <summary>
    /// The default mode.
    /// </summary>
    public sealed class NoLock : LockingBehaviorLifecycleTests
    {
        public NoLock()
            : base(new NoLockBehavior(NullLogger<NoLockBehavior>.Instance))
        {
        }
    }

    /// <summary>
    /// A process-wide reader/writer lock around commands and transactions.
    /// </summary>
    public sealed class Pessimistic : LockingBehaviorLifecycleTests
    {
        public Pessimistic()
            : base(new PessimisticLockBehavior(NullLogger<PessimisticLockBehavior>.Instance, NullLoggerFactory.Instance))
        {
        }
    }

    /// <summary>
    /// Retries of commands and transactions that find the database locked.
    /// </summary>
    public sealed class Optimistic : LockingBehaviorLifecycleTests
    {
        public Optimistic()
            : base(new OptimisticLockBehavior(NullLogger<OptimisticLockBehavior>.Instance))
        {
        }
    }

#pragma warning restore CA1034

    /// <summary>
    /// A write that runs on a thread of its own, joined under the deadline.
    /// </summary>
    private sealed class BackgroundWrite
    {
        private readonly Thread _thread;
        private Exception? _error;
        private volatile bool _completed;

        public BackgroundWrite(Action write)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    write();
                }
                catch (Exception e)
                {
                    _error = e;
                }
                finally
                {
                    _completed = true;
                }
            })
            {
                IsBackground = true,
            };
            _thread.Start();
        }

        public bool IsCompleted => _completed;

        public void AssertSucceeded()
        {
            Assert.True(_thread.Join(_deadline), "A write from another thread is still blocked.");
            Assert.Null(_error);
        }
    }

    /// <summary>
    /// Resumes the awaiting flow on a thread created for it, so that the flow really changes thread
    /// instead of depending on where the thread pool happens to schedule a continuation.
    /// </summary>
    private sealed class ThreadSwitcher
    {
        private int _switches;

        public int Switches => _switches;

        public Awaitable SwitchThread() => new(this);

        public readonly struct Awaitable : ICriticalNotifyCompletion
        {
            private readonly ThreadSwitcher _owner;

            public Awaitable(ThreadSwitcher owner)
            {
                _owner = owner;
            }

            public bool IsCompleted => false;

            public Awaitable GetAwaiter() => this;

            public void GetResult()
            {
            }

            public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);

            public void UnsafeOnCompleted(Action continuation)
            {
                var owner = _owner;
                var origin = Environment.CurrentManagedThreadId;
                new Thread(() =>
                {
                    if (Environment.CurrentManagedThreadId != origin)
                    {
                        Interlocked.Increment(ref owner._switches);
                    }

                    continuation();
                })
                {
                    IsBackground = true,
                }.Start();
            }
        }
    }
}
