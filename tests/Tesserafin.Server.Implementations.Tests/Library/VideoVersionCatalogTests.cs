using System;
using Moq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

public class VideoVersionCatalogTests
{
    [Fact]
    public void Count_FollowsPrimaryAndCountsOnlyResolvedLinkedVideosButEveryLocalLink()
    {
        var primary = new Video { Id = Guid.NewGuid() };
        var alternate = new Video { Id = Guid.NewGuid() };
        alternate.SetPrimaryVersionId(primary.Id);
        var missingLinked = Guid.NewGuid();
        var missingLocal = Guid.NewGuid();
        var items = new Mock<IItemLookupService>(MockBehavior.Strict);
        items.Setup(x => x.GetItemById(primary.Id)).Returns(primary);
        items.Setup(x => x.GetItemById(alternate.Id)).Returns(alternate);
        items.Setup(x => x.GetItemById(missingLinked)).Returns((BaseItem?)null);
        var links = new Mock<ILinkedChildrenService>(MockBehavior.Strict);
        links.Setup(x => x.GetLinkedChildrenIds(primary.Id, (int)LinkedChildType.LinkedAlternateVersion))
            .Returns([alternate.Id, missingLinked]);
        links.Setup(x => x.GetLinkedChildrenIds(primary.Id, (int)LinkedChildType.LocalAlternateVersion))
            .Returns([missingLocal]);
        var catalog = new VideoVersionCatalog(items.Object, links.Object);

        Assert.Equal(3, catalog.GetMediaSourceCount(alternate));
        Assert.True(catalog.HasLocalVersions(primary));
    }

    [Fact]
    public void Count_CycleTerminatesAtTheFirstRepeatedPrimary()
    {
        var first = new Video { Id = Guid.NewGuid() };
        var second = new Video { Id = Guid.NewGuid() };
        first.SetPrimaryVersionId(second.Id);
        second.SetPrimaryVersionId(first.Id);
        var items = new Mock<IItemLookupService>(MockBehavior.Strict);
        items.Setup(x => x.GetItemById(first.Id)).Returns(first);
        items.Setup(x => x.GetItemById(second.Id)).Returns(second);
        var links = new Mock<ILinkedChildrenService>(MockBehavior.Strict);
        links.Setup(x => x.GetLinkedChildrenIds(second.Id, (int)LinkedChildType.LinkedAlternateVersion)).Returns([]);
        links.Setup(x => x.GetLinkedChildrenIds(second.Id, (int)LinkedChildType.LocalAlternateVersion)).Returns([Guid.NewGuid()]);
        var catalog = new VideoVersionCatalog(items.Object, links.Object);

        Assert.Equal(2, catalog.GetMediaSourceCount(first));
    }
}
