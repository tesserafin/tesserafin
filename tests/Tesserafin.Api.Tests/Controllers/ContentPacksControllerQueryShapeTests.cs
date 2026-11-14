using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Tesserafin.Api.Constants;
using Tesserafin.Api.Controllers;
using Tesserafin.Api.Results;
using Tesserafin.Controller.ContentPacks;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.ContentPacks;
using Tesserafin.Model.Querying;
using Xunit;

namespace Tesserafin.Api.Tests.Controllers;

/// <summary>
/// Everything a content pack response says about items has to come from the ordinary item query,
/// asked on behalf of the caller. These tests capture the queries the controller actually issues,
/// so a count or an artwork pick that quietly stops being user-scoped cannot ship.
/// </summary>
public class ContentPacksControllerQueryShapeTests
{
    private static readonly Guid _userId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _packId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid _visibleItemId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task GetContentPack_CountAndArtworkComeFromTheCallersItemQuery()
    {
        var harness = new Harness();
        harness.PackItemsResult = harness.MakeResult(totalRecordCount: 2, itemId: _visibleItemId);

        var result = await harness.Controller.GetContentPack(_packId, CancellationToken.None);

        var dto = Assert.IsType<ContentPackDto>(Assert.IsType<OkResult<ContentPackDto>>(result.Result).Value);

        // The count is the query's total for this user, never a raw membership count.
        Assert.Equal(2, dto.VisibleItemCount);

        // The representative comes out of that same user-scoped result set.
        Assert.Equal(_visibleItemId, dto.RepresentativeItemId);

        var query = Assert.Single(harness.ItemsResultQueries);
        Assert.Equal(_packId, query.ContentPackId);
        Assert.Same(harness.User, query.User);
        Assert.Equal(1, query.Limit);
        Assert.True(query.EnableTotalRecordCount);
    }

