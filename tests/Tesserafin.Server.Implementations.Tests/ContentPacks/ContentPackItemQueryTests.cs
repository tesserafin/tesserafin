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
using Tesserafin.Data.Enums;
using Tesserafin.Database.Implementations;
using Tesserafin.Database.Implementations.Locking;
using Tesserafin.Database.Providers.Sqlite;
using Tesserafin.Model.Configuration;
using Tesserafin.Server.Core.Data;
using Tesserafin.Server.Implementations.Item;
using Xunit;
using BaseItemKind = Tesserafin.Data.Enums.BaseItemKind;

namespace Tesserafin.Server.Implementations.Tests.ContentPacks;

/// <summary>
/// The content pack filter is a restriction on the ordinary item query. These tests drive the real
/// repository so the composition with paging, sorting, media types and — above all — library
/// restriction is what is being asserted, not a hand-written mirror of it.
/// </summary>
public sealed class ContentPackItemQueryTests : IDisposable
{
    private static readonly Guid _allowedLibraryId = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid _deniedLibraryId = Guid.Parse("b0000000-0000-0000-0000-000000000002");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<TesserafinDbContext> _dbOptions;
    private readonly BaseItemRepository _repository;
    private readonly ItemTypeLookup _itemTypeLookup;

    public ContentPackItemQueryTests()
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

        _itemTypeLookup = new ItemTypeLookup();

        var serverConfigurationManager = new Mock<IServerConfigurationManager>();
        serverConfigurationManager.Setup(c => c.Configuration).Returns(new ServerConfiguration());

        _repository = new BaseItemRepository(
            factory.Object,
            new Mock<IServerApplicationHost>().Object,
            _itemTypeLookup,
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
    public void ContentPackFilter_ReturnsMixedMediaFamiliesFromOnePack()
    {
        var packId = Guid.NewGuid();
        var movie = NewId();
        var episode = NewId();
        var audio = NewId();

        Seed(ctx =>
        {
            ctx.BaseItems.Add(Item(movie, BaseItemKind.Movie, "Match of the day", _allowedLibraryId));
            ctx.BaseItems.Add(Item(episode, BaseItemKind.Episode, "Documentary part one", _allowedLibraryId));
            ctx.BaseItems.Add(Item(audio, BaseItemKind.Audio, "Anthem", _allowedLibraryId));
            ctx.ContentPacks.Add(Pack(packId, "Sport"));
            ctx.ContentPackMemberships.Add(Membership(packId, movie));
            ctx.ContentPackMemberships.Add(Membership(packId, episode));
            ctx.ContentPackMemberships.Add(Membership(packId, audio));
        });

        var result = _repository.GetItems(Query(packId));

        Assert.Equal(3, result.TotalRecordCount);
        Assert.Equal(
            new[] { movie, episode, audio }.OrderBy(e => e).ToArray(),
            result.Items.Select(e => e.Id).OrderBy(e => e).ToArray());
    }

    [Fact]
    public void ContentPackFilter_NeverReachesOutsideTheCallersLibraries()
    {
        // This is the whole point of expressing operation 9 as an item-query filter: membership
        // is not a capability, so an item in a library the caller is denied stays invisible even
        // though the pack row says it belongs.
        var packId = Guid.NewGuid();
        var visible = NewId();
        var forbidden = NewId();

        Seed(ctx =>
        {
            ctx.BaseItems.Add(Item(visible, BaseItemKind.Movie, "Allowed", _allowedLibraryId));
            ctx.BaseItems.Add(Item(forbidden, BaseItemKind.Movie, "Forbidden", _deniedLibraryId));
            ctx.ContentPacks.Add(Pack(packId, "Sport"));
            ctx.ContentPackMemberships.Add(Membership(packId, visible));
            ctx.ContentPackMemberships.Add(Membership(packId, forbidden));
        });

        var result = _repository.GetItems(Query(packId));

        Assert.Equal(1, result.TotalRecordCount);
        Assert.Equal(visible, Assert.Single(result.Items).Id);
    }

    [Fact]
    public void ContentPackFilter_WhollyInaccessiblePackYieldsNothingAtAll()
    {
        var packId = Guid.NewGuid();
        var forbidden = NewId();

        Seed(ctx =>
        {
            ctx.BaseItems.Add(Item(forbidden, BaseItemKind.Movie, "Forbidden", _deniedLibraryId));
            ctx.ContentPacks.Add(Pack(packId, "Sport"));
            ctx.ContentPackMemberships.Add(Membership(packId, forbidden));
        });

        var result = _repository.GetItems(Query(packId));

        Assert.Equal(0, result.TotalRecordCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void ContentPackFilter_IsRestrictingAndKeepsPagingSemantics()
    {
        var packId = Guid.NewGuid();
        var inPack = new[] { NewId(), NewId(), NewId() };
        var outOfPack = NewId();

        Seed(ctx =>
        {
            for (var i = 0; i < inPack.Length; i++)
            {
                ctx.BaseItems.Add(Item(inPack[i], BaseItemKind.Movie, $"In {i}", _allowedLibraryId));
            }

            ctx.BaseItems.Add(Item(outOfPack, BaseItemKind.Movie, "Out", _allowedLibraryId));
            ctx.ContentPacks.Add(Pack(packId, "Sport"));
            foreach (var id in inPack)
            {
                ctx.ContentPackMemberships.Add(Membership(packId, id));
            }
        });

        var unfiltered = _repository.GetItems(Query(null));
        Assert.Equal(4, unfiltered.TotalRecordCount);

        var page = Query(packId);
        page.StartIndex = 1;
        page.Limit = 1;

        var result = _repository.GetItems(page);

        // The pack narrows the set to three, and the page is taken from those three only.
        Assert.Equal(3, result.TotalRecordCount);
        Assert.Single(result.Items);
        Assert.Contains(result.Items[0].Id, inPack);
    }

    [Fact]
    public void ContentPackFilter_UnknownPackReturnsEmptyRatherThanEverything()
    {
        Seed(ctx => ctx.BaseItems.Add(Item(NewId(), BaseItemKind.Movie, "Allowed", _allowedLibraryId)));

        var result = _repository.GetItems(Query(Guid.NewGuid()));

        Assert.Equal(0, result.TotalRecordCount);
    }

    private static Guid NewId() => Guid.NewGuid();

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

    private static InternalItemsQuery Query(Guid? packId)
    {
        // TopParentIds is what the library manager fills in from the caller's accessible views
        // before the repository ever sees the query. Setting only the allowed library here is the
        // same restriction a user denied the second library would arrive with.
        return new InternalItemsQuery(new User("test", "auth", "reset"))
        {
            ContentPackId = packId,
            TopParentIds = [_allowedLibraryId],
            EnableTotalRecordCount = true,
            GroupByPresentationUniqueKey = false
        };
    }

    private BaseItemEntity Item(Guid id, BaseItemKind kind, string name, Guid topParentId) => new()
    {
        Id = id,
        Type = _itemTypeLookup.BaseItemKindNames[kind],
        Name = name,
        TopParentId = topParentId,
        PresentationUniqueKey = id.ToString("N"),
        MediaType = kind == BaseItemKind.Audio ? "Audio" : "Video",
        IsMovie = kind == BaseItemKind.Movie,
        IsFolder = false,
        IsVirtualItem = false
    };

    private void Seed(Action<TesserafinDbContext> seed)
    {
        using var ctx = CreateDbContext();
        seed(ctx);
        ctx.SaveChanges();
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
