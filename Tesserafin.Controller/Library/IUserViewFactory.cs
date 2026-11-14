using System;
using Tesserafin.Controller.Entities;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;

namespace Tesserafin.Controller.Library
{
    /// <summary>
    /// Leaf port covering named/shadow user-view creation - the 4 <c>GetNamedView</c>
    /// overloads plus <c>GetShadowView</c>, ported off <see cref="ILibraryManager"/>
    /// (RFC <c>docs/rfc-di-query-user-views-v2.md</c> §2, PR106b). Signatures are identical to the
    /// historical <c>ILibraryManager</c> overloads (<c>ILibraryManager.cs:433,448,461,475,489</c>).
    /// </summary>
    /// <remarks>
    /// Introduced in PR106b, built on top of the PR106a <see cref="IItemStore"/> leaf. See the
    /// concrete <c>UserViewFactory</c> implementation (Tesserafin.Server.Core) for the RFC invariant I1
    /// (no eager construction-graph edge to <see cref="ILibraryManager"/>, <c>IUserViewManager</c>,
    /// <c>IChannelManager</c> or <c>ILiveTvManager</c>) and I2 (<c>Lazy&lt;IProviderManager&gt;.Value</c>
    /// evaluated only after persistence+registration) proofs. <see cref="ILibraryManager"/> keeps its
    /// own <c>GetNamedView</c>/<c>GetShadowView</c> members for API compatibility, but delegates their
    /// bodies to this port; <c>IUserViewManager</c>/<c>LiveTvManager</c> continue calling through
    /// <see cref="ILibraryManager"/> until PR109/PR110 migrate them directly onto this port.
    /// </remarks>
    public interface IUserViewFactory
    {
        /// <summary>
        /// Gets the named view.
        /// </summary>
        /// <param name="user">The user.</param>
        /// <param name="name">The name.</param>
        /// <param name="parentId">The parent identifier.</param>
        /// <param name="viewType">Type of the view.</param>
        /// <param name="sortName">Name of the sort.</param>
        /// <returns>The named view.</returns>
        UserView GetNamedView(
            User user,
            string name,
            Guid parentId,
            CollectionType? viewType,
            string sortName);

        /// <summary>
        /// Gets the named view.
        /// </summary>
        /// <param name="user">The user.</param>
        /// <param name="name">The name.</param>
        /// <param name="viewType">Type of the view.</param>
        /// <param name="sortName">Name of the sort.</param>
        /// <returns>The named view.</returns>
        UserView GetNamedView(
            User user,
            string name,
            CollectionType? viewType,
            string sortName);

        /// <summary>
        /// Gets the named view.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="viewType">Type of the view.</param>
        /// <param name="sortName">Name of the sort.</param>
        /// <returns>The named view.</returns>
        UserView GetNamedView(
            string name,
            CollectionType viewType,
            string sortName);

        /// <summary>
        /// Gets the named view.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="parentId">The parent identifier.</param>
        /// <param name="viewType">Type of the view.</param>
        /// <param name="sortName">Name of the sort.</param>
        /// <param name="uniqueId">The unique identifier.</param>
        /// <returns>The named view.</returns>
        UserView GetNamedView(
            string name,
            Guid parentId,
            CollectionType? viewType,
            string sortName,
            string uniqueId);

        /// <summary>
        /// Gets the shadow view.
        /// </summary>
        /// <param name="parent">The parent.</param>
        /// <param name="viewType">Type of the view.</param>
        /// <param name="sortName">Name of the sort.</param>
        /// <returns>The shadow view.</returns>
        UserView GetShadowView(
            BaseItem parent,
            CollectionType? viewType,
            string sortName);
    }
}
