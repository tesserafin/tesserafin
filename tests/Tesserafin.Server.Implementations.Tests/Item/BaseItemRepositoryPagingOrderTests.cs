using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Database.Implementations;
using Tesserafin.Database.Implementations.Locking;
using Tesserafin.Database.Providers.Sqlite;
using Tesserafin.Model.Configuration;
using Tesserafin.Server.Core.Data;
using Tesserafin.Server.Implementations.Item;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Item;

/// <summary>
/// Pages of a query must read one sequence whatever the page size. The expected sequence is computed here from the
/// seeded rows, never from another query, and the cases run with SQLite's scan order both ways round
/// (PRAGMA reverse_unordered_selects). Where the engine's plan still yields Id order by accident, the SQL tests
/// below are what fails: they read the ORDER BY itself, whatever the engine does with it.
/// </summary>
public sealed class BaseItemRepositoryPagingOrderTests : IDisposable
{
    private const int MovieCount = 230;
    private const int SeriesCount = 120;

    private static readonly int[] _pageSizes = [24, 37, 100];
    private static readonly DateTime _firstDate = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime _secondDate = new(2021, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<TesserafinDbContext> _dbOptions;
    private readonly BaseItemRepository _repository;
    private readonly ItemTypeLookup _types = new();
    private readonly User _user;

    public BaseItemRepositoryPagingOrderTests()
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

        var serverConfigurationManager = new Mock<IServerConfigurationManager>();
        serverConfigurationManager.Setup(c => c.Configuration).Returns(new ServerConfiguration());

        _repository = new BaseItemRepository(
            factory.Object,
            new Mock<IServerApplicationHost>().Object,
            _types,
            serverConfigurationManager.Object,
            NullLogger<BaseItemRepository>.Instance,
            Mock.Of<IItemReadServices>());

        _user = new User("paging", "auth", "reset");
        using (var ctx = CreateDbContext())
        {
            ctx.Users.Add(_user);
            ctx.SaveChanges();
        }
    }

    private static Dictionary<string, OrderCase> MovieOrderCases { get; } = new()
    {
        ["none"] = new([], [Asc(r => r.SortName)]),
        ["SortName asc"] = new(
            [(ItemSortBy.SortName, SortOrder.Ascending)],
            [Asc(r => r.SortName), Asc(r => r.Name)]),
        ["SortName desc"] = new(
            [(ItemSortBy.SortName, SortOrder.Descending)],
            [Desc(r => r.SortName), Desc(r => r.Name)]),
        ["ProductionYear asc"] = new(
            [(ItemSortBy.ProductionYear, SortOrder.Ascending)],
            [Asc(r => r.Year), Asc(r => r.SortName)]),
        ["ProductionYear desc"] = new(
            [(ItemSortBy.ProductionYear, SortOrder.Descending)],
            [Desc(r => r.Year), Asc(r => r.SortName)]),
        ["CommunityRating desc"] = new(
            [(ItemSortBy.CommunityRating, SortOrder.Descending)],
            [Desc(r => r.Rating), Asc(r => r.SortName)]),
        ["DateCreated desc"] = new(
            [(ItemSortBy.DateCreated, SortOrder.Descending)],
            [Desc(r => r.Created), Asc(r => r.SortName)]),
        ["DatePlayed desc"] = new(
            [(ItemSortBy.DatePlayed, SortOrder.Descending)],
            [Desc(r => r.Played), Asc(r => r.SortName)]),
        ["several, mixed directions"] = new(
            [(ItemSortBy.ProductionYear, SortOrder.Descending), (ItemSortBy.CommunityRating, SortOrder.Ascending), (ItemSortBy.DateCreated, SortOrder.Descending)],
            [Desc(r => r.Year), Asc(r => r.Rating), Desc(r => r.Created), Asc(r => r.SortName)]),
        ["requested priority wins"] = new(
            [(ItemSortBy.CommunityRating, SortOrder.Ascending), (ItemSortBy.ProductionYear, SortOrder.Ascending)],
            [Asc(r => r.Rating), Asc(r => r.Year), Asc(r => r.SortName)]),
        ["SortName then more"] = new(
            [(ItemSortBy.SortName, SortOrder.Ascending), (ItemSortBy.ProductionYear, SortOrder.Descending)],
            [Asc(r => r.SortName), Asc(r => r.Name), Desc(r => r.Year)])
    };

