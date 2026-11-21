using System;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Persistence;
using Tesserafin.Data.Entities;
using Tesserafin.Database.Implementations;
using Tesserafin.Database.Implementations.Locking;
using Tesserafin.Database.Providers.Sqlite;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Tesserafin.Server.Core.Data;
using Tesserafin.Server.Implementations.Item;
using Xunit;
using BaseItemKind = Tesserafin.Data.Enums.BaseItemKind;

namespace Tesserafin.Server.Implementations.Tests.Item;

public sealed class BaseItemRepositoryGroupingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<TesserafinDbContext> _dbOptions;
    private readonly BaseItemRepository _repository;
    private readonly string _movieTypeName;

    public BaseItemRepositoryGroupingTests()
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

        var itemTypeLookup = new ItemTypeLookup();
        _movieTypeName = itemTypeLookup.BaseItemKindNames[BaseItemKind.Movie];

        var serverConfigurationManager = new Mock<IServerConfigurationManager>();
        serverConfigurationManager.Setup(c => c.Configuration).Returns(new ServerConfiguration());

        _repository = new BaseItemRepository(
            factory.Object,
            new Mock<IServerApplicationHost>().Object,
            itemTypeLookup,
            serverConfigurationManager.Object,
            NullLogger<BaseItemRepository>.Instance,
            new Tesserafin.Controller.LiveTv.ActiveRecordings(),
            new(),
            new(),
            new(() => Mock.Of<ILinkedChildrenService>()));
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public void GetItemList_VersionGroup_ReturnsPrimaryVersion()
    {
        // The alternate version sorts before the primary by id, so a plain Min(Id) per
        // presentation key would wrongly pick the alternate as the group representative.
        var primaryId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var alternateId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var presentationKey = primaryId.ToString("N");

        using (var ctx = CreateDbContext())
        {
            ctx.BaseItems.Add(CreateMovieEntity(primaryId, "Movie", presentationKey, null));
            ctx.BaseItems.Add(CreateMovieEntity(alternateId, "Movie - 1080p", presentationKey, primaryId));
            ctx.SaveChanges();
        }

        var result = _repository.GetItemList(CreateQuery());

        var item = Assert.Single(result);
        Assert.Equal(primaryId, item.Id);
    }

    [Fact]
    public void GetItemList_GroupWithoutPrimary_FallsBackToMinId()
    {
        var firstId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var secondId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var otherPrimaryId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var presentationKey = otherPrimaryId.ToString("N");

        using (var ctx = CreateDbContext())
        {
            ctx.BaseItems.Add(CreateMovieEntity(firstId, "Movie", presentationKey, otherPrimaryId));
            ctx.BaseItems.Add(CreateMovieEntity(secondId, "Movie - 4K", presentationKey, otherPrimaryId));
            ctx.SaveChanges();
        }

        var result = _repository.GetItemList(CreateQuery());

        var item = Assert.Single(result);
        Assert.Equal(firstId, item.Id);
    }

    [Fact]
    public void GetItemList_AttachesTheCatalogueReadsToEveryLoadedItem()
    {
        var id = Guid.Parse("33333333-3333-3333-3333-333333333333");
        using (var ctx = CreateDbContext())
        {
            ctx.BaseItems.Add(CreateMovieEntity(id, "Movie", id.ToString("N"), null));
            ctx.SaveChanges();
        }

        var item = Assert.Single(_repository.GetItemList(CreateQuery()));

        Assert.NotNull(item.ReadServices);
    }

    private static InternalItemsQuery CreateQuery()
    {
        // IncludeOwnedItems keeps the alternate version rows in the query so the
        // grouping collapse is what picks the group representative.
        return new InternalItemsQuery(new global::Tesserafin.Data.Entities.User("test", "auth", "reset"))
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            IncludeOwnedItems = true
        };
    }

    private BaseItemEntity CreateMovieEntity(Guid id, string name, string presentationKey, Guid? primaryVersionId)
    {
        return new BaseItemEntity
        {
            Id = id,
            Type = _movieTypeName,
            Name = name,
            PresentationUniqueKey = presentationKey,
            PrimaryVersionId = primaryVersionId,
            MediaType = "Video",
            IsMovie = true,
            IsFolder = false,
            IsVirtualItem = false
        };
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
