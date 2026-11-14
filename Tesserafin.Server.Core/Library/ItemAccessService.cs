#pragma warning disable CS1591

using System;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Data.Entities;

namespace Tesserafin.Server.Core.Library
{
    /// <summary>
    /// Applies user visibility rules on top of <see cref="IItemLookupService"/>. This is the sole
    /// production implementation of <see cref="IItemAccessService"/>.
    /// </summary>
    /// <remarks>
    /// Extracted in PR77 from the user-aware overload previously living on
    /// <c>ItemLookupService.GetItemById&lt;T&gt;(Guid, User?)</c>/<c>ItemIsVisible</c> - lookup
    /// (finding an item) and access control (deciding whether a user may see it) are different
    /// responsibilities, and the latter was leaking a hidden static dependency into the former.
    /// <see cref="ItemLookupService"/> itself has no knowledge of users or visibility any more.
    /// Tag inheritance and the tag filter (DEBT-SERVER-3), then the parents and the libraries of the
    /// folder restriction (DEBT-SERVER-4), come from <see cref="IStandaloneVisibilityService"/>, passed
    /// down the entities' visibility overrides through
    /// <see cref="BaseItem.IsVisibleStandalone(User, ITagVisibilityService)"/>. What still reads the
    /// static <see cref="BaseItem.LibraryManager"/> on this path is channel and box set visibility, and
    /// the library options read for a rating's country code once neither the item, its parents nor its
    /// libraries carry one.
    /// </remarks>
    internal sealed class ItemAccessService : IItemAccessService
    {
        private readonly IItemLookupService _itemLookupService;
        private readonly IStandaloneVisibilityService _tagVisibility;

        /// <summary>
        /// Initializes a new instance of the <see cref="ItemAccessService"/> class.
        /// </summary>
        /// <param name="itemLookupService">The item lookup service used to resolve the item before applying visibility.</param>
        /// <param name="tagVisibility">Tag inheritance and the tag filter, the parents and the libraries.</param>
        public ItemAccessService(IItemLookupService itemLookupService, IStandaloneVisibilityService tagVisibility)
        {
            _itemLookupService = itemLookupService;
            _tagVisibility = tagVisibility;
        }

        /// <inheritdoc />
        public T? GetVisibleItemById<T>(Guid id, User user)
            where T : BaseItem
        {
            var item = _itemLookupService.GetItemById<T>(id);
            return ItemIsVisible(item, user) ? item : null;
        }

        /// <summary>
        /// Determines whether <paramref name="item"/> is visible to <paramref name="user"/>.
        /// <see cref="UserRootFolder"/> is always visible regardless of a user's tag/parental
        /// restrictions.
        /// </summary>
        private bool ItemIsVisible(BaseItem? item, User user)
        {
            if (item is null)
            {
                return false;
            }

            return item is UserRootFolder || item.IsVisibleStandalone(user, _tagVisibility);
        }
    }
}
