using System;
using System.Linq;
using Moq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Xunit;

namespace Tesserafin.Controller.Tests.Entities;

/// <summary>
/// Tests for the <see cref="IItemLookupService"/>-aware overloads of
/// <see cref="BaseItem.GetParent(IItemLookupService)"/>, <see cref="BaseItem.GetParents(IItemLookupService)"/>,
/// <see cref="BaseItem.FindParent{T}(IItemLookupService)"/> and
/// <see cref="BaseItem.GetOwner(IItemLookupService)"/> introduced in PR72.
///
/// The key property under test is that these overloads never touch the static
/// <see cref="BaseItem.LibraryManager"/>: each test points that static at a strict mock with no
/// setups, so any call into it throws <see cref="MockException"/> and fails the test.
/// </summary>
[Collection(BaseItemStaticStateFixture.Name)]
public class BaseItemLookupServiceTests
{
    public BaseItemLookupServiceTests()
    {
        // Strict, no setups: any call the code under test makes to the static LibraryManager
        // throws immediately, proving the service-aware overloads never fall back to it.
        BaseItem.LibraryManager = new Mock<ILibraryManager>(MockBehavior.Strict).Object;
    }

    [Fact]
    public void AttachedCatalogueResolvesTheParentChainAndOwner()
    {
        var reads = new Mock<IItemReadServices>(MockBehavior.Strict);
        var root = new Folder { Id = Guid.NewGuid(), ReadServices = reads.Object };
        var parent = new Folder { Id = Guid.NewGuid(), ParentId = root.Id, ReadServices = reads.Object };
        var item = new Movie { Id = Guid.NewGuid(), ParentId = parent.Id, OwnerId = root.Id, ReadServices = reads.Object };
        reads.Setup(scope => scope.GetItemById(parent.Id)).Returns(parent);
        reads.Setup(scope => scope.GetItemById(root.Id)).Returns(root);

        Assert.Equal(new BaseItem[] { parent, root }, item.GetParents());
        Assert.Same(parent, item.DisplayParent);
        Assert.Same(root, item.GetOwner());
    }

    // ---------------------------------------------------------------
    // GetParent(lookup)
    // ---------------------------------------------------------------

    [Fact]
    public void GetParent_WithLookup_ResolvesViaLookupService()
    {
        var parent = new Folder { Id = Guid.NewGuid(), Name = "Parent" };
        var child = new Movie { Id = Guid.NewGuid(), Name = "Child", ParentId = parent.Id };

        var lookup = new Mock<IItemLookupService>(MockBehavior.Strict);
        lookup.Setup(l => l.GetItemById(parent.Id)).Returns(parent);

        var result = child.GetParent(lookup.Object);

        Assert.Same(parent, result);
        lookup.Verify(l => l.GetItemById(parent.Id), Times.Once);
    }

