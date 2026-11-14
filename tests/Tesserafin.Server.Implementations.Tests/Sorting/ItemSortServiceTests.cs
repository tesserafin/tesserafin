using System;
using System.Linq;
using Moq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Sorting;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Server.Core.Sorting;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Sorting;

public class ItemSortServiceTests
{
    private const ItemSortBy NameSortBy = ItemSortBy.Name;
    private const ItemSortBy UserSortBy = ItemSortBy.IsPlayed;

    private static ItemSortService CreateService(out Mock<IUserManager> userManager, out Mock<IUserDataManager> userDataManager)
    {
        userManager = new Mock<IUserManager>();
        userDataManager = new Mock<IUserDataManager>();

        return new ItemSortService(userManager.Object, userDataManager.Object);
    }

    private static BaseItem CreateItem(string name)
        => new Movie { Id = Guid.NewGuid(), Name = name };

    [Fact]
    public void Sort_WithUnregisteredComparer_ReturnsItemsUnchanged()
    {
        var service = CreateService(out _, out _);

        var items = new[] { CreateItem("c"), CreateItem("a"), CreateItem("b") };

        var result = service.Sort(items, null, [NameSortBy], SortOrder.Ascending).ToArray();

        // No comparer registered via AddParts -> GetComparer returns null -> falls back to original order.
        Assert.Equal(items, result);
    }

    [Fact]
    public void Sort_Ascending_OrdersUsingRegisteredComparer()
    {
        var service = CreateService(out _, out _);
        service.AddParts([new FakeNameComparer()]);

        var c = CreateItem("c");
        var a = CreateItem("a");
        var b = CreateItem("b");

        var result = service.Sort([c, a, b], null, [NameSortBy], SortOrder.Ascending).ToArray();

        Assert.Equal([a, b, c], result);
    }

    [Fact]
    public void Sort_Descending_ReversesOrder()
    {
        var service = CreateService(out _, out _);
        service.AddParts([new FakeNameComparer()]);

        var c = CreateItem("c");
        var a = CreateItem("a");
        var b = CreateItem("b");

        var result = service.Sort([c, a, b], null, [NameSortBy], SortOrder.Descending).ToArray();

        Assert.Equal([c, b, a], result);
    }

    [Fact]
    public void Sort_TupleOverload_SkipsUnregisteredComparer()
    {
        var service = CreateService(out _, out _);

        var items = new[] { CreateItem("c"), CreateItem("a"), CreateItem("b") };

        var result = service.Sort(items, null, [(NameSortBy, SortOrder.Ascending)]).ToArray();

        // Mirrors the "IEnumerable<ItemSortBy>" overload above but exercises the "continue" branch
        // of the tuple overload (comparer is null) instead of the ".Where(c => c is not null)" branch.
        Assert.Equal(items, result);
    }

    [Fact]
    public void Sort_TupleOverload_OrdersUsingRegisteredComparer()
    {
        var service = CreateService(out _, out _);
        service.AddParts([new FakeNameComparer()]);

        var c = CreateItem("c");
        var a = CreateItem("a");
        var b = CreateItem("b");

        var result = service.Sort([c, a, b], null, [(NameSortBy, SortOrder.Descending)]).ToArray();

        Assert.Equal([c, b, a], result);
    }

    [Fact]
    public void Sort_WithMultipleComparers_AppliesThenBy()
    {
        var service = CreateService(out _, out _);
        service.AddParts([new FakeGroupComparer(), new FakeNameComparer()]);

        var a1 = CreateItem("a1"); // group 1
        var a0 = CreateItem("a0"); // group 1
        var b0 = CreateItem("b0"); // group 0

        // Group first (ascending, group 0 before group 1), then name within each group.
        var result = service.Sort([a1, a0, b0], null, [ItemSortBy.Studio, NameSortBy], SortOrder.Ascending).ToArray();

        Assert.Equal([b0, a0, a1], result);
    }

