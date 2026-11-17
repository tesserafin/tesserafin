using System;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Database.Implementations;
using Tesserafin.Database.Implementations.Locking;
using Tesserafin.Database.Providers.Sqlite;
using Tesserafin.Server.Implementations.Item;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.ContentPacks;

/// <summary>
/// Deleting an item through the supported deletion path must take its membership rows with it.
/// The path uses per-table <c>ExecuteDelete</c> rather than tracked entities, so an EF-level
/// cascade alone would not fire — this drives the real service to prove the rows actually go.
/// </summary>
public sealed class ContentPackItemDeletionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<TesserafinDbContext> _dbOptions;
    private readonly ItemPersistenceService _service;

    public ContentPackItemDeletionTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<TesserafinDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var ctx = CreateDbContext())
        {
            ctx.Database.EnsureCreated();
        }

        var factory = new Mock<IDbContextFactory<TesserafinDbContext>>();
        factory.Setup(f => f.CreateDbContext()).Returns(CreateDbContext);

        _service = new ItemPersistenceService(
            factory.Object,
            new Mock<IServerApplicationHost>().Object,
            NullLogger<ItemPersistenceService>.Instance,
            Mock.Of<Tesserafin.Controller.Library.IItemReadServices>());
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public void DeleteItem_RemovesItsMembershipsAndLeavesEveryOtherOneIntact()
    {
        var packA = Guid.NewGuid();
        var packB = Guid.NewGuid();
        var deleted = Guid.NewGuid();
        var kept = Guid.NewGuid();

        using (var ctx = CreateDbContext())
        {
            ctx.BaseItems.Add(Item(deleted, "Doomed"));
            ctx.BaseItems.Add(Item(kept, "Kept"));
            ctx.ContentPacks.Add(Pack(packA, "Sport"));
            ctx.ContentPacks.Add(Pack(packB, "Concerts"));

            // The doomed item is in both packs; the kept item shares one of them.
            ctx.ContentPackMemberships.Add(Membership(packA, deleted));
            ctx.ContentPackMemberships.Add(Membership(packB, deleted));
            ctx.ContentPackMemberships.Add(Membership(packA, kept));
            ctx.SaveChanges();
        }

        _service.DeleteItem(deleted);

        using (var ctx = CreateDbContext())
        {
            Assert.Empty(ctx.ContentPackMemberships.Where(e => e.ItemId.Equals(deleted)));

            // No dangling reference is left, and nothing else moved.
            var remaining = ctx.ContentPackMemberships.ToList();
            Assert.Single(remaining);
            Assert.Equal(packA, remaining[0].PackId);
            Assert.Equal(kept, remaining[0].ItemId);

            // Both packs are still valid rows; a pack does not die with its content.
            Assert.Equal(2, ctx.ContentPacks.Count());
            Assert.Single(ctx.BaseItems.Where(e => e.Id.Equals(kept)));
            Assert.Empty(ctx.BaseItems.Where(e => e.Id.Equals(deleted)));
        }
    }

    private static ContentPack Pack(Guid id, string name) => new()
    {
        Id = id,
        Name = name,
        NormalizedName = ContentPack.Normalize(name),
        SortOrder = 0,
        Origin = ContentPackOrigin.Manual,
        DateCreated = DateTime.UtcNow
    };

    private static ContentPackMembership Membership(Guid packId, Guid itemId) => new()
    {
        PackId = packId,
        ItemId = itemId,
        Provenance = ContentPackMembershipProvenance.Manual,
        DateCreated = DateTime.UtcNow
    };

    private static BaseItemEntity Item(Guid id, string name) => new()
    {
        Id = id,
        Type = "Tesserafin.Controller.Entities.Movies.Movie",
        Name = name
    };

    private TesserafinDbContext CreateDbContext()
    {
        return new TesserafinDbContext(
            _dbOptions,
            NullLogger<TesserafinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }
}