    [Fact]
    public void GetParent_WithLookup_EmptyParentId_ReturnsNullWithoutConsultingLookup()
    {
        var child = new Movie { Id = Guid.NewGuid(), Name = "Child", ParentId = Guid.Empty };

        var lookup = new Mock<IItemLookupService>(MockBehavior.Strict);

        var result = child.GetParent(lookup.Object);

        Assert.Null(result);
        lookup.Verify(l => l.GetItemById(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public void GetParent_WithLookup_NullLookup_Throws()
    {
        var child = new Movie { Id = Guid.NewGuid(), Name = "Child" };

        Assert.Throws<ArgumentNullException>(() => child.GetParent(null!));
    }

    // ---------------------------------------------------------------
    // GetParents(lookup)
    // ---------------------------------------------------------------

    [Fact]
    public void GetParents_WithLookup_WalksFullChainThroughLookupService()
    {
        var grandparent = new Folder { Id = Guid.NewGuid(), Name = "Grandparent" };
        var parent = new Folder { Id = Guid.NewGuid(), Name = "Parent", ParentId = grandparent.Id };
        var child = new Movie { Id = Guid.NewGuid(), Name = "Child", ParentId = parent.Id };

        var lookup = new Mock<IItemLookupService>(MockBehavior.Strict);
        lookup.Setup(l => l.GetItemById(parent.Id)).Returns(parent);
        lookup.Setup(l => l.GetItemById(grandparent.Id)).Returns(grandparent);

        var chain = child.GetParents(lookup.Object).ToList();

        Assert.Equal(new BaseItem[] { parent, grandparent }, chain);
        lookup.Verify(l => l.GetItemById(parent.Id), Times.Once);
        lookup.Verify(l => l.GetItemById(grandparent.Id), Times.Once);
    }

    [Fact]
    public void GetParents_WithLookup_NoParent_ReturnsEmpty()
    {
        var child = new Movie { Id = Guid.NewGuid(), Name = "Child", ParentId = Guid.Empty };

        var lookup = new Mock<IItemLookupService>(MockBehavior.Strict);

        var chain = child.GetParents(lookup.Object).ToList();

        Assert.Empty(chain);
    }

    // ---------------------------------------------------------------
    // FindParent<T>(lookup)
    // ---------------------------------------------------------------

    [Fact]
    public void FindParent_WithLookup_FindsTypeInChain()
    {
        var boxSet = new BoxSet { Id = Guid.NewGuid(), Name = "BoxSet" };
        var parent = new Folder { Id = Guid.NewGuid(), Name = "Parent", ParentId = boxSet.Id };
        var child = new Movie { Id = Guid.NewGuid(), Name = "Child", ParentId = parent.Id };

        var lookup = new Mock<IItemLookupService>(MockBehavior.Strict);
        lookup.Setup(l => l.GetItemById(parent.Id)).Returns(parent);
        lookup.Setup(l => l.GetItemById(boxSet.Id)).Returns(boxSet);

        var result = child.FindParent<BoxSet>(lookup.Object);

        Assert.Same(boxSet, result);
    }

    [Fact]
    public void FindParent_WithLookup_TypeAbsentFromChain_ReturnsNull()
    {
        var parent = new Folder { Id = Guid.NewGuid(), Name = "Parent" };
        var child = new Movie { Id = Guid.NewGuid(), Name = "Child", ParentId = parent.Id };

        var lookup = new Mock<IItemLookupService>(MockBehavior.Strict);
        lookup.Setup(l => l.GetItemById(parent.Id)).Returns(parent);

        var result = child.FindParent<BoxSet>(lookup.Object);

        Assert.Null(result);
    }

    [Fact]
    public void FindParent_WithLookup_NullLookup_Throws()
    {
        var child = new Movie { Id = Guid.NewGuid(), Name = "Child" };

        Assert.Throws<ArgumentNullException>(() => child.FindParent<Folder>(null!));
    }

    // ---------------------------------------------------------------
    // GetOwner(lookup)
    // ---------------------------------------------------------------

    [Fact]
    public void GetOwner_WithLookup_ResolvesViaLookupService()
    {
        var owner = new Movie { Id = Guid.NewGuid(), Name = "Owner" };
        var item = new Movie { Id = Guid.NewGuid(), Name = "Item", OwnerId = owner.Id };

        var lookup = new Mock<IItemLookupService>(MockBehavior.Strict);
        lookup.Setup(l => l.GetItemById(owner.Id)).Returns(owner);

        var result = item.GetOwner(lookup.Object);

        Assert.Same(owner, result);
        lookup.Verify(l => l.GetItemById(owner.Id), Times.Once);
    }

    [Fact]
    public void GetOwner_WithLookup_EmptyOwnerId_ReturnsNullWithoutConsultingLookup()
    {
        var item = new Movie { Id = Guid.NewGuid(), Name = "Item", OwnerId = Guid.Empty };

        var lookup = new Mock<IItemLookupService>(MockBehavior.Strict);

        var result = item.GetOwner(lookup.Object);

        Assert.Null(result);
        lookup.Verify(l => l.GetItemById(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public void GetOwner_WithLookup_NullLookup_Throws()
    {
        var item = new Movie { Id = Guid.NewGuid(), Name = "Item" };

        Assert.Throws<ArgumentNullException>(() => item.GetOwner(null!));
    }
}