    [Fact]
    public void Sort_WithUserComparer_InjectsUserAndManagersIntoANewInstancePerCall()
    {
        var service = CreateService(out var userManager, out var userDataManager);
        service.AddParts([new FakeUserComparer()]);

        var user = new User("test-user", "Tesserafin.Server.Implementations.Tests", "Tesserafin.Server.Implementations.Tests");
        var items = new[] { CreateItem("a"), CreateItem("b") };

        _ = service.Sort(items, user, [UserSortBy], SortOrder.Ascending).ToArray();

        Assert.NotNull(FakeUserComparer.LastAssignedUser);
        Assert.Same(user, FakeUserComparer.LastAssignedUser);
        Assert.Same(userManager.Object, FakeUserComparer.LastAssignedUserManager);
        Assert.Same(userDataManager.Object, FakeUserComparer.LastAssignedUserDataManager);

        // GetComparer creates a fresh instance (via Activator.CreateInstance) on every resolution
        // rather than mutating a shared registered instance.
        Assert.True(FakeUserComparer.InstancesCreated >= 1);
    }

    [Fact]
    public void Sort_WithRandomComparer_ShufflesButKeepsAllItems()
    {
        var service = CreateService(out _, out _);
        service.AddParts([new RandomComparer()]);

        var items = Enumerable.Range(0, 10).Select(i => CreateItem(i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();

        var result = service.Sort(items, null, [ItemSortBy.Random], SortOrder.Ascending).ToArray();

        Assert.Equal(items.Length, result.Length);
        Assert.Equal(items.OrderBy(i => i.Id), result.OrderBy(i => i.Id));
    }

    [Fact]
    public void Sort_RandomFollowedByRealComparer_ResetsOrderingBeforeApplyingIt()
    {
        var service = CreateService(out _, out _);
        service.AddParts([new RandomComparer(), new FakeNameComparer()]);

        var c = CreateItem("c");
        var a = CreateItem("a");
        var b = CreateItem("b");

        // Random shuffles first, but the subsequent real comparer must start a fresh ordering
        // rather than "ThenBy" on top of the (no longer meaningful) shuffled sequence.
        var result = service.Sort([c, a, b], null, [ItemSortBy.Random, NameSortBy], SortOrder.Ascending).ToArray();

        Assert.Equal([a, b, c], result);
    }

    private sealed class FakeNameComparer : IBaseItemComparer
    {
        public ItemSortBy Type => NameSortBy;

        public int Compare(BaseItem? x, BaseItem? y) => string.CompareOrdinal(x?.Name, y?.Name);
    }

    private sealed class FakeGroupComparer : IBaseItemComparer
    {
        public ItemSortBy Type => ItemSortBy.Studio;

        public int Compare(BaseItem? x, BaseItem? y)
        {
            var xGroup = x?.Name?.StartsWith('a') == true ? 1 : 0;
            var yGroup = y?.Name?.StartsWith('a') == true ? 1 : 0;
            return xGroup.CompareTo(yGroup);
        }
    }

    private sealed class FakeUserComparer : IUserBaseItemComparer
    {
        private static int _instancesCreated;

        private User? _user;
        private IUserManager? _userManager;
        private IUserDataManager? _userDataManager;

        public FakeUserComparer()
        {
            _instancesCreated++;
        }

        public static User? LastAssignedUser { get; private set; }

        public static IUserManager? LastAssignedUserManager { get; private set; }

        public static IUserDataManager? LastAssignedUserDataManager { get; private set; }

        public static int InstancesCreated => _instancesCreated;

        public ItemSortBy Type => UserSortBy;

        public User? User
        {
            get => _user;
            set
            {
                _user = value;
                LastAssignedUser = value;
            }
        }

        public IUserManager? UserManager
        {
            get => _userManager;
            set
            {
                _userManager = value;
                LastAssignedUserManager = value;
            }
        }

        public IUserDataManager? UserDataManager
        {
            get => _userDataManager;
            set
            {
                _userDataManager = value;
                LastAssignedUserDataManager = value;
            }
        }

        public int Compare(BaseItem? x, BaseItem? y) => 0;
    }
}
