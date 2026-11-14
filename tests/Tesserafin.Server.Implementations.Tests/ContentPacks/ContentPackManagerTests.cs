using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.ContentPacks;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Database.Implementations;
using Tesserafin.Database.Implementations.Locking;
using Tesserafin.Database.Providers.Sqlite;
using Tesserafin.Server.Implementations.ContentPacks;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.ContentPacks;

/// <summary>
/// Storage-level behaviour of content packs against a real SQLite file, so the unique index, the
/// composite key and the foreign keys are the things actually under test.
/// </summary>
public sealed class ContentPackManagerTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DbContextOptions<TesserafinDbContext> _dbOptions;
    private readonly ContentPackManager _manager;

    public ContentPackManagerTests()
    {
        // A file, not :memory:, so several connections see one database and the concurrency cases
        // below are genuine races rather than a single shared in-process handle.
        _databasePath = Path.Combine(Path.GetTempPath(), $"tesserafin-packs-{Guid.NewGuid():N}.db");

        _dbOptions = new DbContextOptionsBuilder<TesserafinDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        using (var ctx = CreateDbContext())
        {
            ctx.Database.EnsureCreated();
        }

        var factory = new Mock<IDbContextFactory<TesserafinDbContext>>();
        factory.Setup(f => f.CreateDbContext()).Returns(CreateDbContext);
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateDbContext);

        _manager = new ContentPackManager(factory.Object);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    [Fact]
    public async Task CreatePack_AssignsContiguousOrderingAndStableId()
    {
        var first = await _manager.CreatePackAsync("Sport", null, ContentPackOrigin.Manual, Ct);
        var second = await _manager.CreatePackAsync("Concerts", "Live music", ContentPackOrigin.SystemSeed, Ct);

        Assert.Equal(0, first.SortOrder);
        Assert.Equal(1, second.SortOrder);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(ContentPackOrigin.Manual, first.Origin);
        Assert.Equal(ContentPackOrigin.SystemSeed, second.Origin);
        Assert.Equal("Live music", second.Description);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreatePack_RejectsEmptyName(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _manager.CreatePackAsync(name, null, ContentPackOrigin.Manual, Ct));
    }

    [Fact]
    public async Task CreatePack_RejectsOverlongName()
    {
        var name = new string('x', ContentPack.NameMaxLength + 1);
        await Assert.ThrowsAsync<ArgumentException>(() => _manager.CreatePackAsync(name, null, ContentPackOrigin.Manual, Ct));
    }

    [Fact]
    public async Task CreatePack_TrimsNameAndNormalizesCultureIndependently()
    {
        var pack = await _manager.CreatePackAsync("  Sport  ", null, ContentPackOrigin.Manual, Ct);

        Assert.Equal("Sport", pack.Name);
        Assert.Equal("SPORT", pack.NormalizedName);
    }

    [Fact]
    public async Task CreatePack_RejectsDuplicateNormalizedName()
    {
        await _manager.CreatePackAsync("Sport", null, ContentPackOrigin.Manual, Ct);

        await Assert.ThrowsAsync<ContentPackNameConflictException>(
            () => _manager.CreatePackAsync("  sport ", null, ContentPackOrigin.Manual, Ct));
    }

    [Fact]
    public async Task CreatePack_ConcurrentSameName_LeavesOneRowAndOneConflict()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => RunCreateAsync("Sport")));

        Assert.Equal(1, results.Count(r => r is null));
        Assert.Equal(7, results.Count(r => r is ContentPackNameConflictException));

        var packs = await _manager.GetPacksAsync(Ct);
        Assert.Single(packs);
    }

    [Fact]
    public async Task UpdatePack_KeepsIdentityAndIsIdempotent()
    {
        var created = await _manager.CreatePackAsync("Sport", null, ContentPackOrigin.Manual, Ct);

        var renamed = await _manager.UpdatePackAsync(created.Id, "Sports", "All of it", Ct);
        var again = await _manager.UpdatePackAsync(created.Id, "Sports", "All of it", Ct);

        Assert.Equal(created.Id, renamed.Id);
        Assert.Equal(created.Id, again.Id);
        Assert.Equal("Sports", again.Name);
        Assert.Equal("SPORTS", again.NormalizedName);
        Assert.Equal("All of it", again.Description);
        Assert.Equal(created.SortOrder, again.SortOrder);
    }

    [Fact]
    public async Task UpdatePack_UnknownPackThrowsNotFound()
    {
        await Assert.ThrowsAsync<ContentPackNotFoundException>(
            () => _manager.UpdatePackAsync(Guid.NewGuid(), "Sport", null, Ct));
    }

    [Fact]
    public async Task UpdatePack_RejectsAnotherPacksName()
    {
        await _manager.CreatePackAsync("Sport", null, ContentPackOrigin.Manual, Ct);
        var other = await _manager.CreatePackAsync("Concerts", null, ContentPackOrigin.Manual, Ct);

        await Assert.ThrowsAsync<ContentPackNameConflictException>(
            () => _manager.UpdatePackAsync(other.Id, "SPORT", null, Ct));
    }

    [Fact]
    public async Task ReorderPacks_ProducesContiguousOrdering()
    {
        var a = await _manager.CreatePackAsync("A", null, ContentPackOrigin.Manual, Ct);
        var b = await _manager.CreatePackAsync("B", null, ContentPackOrigin.Manual, Ct);
        var c = await _manager.CreatePackAsync("C", null, ContentPackOrigin.Manual, Ct);

        await _manager.ReorderPacksAsync([c.Id, a.Id, b.Id], Ct);

        var ordered = await _manager.GetPacksAsync(Ct);
        Assert.Equal([c.Id, a.Id, b.Id], ordered.Select(e => e.Id).ToArray());
        Assert.Equal([0, 1, 2], ordered.Select(e => e.SortOrder).ToArray());
    }

    [Fact]
    public async Task ReorderPacks_RejectsPartialAndDuplicateAndUnknown()
    {
        var a = await _manager.CreatePackAsync("A", null, ContentPackOrigin.Manual, Ct);
        var b = await _manager.CreatePackAsync("B", null, ContentPackOrigin.Manual, Ct);

        await Assert.ThrowsAsync<ArgumentException>(() => _manager.ReorderPacksAsync([a.Id], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _manager.ReorderPacksAsync([a.Id, a.Id], Ct));
        await Assert.ThrowsAsync<ContentPackNotFoundException>(() => _manager.ReorderPacksAsync([a.Id, b.Id, Guid.NewGuid()], Ct));
    }

    [Fact]
    public async Task ReorderPacks_ConcurrentReordersLeaveOneCompleteOrdering()
    {
        var ids = new List<Guid>();
        foreach (var name in new[] { "A", "B", "C", "D" })
        {
            ids.Add((await _manager.CreatePackAsync(name, null, ContentPackOrigin.Manual, Ct)).Id);
        }

        var forward = ids.ToArray();
        var backward = ids.AsEnumerable().Reverse().ToArray();

        await Task.WhenAll(Enumerable.Range(0, 6).Select(i => RunReorderAsync(i % 2 == 0 ? forward : backward)));

        var ordered = await _manager.GetPacksAsync(Ct);
        Assert.Equal([0, 1, 2, 3], ordered.Select(e => e.SortOrder).ToArray());
        Assert.Equal(4, ordered.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public async Task DeletePack_RemovesPackAndItsMembershipsOnly()
    {
        var pack = await _manager.CreatePackAsync("Sport", null, ContentPackOrigin.Manual, Ct);
        var other = await _manager.CreatePackAsync("Concerts", null, ContentPackOrigin.Manual, Ct);
        var itemId = await SeedItemAsync("Match");

        await _manager.AddItemAsync(pack.Id, itemId, ContentPackMembershipProvenance.Manual, Ct);
        await _manager.AddItemAsync(other.Id, itemId, ContentPackMembershipProvenance.Manual, Ct);

        Assert.True(await _manager.DeletePackAsync(pack.Id, Ct));

        using var ctx = CreateDbContext();
        Assert.Empty(ctx.ContentPacks.Where(e => e.Id.Equals(pack.Id)));
        Assert.Empty(ctx.ContentPackMemberships.Where(e => e.PackId.Equals(pack.Id)));

        // The item and the other pack's membership are untouched.
        Assert.Single(ctx.BaseItems.Where(e => e.Id.Equals(itemId)));
        Assert.Single(ctx.ContentPackMemberships.Where(e => e.PackId.Equals(other.Id)));
    }

    [Fact]
    public async Task DeletePack_IsEffectIdempotent()
    {
        var pack = await _manager.CreatePackAsync("Sport", null, ContentPackOrigin.Manual, Ct);

        Assert.True(await _manager.DeletePackAsync(pack.Id, Ct));
        Assert.False(await _manager.DeletePackAsync(pack.Id, Ct));
    }

    [Fact]
    public async Task AddItem_IsIdempotentAndLeavesOneRow()
    {
        var pack = await _manager.CreatePackAsync("Sport", null, ContentPackOrigin.Manual, Ct);
        var itemId = await SeedItemAsync("Match");

        await _manager.AddItemAsync(pack.Id, itemId, ContentPackMembershipProvenance.Manual, Ct);
        await _manager.AddItemAsync(pack.Id, itemId, ContentPackMembershipProvenance.Manual, Ct);

        using var ctx = CreateDbContext();
        Assert.Single(ctx.ContentPackMemberships.Where(e => e.PackId.Equals(pack.Id)));
    }

    [Fact]
    public async Task AddItem_ConcurrentDuplicatesLeaveOneRow()
    {
        var pack = await _manager.CreatePackAsync("Sport", null, ContentPackOrigin.Manual, Ct);
        var itemId = await SeedItemAsync("Match");

        var failures = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => RunAddAsync(pack.Id, itemId, ContentPackMembershipProvenance.Manual)));

        Assert.All(failures, Assert.Null);

        using var ctx = CreateDbContext();
        Assert.Single(ctx.ContentPackMemberships.Where(e => e.PackId.Equals(pack.Id)));
    }

    [Fact]
    public async Task AddItem_UnknownPackThrowsNotFound()
    {
        var itemId = await SeedItemAsync("Match");

        await Assert.ThrowsAsync<ContentPackNotFoundException>(
            () => _manager.AddItemAsync(Guid.NewGuid(), itemId, ContentPackMembershipProvenance.Manual, Ct));
    }

    [Theory]
    [InlineData(null, ContentPackMembershipProvenance.Manual, ContentPackMembershipProvenance.Manual)]
    [InlineData(null, ContentPackMembershipProvenance.SystemSeed, ContentPackMembershipProvenance.SystemSeed)]
    [InlineData(ContentPackMembershipProvenance.Manual, ContentPackMembershipProvenance.Manual, ContentPackMembershipProvenance.Manual)]
    [InlineData(ContentPackMembershipProvenance.Manual, ContentPackMembershipProvenance.SystemSeed, ContentPackMembershipProvenance.Manual)]
    [InlineData(ContentPackMembershipProvenance.SystemSeed, ContentPackMembershipProvenance.Manual, ContentPackMembershipProvenance.Manual)]
    [InlineData(ContentPackMembershipProvenance.SystemSeed, ContentPackMembershipProvenance.SystemSeed, ContentPackMembershipProvenance.SystemSeed)]
    public async Task AddItem_AppliesProvenanceTransitionMatrix(
        ContentPackMembershipProvenance? existing,
        ContentPackMembershipProvenance incoming,
        ContentPackMembershipProvenance expected)
    {
        var pack = await _manager.CreatePackAsync("Sport", null, ContentPackOrigin.Manual, Ct);
        var itemId = await SeedItemAsync("Match");

        if (existing.HasValue)
        {
            await _manager.AddItemAsync(pack.Id, itemId, existing.Value, Ct);
        }

        await _manager.AddItemAsync(pack.Id, itemId, incoming, Ct);

        using var ctx = CreateDbContext();
        var row = ctx.ContentPackMemberships.Single(e => e.PackId.Equals(pack.Id) && e.ItemId.Equals(itemId));
        Assert.Equal(expected, row.Provenance);
    }

    [Fact]
    public async Task AddItem_ConcurrentManualAndSystemSeedKeepsManual()
    {
        var pack = await _manager.CreatePackAsync("Sport", null, ContentPackOrigin.Manual, Ct);
        var itemId = await SeedItemAsync("Match");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => RunAddAsync(
            pack.Id,
            itemId,
            i % 2 == 0 ? ContentPackMembershipProvenance.Manual : ContentPackMembershipProvenance.SystemSeed)));

        using var ctx = CreateDbContext();
        var row = ctx.ContentPackMemberships.Single(e => e.PackId.Equals(pack.Id) && e.ItemId.Equals(itemId));
        Assert.Equal(ContentPackMembershipProvenance.Manual, row.Provenance);
    }

    [Fact]
    public async Task AddItem_UnrelatedDatabaseFailureIsNotReportedAsConflict()
    {
        var pack = await _manager.CreatePackAsync("Sport", null, ContentPackOrigin.Manual, Ct);

        // No BaseItem row exists for this id, so the item foreign key rejects the insert. That is
        // not a duplicate, and it must surface as the database error it is.
        var failure = await Record.ExceptionAsync(
            () => _manager.AddItemAsync(pack.Id, Guid.NewGuid(), ContentPackMembershipProvenance.Manual, Ct));

        Assert.IsAssignableFrom<DbUpdateException>(failure);
    }

    [Fact]
    public async Task RemoveItem_AbsentMembershipSucceedsAndTouchesNothingElse()
    {
        var pack = await _manager.CreatePackAsync("Sport", null, ContentPackOrigin.Manual, Ct);
        var kept = await SeedItemAsync("Kept");
        var absent = await SeedItemAsync("Absent");

        await _manager.AddItemAsync(pack.Id, kept, ContentPackMembershipProvenance.Manual, Ct);
        await _manager.RemoveItemAsync(pack.Id, absent, Ct);

        using var ctx = CreateDbContext();
        Assert.Single(ctx.ContentPackMemberships.Where(e => e.PackId.Equals(pack.Id)));
        Assert.Single(ctx.BaseItems.Where(e => e.Id.Equals(kept)));
        Assert.Single(ctx.BaseItems.Where(e => e.Id.Equals(absent)));
    }

    [Fact]
    public async Task RemoveItem_UnknownPackThrowsNotFound()
    {
        var itemId = await SeedItemAsync("Match");

        await Assert.ThrowsAsync<ContentPackNotFoundException>(
            () => _manager.RemoveItemAsync(Guid.NewGuid(), itemId, Ct));
    }

    [Fact]
    public async Task Membership_IsManyToManyInBothDirections()
    {
        var sport = await _manager.CreatePackAsync("Sport", null, ContentPackOrigin.Manual, Ct);
        var family = await _manager.CreatePackAsync("Family", null, ContentPackOrigin.Manual, Ct);
        var match = await SeedItemAsync("Match");
        var album = await SeedItemAsync("Album");

        await _manager.AddItemAsync(sport.Id, match, ContentPackMembershipProvenance.Manual, Ct);
        await _manager.AddItemAsync(sport.Id, album, ContentPackMembershipProvenance.Manual, Ct);
        await _manager.AddItemAsync(family.Id, match, ContentPackMembershipProvenance.Manual, Ct);

        var packsForMatch = await _manager.GetPacksForItemAsync(match, Ct);
        Assert.Equal([sport.Id, family.Id], packsForMatch.Select(e => e.Id).ToArray());

        using var ctx = CreateDbContext();
        Assert.Equal(2, ctx.ContentPackMemberships.Count(e => e.PackId.Equals(sport.Id)));
    }

    [Fact]
    public async Task GetNonEmptyPackIds_ReportsOnlyPopulatedPacks()
    {
        var populated = await _manager.CreatePackAsync("Sport", null, ContentPackOrigin.Manual, Ct);
        var empty = await _manager.CreatePackAsync("Concerts", null, ContentPackOrigin.Manual, Ct);
        var itemId = await SeedItemAsync("Match");

        await _manager.AddItemAsync(populated.Id, itemId, ContentPackMembershipProvenance.Manual, Ct);

        var nonEmpty = await _manager.GetNonEmptyPackIdsAsync(Ct);

        Assert.Contains(populated.Id, nonEmpty);
        Assert.DoesNotContain(empty.Id, nonEmpty);
    }

    private async Task<Exception?> RunCreateAsync(string name)
    {
        return await Record.ExceptionAsync(() => _manager.CreatePackAsync(name, null, ContentPackOrigin.Manual, Ct));
    }

    private async Task<Exception?> RunReorderAsync(Guid[] order)
    {
        return await Record.ExceptionAsync(() => _manager.ReorderPacksAsync(order, Ct));
    }

    private async Task<Exception?> RunAddAsync(Guid packId, Guid itemId, ContentPackMembershipProvenance provenance)
    {
        return await Record.ExceptionAsync(() => _manager.AddItemAsync(packId, itemId, provenance, Ct));
    }

    private async Task<Guid> SeedItemAsync(string name)
    {
        var id = Guid.NewGuid();
        var ctx = CreateDbContext();
        await using (ctx.ConfigureAwait(false))
        {
            ctx.BaseItems.Add(new BaseItemEntity
            {
                Id = id,
                Type = "Tesserafin.Controller.Entities.Video",
                Name = name
            });

            await ctx.SaveChangesAsync(Ct);
        }

        return id;
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
