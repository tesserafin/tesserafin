using System;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller;
using Tesserafin.Controller.Configuration;
using Tesserafin.Data.Entities;
using Tesserafin.Database.Implementations;
using Tesserafin.Database.Implementations.Locking;
using Tesserafin.Database.Providers.Sqlite;
using Tesserafin.LiveTv.Recordings;
using Tesserafin.Model.Configuration;
using Tesserafin.Server.Core.Data;
using Tesserafin.Server.Implementations.Item;
using Xunit;
using BaseItemKind = Tesserafin.Data.Enums.BaseItemKind;

namespace Tesserafin.LiveTv.Tests.Recordings;

/// <summary>
/// The KeepUpTo retention query runs against the real catalogue: it must return exactly the real recorded
/// files below the series folder, newest first, without the entity statics the former folder query used.
/// </summary>
public sealed class SeriesRecordingsQueryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<TesserafinDbContext> _dbOptions;
    private readonly ItemTypeLookup _types = new();
    private readonly BaseItemRepository _repository;

    public SeriesRecordingsQueryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<TesserafinDbContext>().UseSqlite(_connection).Options;

        using (var ctx = CreateDbContext())
        {
            ctx.Database.EnsureCreated();
        }

        var factory = new Mock<IDbContextFactory<TesserafinDbContext>>();
        factory.Setup(f => f.CreateDbContext()).Returns(CreateDbContext);
        var configuration = new Mock<IServerConfigurationManager>();
        configuration.Setup(c => c.Configuration).Returns(new ServerConfiguration());

        _repository = new BaseItemRepository(
            factory.Object,
            Mock.Of<IServerApplicationHost>(),
            _types,
            configuration.Object,
            NullLogger<BaseItemRepository>.Instance,
            new Tesserafin.Controller.LiveTv.ActiveRecordings());
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void ReturnsOnlyRealRecordedFilesBelowTheSeries_NewestFirst()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var series = Entity(BaseItemKind.Series, "Series", isFolder: true, start);
        var season = Entity(BaseItemKind.Season, "Season 1", isFolder: true, start);
        var oldest = Entity(BaseItemKind.Episode, "E1", isFolder: false, start.AddDays(1));
        var newest = Entity(BaseItemKind.Episode, "E3", isFolder: false, start.AddDays(3));
        var middle = Entity(BaseItemKind.Episode, "E2", isFolder: false, start.AddDays(2));
        var virtualEpisode = Entity(BaseItemKind.Episode, "Missing", isFolder: false, start.AddDays(4), isVirtual: true);
        var otherSeries = Entity(BaseItemKind.Series, "Other", isFolder: true, start);
        var otherEpisode = Entity(BaseItemKind.Episode, "Other E1", isFolder: false, start.AddDays(5));

        using (var ctx = CreateDbContext())
        {
            ctx.BaseItems.AddRange(series, season, oldest, newest, middle, virtualEpisode, otherSeries, otherEpisode);
            Ancestor(ctx, series, season);
            foreach (var episode in new[] { oldest, newest, middle, virtualEpisode })
            {
                Ancestor(ctx, series, episode);
                Ancestor(ctx, season, episode);
            }

            Ancestor(ctx, otherSeries, otherEpisode);
            ctx.SaveChanges();
        }

        var result = _repository.GetItemList(RecordingsManager.CreateSeriesRecordingsQuery(series.Id));

        Assert.Equal([newest.Id, middle.Id, oldest.Id], result.Select(item => item.Id));
    }

    private static void Ancestor(TesserafinDbContext ctx, BaseItemEntity parent, BaseItemEntity item)
        => ctx.AncestorIds.Add(new AncestorId { ParentItemId = parent.Id, ItemId = item.Id, ParentItem = parent, Item = item });

    private BaseItemEntity Entity(BaseItemKind kind, string name, bool isFolder, DateTime created, bool isVirtual = false)
    {
        var id = Guid.NewGuid();
        return new BaseItemEntity
        {
            Id = id,
            Type = _types.BaseItemKindNames[kind],
            Name = name,
            Path = isVirtual ? null : "/recordings/" + name + (isFolder ? string.Empty : ".ts"),
            PresentationUniqueKey = id.ToString("N"),
            MediaType = isFolder ? null : "Video",
            IsFolder = isFolder,
            IsVirtualItem = isVirtual,
            DateCreated = created,
        };
    }

    private TesserafinDbContext CreateDbContext()
        => new(
            _dbOptions,
            NullLogger<TesserafinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
}