    public static TheoryData<string, bool> MovieOrders()
    {
        var data = new TheoryData<string, bool>();
        foreach (var name in MovieOrderCases.Keys)
        {
            data.Add(name, false);
            data.Add(name, true);
        }

        return data;
    }

    public static TheoryData<bool> ScanOrders() => new() { false, true };

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Theory]
    [MemberData(nameof(MovieOrders))]
    public void Movies_PagesReadTheExpectedSequence(string name, bool reverseScan)
    {
        var rows = SeedMovies(MovieCount);
        ScanBackwards(reverseScan);
        var orderCase = MovieOrderCases[name];

        AssertPagesRead(Expected(rows, orderCase.Keys), () => MovieQuery(orderCase.Requested));
    }

    [Theory]
    [MemberData(nameof(ScanOrders))]
    public void Movies_LargeTiedLibrary_PagesReadTheExpectedSequence(bool reverseScan)
    {
        var rows = SeedMovies(2000);
        ScanBackwards(reverseScan);

        AssertPagesRead(
            Expected(rows, [Desc(r => r.Year), Asc(r => r.SortName)]),
            () => MovieQuery([(ItemSortBy.ProductionYear, SortOrder.Descending)]));
    }

    [Theory]
    [MemberData(nameof(ScanOrders))]
    public void Movies_SearchRelevance_PagesReadTheExpectedSequence(bool reverseScan)
    {
        var rows = SeedMovies(MovieCount);
        ScanBackwards(reverseScan);

        AssertPagesRead(
            Expected(rows.Where(r => r.CleanName.Contains("tied", StringComparison.Ordinal)), [Asc(Relevance), Desc(r => r.Year)]),
            () =>
            {
                var query = MovieQuery([(ItemSortBy.ProductionYear, SortOrder.Descending)]);
                query.SearchTerm = "tied";
                return query;
            });

        AssertPagesRead(
            Expected(rows.Where(r => r.CleanName.Contains("tied", StringComparison.Ordinal)), [Asc(Relevance)]),
            () =>
            {
                var query = MovieQuery([]);
                query.SearchTerm = "tied";
                return query;
            });
    }

    [Theory]
    [MemberData(nameof(ScanOrders))]
    public void Movies_AlternateVersions_CollapseOnceAtTheirPlace(bool reverseScan)
    {
        var rows = SeedMovies(MovieCount);
        var alternatePrimary = rows[0];
        using (var ctx = CreateDbContext())
        {
            var entity = MovieEntity(alternatePrimary with { Id = Guid.Parse("00000000-0000-4000-8000-000000000001") });
            entity.PresentationUniqueKey = alternatePrimary.Key;
            entity.PrimaryVersionId = alternatePrimary.Id;
            ctx.BaseItems.Add(entity);
            ctx.SaveChanges();
        }

        ScanBackwards(reverseScan);

        // Id ordering decides between tied rows; it never decides which version represents the group.
        AssertPagesRead(
            Expected(rows, [Desc(r => r.Year), Asc(r => r.SortName)]),
            () =>
            {
                var query = MovieQuery([(ItemSortBy.ProductionYear, SortOrder.Descending)]);
                query.IncludeOwnedItems = true;
                return query;
            });
    }

    [Theory]
    [InlineData(SortOrder.Ascending, false)]
    [InlineData(SortOrder.Ascending, true)]
    [InlineData(SortOrder.Descending, false)]
    [InlineData(SortOrder.Descending, true)]
    public void Series_SeriesDatePlayed_PagesReadTheExpectedSequence(SortOrder direction, bool reverseScan)
    {
        var rows = SeedSeries();
        ScanBackwards(reverseScan);
        (Func<OrderRow, IComparable?> Key, bool Ascending) played = (r => r.Played, direction == SortOrder.Ascending);

        AssertPagesRead(
            Expected(rows, [played, Asc(r => r.SortName)]),
            () => SeriesQuery([(ItemSortBy.SeriesDatePlayed, direction)]));
    }

    [Theory]
    [MemberData(nameof(ScanOrders))]
    public void Series_SeriesDatePlayedWithinSearch_PagesReadTheExpectedSequence(bool reverseScan)
    {
        var rows = SeedSeries();
        ScanBackwards(reverseScan);

        // The search path orders through the correlated fallback, not the pre-aggregated join.
        AssertPagesRead(
            Expected(rows, [Asc(Relevance), Desc(r => r.Played)]),
            () =>
            {
                var query = SeriesQuery([(ItemSortBy.SeriesDatePlayed, SortOrder.Descending)]);
                query.SearchTerm = "tied";
                return query;
            });
    }

    [Theory]
    [MemberData(nameof(ScanOrders))]
    public void Movies_UngroupedQuery_PagesReadTheExpectedSequence(bool reverseScan)
    {
        var rows = SeedMovies(MovieCount);
        ScanBackwards(reverseScan);

        // The grouped path reaches rows through Id IN (...), which today hands SQLite's sorter rows already in Id
        // order. The ungrouped path scans, so this is the case whose ties really move once the final key is gone.
        AssertPagesRead(
            Expected(rows, [Desc(r => r.Year), Asc(r => r.SortName)]),
            () =>
            {
                var query = MovieQuery([(ItemSortBy.ProductionYear, SortOrder.Descending)]);
                query.GroupByPresentationUniqueKey = false;
                return query;
            });
    }

    [Fact]
    public void SeriesDatePlayed_KeepsItsOwnPriorityOverOtherCriteria()
    {
        // Characterises what the pre-aggregated path has always done: other criteria do not take part.
        var rows = SeedSeries();

        AssertPagesRead(
            Expected(rows, [Desc(r => r.Played), Asc(r => r.SortName)]),
            () => SeriesQuery([(ItemSortBy.ProductionYear, SortOrder.Ascending), (ItemSortBy.SeriesDatePlayed, SortOrder.Descending)]));
    }

    [Theory]
    [InlineData(ItemSortBy.SortName, false)]
    [InlineData(ItemSortBy.ProductionYear, false)]
    [InlineData(ItemSortBy.DatePlayed, false)]
    [InlineData(ItemSortBy.SeriesDatePlayed, false)]
    [InlineData(ItemSortBy.Default, false)]
    [InlineData(ItemSortBy.SortName, true)]
    [InlineData(ItemSortBy.SeriesDatePlayed, true)]
    [InlineData(ItemSortBy.Default, true)]
    public void ApplyOrder_SqlEndsOnTheItemId(ItemSortBy sortBy, bool search)
    {
        foreach (var direction in new[] { SortOrder.Ascending, SortOrder.Descending })
        {
            var clause = OrderByClause(sortBy, direction, search);

            Assert.EndsWith("\"Id\"", clause, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ApplyOrder_RandomGetsNoFinalKey()
    {
        var clause = OrderByClause(ItemSortBy.Random, SortOrder.Ascending, search: false);

        Assert.DoesNotContain("\"Id\"", clause, StringComparison.Ordinal);
        Assert.Contains("RANDOM()", clause, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Random_DoesNotBecomeStable()
    {
        SeedMovies(MovieCount);
        var sequences = Enumerable.Range(0, 3)
            .Select(_ =>
            {
                var query = MovieQuery([(ItemSortBy.Random, SortOrder.Ascending)]);
                return string.Join(',', _repository.GetItemList(query).Select(i => i.Id));
            })
            .ToHashSet();

        Assert.True(sequences.Count > 1);
    }

    private static (Func<OrderRow, IComparable?> Key, bool Ascending) Asc(Func<OrderRow, IComparable?> key) => (key, true);

    private static (Func<OrderRow, IComparable?> Key, bool Ascending) Desc(Func<OrderRow, IComparable?> key) => (key, false);

    private static IComparable Relevance(OrderRow row)
        => row.CleanName == "tied" ? 0
            : row.CleanName.StartsWith("tied ", StringComparison.Ordinal) ? 1
            : row.CleanName.StartsWith("tied", StringComparison.Ordinal) ? 2
            : 3;

    /// <summary>
    /// The sequence the seeded rows must produce: NULL lowest as SQLite compares it, then the Id's stored text,
    /// ascending. Reading the Id's own text keeps this independent of .NET's byte-swapped Guid comparison.
    /// </summary>
    private static List<Guid> Expected(IEnumerable<OrderRow> rows, IReadOnlyList<(Func<OrderRow, IComparable?> Key, bool Ascending)> keys)
    {
        var ordered = rows.ToList();
        ordered.Sort((left, right) =>
        {
            foreach (var (key, ascending) in keys)
            {
                var compared = CompareNullable(key(left), key(right));
                if (compared != 0)
                {
                    return ascending ? compared : -compared;
                }
            }

            return string.CompareOrdinal(StoredId(left.Id), StoredId(right.Id));
        });

        return ordered.ConvertAll(r => r.Id);
    }

    private static string StoredId(Guid id) => id.ToString("D").ToUpperInvariant();

    private static int CompareNullable(IComparable? left, IComparable? right)
    {
        if (left is null || right is null)
        {
            return (left is null ? 0 : 1) - (right is null ? 0 : 1);
        }

        return left is string text ? string.CompareOrdinal(text, (string)right) : left.CompareTo(right);
    }

    private static Guid DeterministicId(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }

    private void AssertPagesRead(IReadOnlyList<Guid> expected, Func<InternalItemsQuery> createQuery)
    {
        Assert.Equal(expected.Count, expected.Distinct().Count());

        foreach (var pageSize in _pageSizes)
        {
            var read = new List<Guid>();
            for (var start = 0; start < expected.Count; start += pageSize)
            {
                var query = createQuery();
                query.StartIndex = start;
                query.Limit = pageSize;
                query.EnableTotalRecordCount = true;

                var page = _repository.GetItems(query);

                Assert.Equal(expected.Count, page.TotalRecordCount);
                Assert.Equal(start, page.StartIndex);
                Assert.Equal(Math.Min(pageSize, expected.Count - start), page.Items.Count);
                read.AddRange(page.Items.Select(i => i.Id));
            }

            Assert.Equal(expected, read);

            // A boundary read exactly at the end is empty, and the total still holds.
            var pastEnd = createQuery();
            pastEnd.StartIndex = expected.Count;
            pastEnd.Limit = pageSize;
            pastEnd.EnableTotalRecordCount = true;
            var past = _repository.GetItems(pastEnd);
            Assert.Empty(past.Items);
            Assert.Equal(expected.Count, past.TotalRecordCount);
        }
    }

    private string OrderByClause(ItemSortBy sortBy, SortOrder direction, bool search)
    {
        var query = new InternalItemsQuery(_user)
        {
            OrderBy = sortBy == ItemSortBy.Default ? [] : [(sortBy, direction)],
            SearchTerm = search ? "tied" : null
        };

        using var ctx = CreateDbContext();
        var sql = _repository.ApplyOrder(ctx.BaseItems.AsNoTracking(), query, ctx).ToQueryString();

        // A term's direction is only spelled out when descending, so a clause ending on the column ends ascending.
        return sql[(sql.LastIndexOf("ORDER BY", StringComparison.Ordinal) + "ORDER BY".Length)..].Trim();
    }

    private InternalItemsQuery MovieQuery((ItemSortBy OrderBy, SortOrder SortOrder)[] orderBy)
        => new(_user)
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            OrderBy = orderBy
        };

    private InternalItemsQuery SeriesQuery((ItemSortBy OrderBy, SortOrder SortOrder)[] orderBy)
        => new(_user)
        {
            IncludeItemTypes = [BaseItemKind.Series],
            OrderBy = orderBy
        };

    private void ScanBackwards(bool reverse)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"PRAGMA reverse_unordered_selects = {(reverse ? "ON" : "OFF")};";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Few distinct values per column, with NULLs, so almost every row ties with many others on every criterion.
    /// Ids are drawn independently of insertion, so neither rowid nor any index lines up with the expected order.
    /// </summary>
    private List<OrderRow> SeedMovies(int count)
    {
        var random = new Random(306);
        var rows = new List<OrderRow>(count);
        for (var i = 0; i < count; i++)
        {
            var title = (i % 5) switch
            {
                0 => "tied",
                1 => "tied film",
                2 => "tiedfilm",
                3 => "a tied film",
                _ => "other"
            };

            rows.Add(new OrderRow(
                DeterministicId(random),
                title,
                title,
                i % 11 == 0 ? "z alternate" : title,
                $"movie-{i}",
                (i % 4) switch { 0 => 2000, 1 => 2001, 2 => 2002, _ => null },
                (i % 3) switch { 0 => 7f, 1 => 8f, _ => null },
                i % 2 == 0 ? _firstDate : _secondDate,
                (i % 6) switch { 0 => _firstDate, 1 => _secondDate, _ => null }));
        }

        using var ctx = CreateDbContext();
        foreach (var row in rows)
        {
            ctx.BaseItems.Add(MovieEntity(row));
            if (row.Played is not null)
            {
                ctx.UserData.Add(new UserData { Item = null!, User = null!, ItemId = row.Id, UserId = _user.Id, CustomDataKey = "k", LastPlayedDate = row.Played, Played = true });
            }

            // Another viewer's play must not move anything in this viewer's order.
            if (row.Played is null)
            {
                ctx.UserData.Add(new UserData { Item = null!, User = null!, ItemId = row.Id, UserId = OtherUser(ctx).Id, CustomDataKey = "k", LastPlayedDate = _secondDate, Played = true });
            }
        }

        ctx.SaveChanges();
        return rows;
    }

    private User OtherUser(TesserafinDbContext ctx)
    {
        var other = ctx.Users.Local.FirstOrDefault(u => u.Username == "other");
        if (other is null)
        {
            other = new User("other", "auth", "reset");
            ctx.Users.Add(other);
        }

        return other;
    }

    /// <summary>A series with a played episode sorts by that date; one nobody played has NULL.</summary>
    private List<OrderRow> SeedSeries()
    {
        var random = new Random(243);
        var rows = new List<OrderRow>(SeriesCount);
        using var ctx = CreateDbContext();
        for (var i = 0; i < SeriesCount; i++)
        {
            var key = $"series-{i}";
            var title = (i % 3) switch { 0 => "tied", 1 => "tied show", _ => "tiedshow" };
            DateTime? played = (i % 3) switch { 0 => _firstDate, 1 => _secondDate, _ => null };
            var row = new OrderRow(DeterministicId(random), title, title, title, key, 2000, null, _firstDate, played);
            rows.Add(row);

            ctx.BaseItems.Add(new BaseItemEntity
            {
                Id = row.Id,
                Type = _types.BaseItemKindNames[BaseItemKind.Series],
                Name = row.Name,
                SortName = row.SortName,
                CleanName = row.CleanName,
                PresentationUniqueKey = key,
                MediaType = null,
                IsSeries = true,
                IsFolder = true,
                IsVirtualItem = false
            });

            var episodeId = DeterministicId(random);
            ctx.BaseItems.Add(new BaseItemEntity
            {
                Id = episodeId,
                Type = _types.BaseItemKindNames[BaseItemKind.Episode],
                Name = "episode",
                SortName = "episode",
                CleanName = "episode",
                PresentationUniqueKey = episodeId.ToString("N"),
                SeriesPresentationUniqueKey = key,
                MediaType = "Video",
                IsFolder = false,
                IsVirtualItem = false
            });

            if (played is not null)
            {
                ctx.UserData.Add(new UserData { Item = null!, User = null!, ItemId = episodeId, UserId = _user.Id, CustomDataKey = "k", LastPlayedDate = played, Played = true });
            }
        }

        ctx.SaveChanges();
        return rows;
    }

    private BaseItemEntity MovieEntity(OrderRow row)
        => new()
        {
            Id = row.Id,
            Type = _types.BaseItemKindNames[BaseItemKind.Movie],
            Name = row.Name,
            SortName = row.SortName,
            CleanName = row.CleanName,
            PresentationUniqueKey = row.Key,
            ProductionYear = row.Year,
            CommunityRating = row.Rating,
            DateCreated = row.Created,
            MediaType = "Video",
            IsMovie = true,
            IsFolder = false,
            IsVirtualItem = false
        };

    private TesserafinDbContext CreateDbContext()
    {
        return new TesserafinDbContext(
            _dbOptions,
            NullLogger<TesserafinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    private sealed record OrderCase(
        (ItemSortBy OrderBy, SortOrder SortOrder)[] Requested,
        (Func<OrderRow, IComparable?> Key, bool Ascending)[] Keys);

    private sealed record OrderRow(
        Guid Id,
        string CleanName,
        string SortName,
        string Name,
        string Key,
        int? Year,
        float? Rating,
        DateTime Created,
        DateTime? Played);
}
