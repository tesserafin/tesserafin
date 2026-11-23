using System;
using System.Linq;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Data.Entities;
using Tesserafin.Database.Implementations.Locking;
using Tesserafin.Model.Entities;
using Tesserafin.Server.Implementations.Item;
using Tesserafin.Server.Implementations.Tests.Data;
using Tesserafin.Server.Implementations.Tests.Library.LibraryManager;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Item;

/// <summary>
/// The deletion of an item, which detaches its users' histories under the placeholder, and their
/// reattachment to an imported item, failing after a statement already changed rows: under each
/// database locking mode, on a migrated SQLite file, from a thread of their own under a deadline. The
/// policies themselves (arbitration, retention, one history per user) are covered by
/// <see cref="ItemPersistenceConservationTests"/>; this covers what the locking modes add.
/// </summary>
[Collection(LibraryManagerStaticStateFixture.Name)]
public abstract class UserDataLockingModeTests : IDisposable
{
    private const string ImdbKey = "tt0351351";
    private const string PlaceholderKey = "00000000-0000-0000-0000-000000000001";

    private readonly MigratedSqliteDatabase _database;
    private readonly ItemPersistenceService _service;
    private readonly Guid _userId;
    private readonly Guid _otherUserId;

    protected UserDataLockingModeTests(IEntityFrameworkCoreLockingBehavior locking)
    {
        _database = new MigratedSqliteDatabase(locking);
        using (var context = _database.CreateDbContext())
        {
            var user = new User("viewer", "auth", "reset");
            var otherUser = new User("other-viewer", "auth", "reset");
            context.Users.AddRange(user, otherUser);
            context.SaveChanges();
            _userId = user.Id;
            _otherUserId = otherUser.Id;
        }

        var appHost = new Mock<IServerApplicationHost>();
        appHost.Setup(value => value.ReverseVirtualPath(It.IsAny<string>())).Returns((string path) => path);
        _service = new ItemPersistenceService(_database.Factory(), appHost.Object, NullLogger<ItemPersistenceService>.Instance, Mock.Of<Tesserafin.Controller.Library.IItemReadServices>());
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void DeleteItem_FailureAfterTheHistoriesAreDetached_RestoresThemAndTheNextDeletionSucceeds()
    {
        var deleted = SeedItem("deleted");
        var kept = SeedItem("kept");
        foreach (var item in new[] { deleted, kept })
        {
            AddHistory(item, _userId, "key-" + item, playCount: 3);
            AddHistory(item, _otherUserId, "key-" + item, playCount: 5);
        }

        // Fails the delete of the item itself, once both histories were moved to the placeholder.
        Execute($"""
            CREATE TRIGGER fail_after_detach BEFORE DELETE ON BaseItems
            WHEN OLD.Name = 'deleted'
              AND (SELECT COUNT(*) FROM UserData WHERE ItemId = '{PlaceholderKey}') = 2
            BEGIN
              SELECT RAISE(ABORT, 'after-detach');
            END;
            """);
        var before = Histories();

        var error = MigratedSqliteDatabase.RunOnAnotherThread(() => _service.DeleteItem(deleted));

        Assert.Contains("after-detach", error?.GetBaseException().Message, StringComparison.Ordinal);
        Assert.Equal(before, Histories());

        Execute("DROP TRIGGER fail_after_detach;");
        Assert.Null(MigratedSqliteDatabase.RunOnAnotherThread(() => _service.DeleteItem(deleted)));
        var after = Histories();
        Assert.Equal(2, after.Count(row => row.StartsWith(PlaceholderKey, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(before.Where(row => row.StartsWith(kept.ToString(), StringComparison.OrdinalIgnoreCase)), after.Where(row => row.StartsWith(kept.ToString(), StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ReattachUserData_FailureAfterTheFirstRow_RestoresTheHistoriesAndTheNextReattachSucceeds()
    {
        var detachedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        AddHistory(BaseItemRepository.PlaceholderId, _userId, ImdbKey, playCount: 3, retention: detachedAt);
        AddHistory(BaseItemRepository.PlaceholderId, _otherUserId, ImdbKey, playCount: 5, retention: detachedAt);
        var movie = new Movie { Id = SeedItem("imported"), Name = "imported", SortName = "imported" };
        movie.SetProviderId(MetadataProvider.Imdb, ImdbKey);
        Execute($"""
            CREATE TRIGGER fail_after_first_reattach AFTER UPDATE ON UserData
            WHEN NEW.ItemId <> '{PlaceholderKey}'
              AND (SELECT COUNT(*) FROM UserData WHERE ItemId = NEW.ItemId) = 2
            BEGIN
              SELECT RAISE(ABORT, 'after-first-reattach');
            END;
            """);
        var before = Histories();

        var error = MigratedSqliteDatabase.RunOnAnotherThread(() => _service.ReattachUserDataAsync(movie, CancellationToken.None).GetAwaiter().GetResult());

        Assert.Contains("after-first-reattach", error?.GetBaseException().Message, StringComparison.Ordinal);
        Assert.Equal(before, Histories());

        Execute("DROP TRIGGER fail_after_first_reattach;");
        Assert.Null(MigratedSqliteDatabase.RunOnAnotherThread(() => _service.ReattachUserDataAsync(movie, CancellationToken.None).GetAwaiter().GetResult()));
        Assert.Equal(2, Histories().Count(row => row.StartsWith(movie.Id.ToString(), StringComparison.OrdinalIgnoreCase)));
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _database.Dispose();
        }
    }

    private Guid SeedItem(string name)
    {
        var id = Guid.NewGuid();
        using var context = _database.CreateDbContext();
        context.BaseItems.Add(new BaseItemEntity { Id = id, Name = name, Type = typeof(Movie).FullName! });
        context.SaveChanges();
        return id;
    }

    private void AddHistory(Guid itemId, Guid userId, string key, int playCount, DateTime? retention = null)
    {
        using var context = _database.CreateDbContext();
        context.UserData.Add(new UserData
        {
            ItemId = itemId,
            Item = null,
            UserId = userId,
            User = null,
            CustomDataKey = key,
            PlayCount = playCount,
            RetentionDate = retention,
        });
        context.SaveChanges();
    }

    private void Execute(string sql)
    {
        using var context = _database.CreateDbContext();
        context.Database.ExecuteSqlRaw(sql);
    }

    private string[] Histories()
    {
        // Read from a new connection: what a rolled back transaction left is what any reader sees.
        using var context = _database.CreateDbContext();
        return context.UserData
            .AsNoTracking()
            .AsEnumerable()
            .Select(row => FormattableString.Invariant($"{row.ItemId}|{row.UserId}|{row.CustomDataKey}|{row.PlayCount}|{row.RetentionDate:O}"))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

#pragma warning disable CA1034 // Nested types should not be visible

    /// <summary>
    /// The default mode.
    /// </summary>
    [Collection(LibraryManagerStaticStateFixture.Name)]
    public sealed class NoLock : UserDataLockingModeTests
    {
        public NoLock()
            : base(new NoLockBehavior(NullLogger<NoLockBehavior>.Instance))
        {
        }
    }

    /// <summary>
    /// A process-wide reader/writer lock around commands and transactions.
    /// </summary>
    [Collection(LibraryManagerStaticStateFixture.Name)]
    public sealed class Pessimistic : UserDataLockingModeTests
    {
        public Pessimistic()
            : base(new PessimisticLockBehavior(NullLogger<PessimisticLockBehavior>.Instance, NullLoggerFactory.Instance))
        {
        }
    }

    /// <summary>
    /// Retries of commands and transactions that find the database locked.
    /// </summary>
    [Collection(LibraryManagerStaticStateFixture.Name)]
    public sealed class Optimistic : UserDataLockingModeTests
    {
        public Optimistic()
            : base(new OptimisticLockBehavior(NullLogger<OptimisticLockBehavior>.Instance))
        {
        }
    }

#pragma warning restore CA1034
}