    [Fact]
    public async Task GetContentPack_WhollyInaccessiblePackIsNotFound()
    {
        var harness = new Harness();
        harness.PackItemsResult = harness.MakeResult(totalRecordCount: 0, itemId: null);
        harness.NonEmptyPackIds = [_packId];

        var result = await harness.Controller.GetContentPack(_packId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetContentPacks_OmitsAPackWhoseWholeContentIsInvisibleAndKeepsAnEmptyOne()
    {
        var emptyPackId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        var harness = new Harness();
        harness.Packs = [harness.MakePack(_packId, "Populated", 0), harness.MakePack(emptyPackId, "Empty", 1)];
        harness.NonEmptyPackIds = [_packId];
        harness.PackItemsResult = harness.MakeResult(totalRecordCount: 0, itemId: null);

        var result = await harness.Controller.GetContentPacks(CancellationToken.None);

        var packs = Assert.IsAssignableFrom<IReadOnlyList<ContentPackDto>>(Assert.IsType<OkResult<IReadOnlyList<ContentPackDto>>>(result.Result).Value);

        var only = Assert.Single(packs);
        Assert.Equal(emptyPackId, only.Id);
        Assert.Equal(0, only.VisibleItemCount);
    }

    [Fact]
    public async Task AddItem_AsksTheItemQueryWhetherTheCallerMaySeeTheItem()
    {
        var harness = new Harness();
        harness.VisibleItemCount = 0;

        var result = await harness.Controller.AddContentPackItem(
            _packId,
            _visibleItemId,
            ContentPackMembershipProvenance.Manual,
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);

        // The membership was never written: the management permission does not grant visibility.
        harness.ContentPackManager.Verify(
            m => m.AddItemAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<ContentPackMembershipProvenance>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Library restriction is resolved into the query before it is narrowed to one item.
        harness.LibraryManager.Verify(
            m => m.ConfigureUserAccess(It.IsAny<InternalItemsQuery>(), harness.User),
            Times.Once);

        var query = Assert.Single(harness.CountQueries);
        Assert.Equal([_visibleItemId], query.ItemIds);
        Assert.Same(harness.User, query.User);
    }

    [Fact]
    public async Task AddItem_WritesTheMembershipOnceTheItemIsVisible()
    {
        var harness = new Harness();
        harness.VisibleItemCount = 1;

        var result = await harness.Controller.AddContentPackItem(
            _packId,
            _visibleItemId,
            ContentPackMembershipProvenance.Manual,
            CancellationToken.None);

        Assert.IsType<NoContentResult>(result);

        harness.ContentPackManager.Verify(
            m => m.AddItemAsync(_packId, _visibleItemId, ContentPackMembershipProvenance.Manual, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AddItem_RejectsAProvenanceThisServerDoesNotProduce()
    {
        var harness = new Harness();
        harness.VisibleItemCount = 1;

        var result = await harness.Controller.AddContentPackItem(
            _packId,
            _visibleItemId,
            ContentPackMembershipProvenance.ProviderSuggestion,
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);

        harness.ContentPackManager.Verify(
            m => m.AddItemAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<ContentPackMembershipProvenance>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private sealed class Harness
    {
        public Harness()
        {
            User = new User("harness", "auth", "reset") { Id = _userId };

            Packs = [MakePack(_packId, "Populated", 0)];
            NonEmptyPackIds = [];
            PackItemsResult = MakeResult(0, null);
            VisibleItemCount = 1;

            ContentPackManager = new Mock<IContentPackManager>();
            ContentPackManager
                .Setup(m => m.GetPacksAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Packs);
            ContentPackManager
                .Setup(m => m.GetPackAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, CancellationToken _) => Packs.FirstOrDefault(p => p.Id.Equals(id)));
            ContentPackManager
                .Setup(m => m.GetNonEmptyPackIdsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => NonEmptyPackIds);
            ContentPackManager
                .Setup(m => m.AddItemAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<ContentPackMembershipProvenance>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            LibraryManager = new Mock<ILibraryManager>();
            LibraryManager
                .Setup(m => m.GetItemsResult(It.IsAny<InternalItemsQuery>()))
                .Returns((InternalItemsQuery q) =>
                {
                    ItemsResultQueries.Add(q);
                    return PackItemsResult;
                });
            LibraryManager
                .Setup(m => m.GetCount(It.IsAny<InternalItemsQuery>()))
                .Returns((InternalItemsQuery q) =>
                {
                    CountQueries.Add(q);
                    return VisibleItemCount;
                });

            var userManager = new Mock<IUserManager>();
            userManager.Setup(m => m.GetUserById(_userId)).Returns(User);

            Controller = new ContentPacksController(
                ContentPackManager.Object,
                LibraryManager.Object,
                userManager.Object,
                new Mock<IDtoService>().Object)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(
                        [
                            new Claim(InternalClaimTypes.UserId, _userId.ToString("N", System.Globalization.CultureInfo.InvariantCulture))
                        ]))
                    }
                }
            };
        }

        public User User { get; }

        public ContentPacksController Controller { get; }

        public Mock<IContentPackManager> ContentPackManager { get; }

        public Mock<ILibraryManager> LibraryManager { get; }

        public IReadOnlyList<ContentPack> Packs { get; set; }

        public IReadOnlyCollection<Guid> NonEmptyPackIds { get; set; }

        public QueryResult<BaseItem> PackItemsResult { get; set; }

        public int VisibleItemCount { get; set; }

        public List<InternalItemsQuery> ItemsResultQueries { get; } = [];

        public List<InternalItemsQuery> CountQueries { get; } = [];

        public ContentPack MakePack(Guid id, string name, int sortOrder) => new()
        {
            Id = id,
            Name = name,
            NormalizedName = ContentPack.Normalize(name),
            SortOrder = sortOrder,
            Origin = ContentPackOrigin.Manual,
            DateCreated = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        public QueryResult<BaseItem> MakeResult(int totalRecordCount, Guid? itemId)
        {
            var items = itemId.HasValue
                ? new List<BaseItem> { new Folder { Id = itemId.Value, Name = "Representative" } }
                : [];

            return new QueryResult<BaseItem>(0, totalRecordCount, items);
        }
    }
}
