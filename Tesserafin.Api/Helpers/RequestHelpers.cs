using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Tesserafin.Api.Constants;
using Tesserafin.Api.Extensions;
using Tesserafin.Common.Extensions;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Net;
using Tesserafin.Controller.Session;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Querying;

namespace Tesserafin.Api.Helpers;

/// <summary>
/// Request Extensions.
/// </summary>
public static class RequestHelpers
{
    /// <summary>
    /// Get Order By.
    /// </summary>
    /// <param name="sortBy">Sort By. Comma delimited string.</param>
    /// <param name="requestedSortOrder">Sort Order. Comma delimited string.</param>
    /// <returns>Order By.</returns>
    public static (ItemSortBy, SortOrder)[] GetOrderBy(IReadOnlyList<ItemSortBy> sortBy, IReadOnlyList<SortOrder> requestedSortOrder)
    {
        if (sortBy.Count == 0)
        {
            return Array.Empty<(ItemSortBy, SortOrder)>();
        }

        var result = new (ItemSortBy, SortOrder)[sortBy.Count];
        var i = 0;
        // Add elements which have a SortOrder specified
        for (; i < requestedSortOrder.Count; i++)
        {
            result[i] = (sortBy[i], requestedSortOrder[i]);
        }

        // Add remaining elements with the first specified SortOrder
        // or the default one if no SortOrders are specified
        var order = requestedSortOrder.Count > 0 ? requestedSortOrder[0] : SortOrder.Ascending;
        for (; i < sortBy.Count; i++)
        {
            result[i] = (sortBy[i], order);
        }

        return result;
    }

    /// <summary>
    /// Whether a user may be served a media source of an item it may see.
    /// </summary>
    /// <remarks>
    /// A media source can be another item - an alternate version, with tags, a rating and a
    /// library of its own - and is then that item's file. A source that is no item at all (a live
    /// stream, a channel's) is the item's own.
    /// </remarks>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userId">The user, or empty for a principal that names none (an API key).</param>
    /// <param name="itemId">The item the source was asked of, already resolved for this user.</param>
    /// <param name="mediaSourceId">The media source id.</param>
    /// <returns>False when the source is an item this user may not see.</returns>
    internal static bool CanSeeMediaSource(ILibraryManager libraryManager, Guid userId, Guid itemId, string? mediaSourceId)
        => userId.IsEmpty()
            || !Guid.TryParse(mediaSourceId, out var sourceItemId)
            || sourceItemId.Equals(itemId)
            || libraryManager.GetItemById<BaseItem>(sourceItemId) is null
            || libraryManager.GetItemById<BaseItem>(sourceItemId, userId) is not null;

    /// <summary>
    /// The media source id to ask a lower layer for: the one named, or - when it is an item
    /// withheld from the user - one that names nothing, so that a withheld version and a version
    /// that does not exist get one and the same answer.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userId">The user, or empty for a principal that names none (an API key).</param>
    /// <param name="itemId">The item the source was asked of, already resolved for this user.</param>
    /// <param name="mediaSourceId">The media source id.</param>
    /// <returns>The media source id to use.</returns>
    internal static string? VisibleMediaSourceId(ILibraryManager libraryManager, Guid userId, Guid itemId, string? mediaSourceId)
        => CanSeeMediaSource(libraryManager, userId, itemId, mediaSourceId)
            ? mediaSourceId
            : Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Checks if the user can access a user.
    /// </summary>
    /// <param name="claimsPrincipal">The <see cref="ClaimsPrincipal"/> for the current request.</param>
    /// <param name="userId">The user id.</param>
    /// <returns>A <see cref="bool"/> whether the user can access the user.</returns>
    internal static Guid GetUserId(ClaimsPrincipal claimsPrincipal, Guid? userId)
    {
        var authenticatedUserId = claimsPrincipal.GetUserId();

        // UserId not provided, fall back to authenticated user id.
        if (userId.IsNullOrEmpty())
        {
            return authenticatedUserId;
        }

        // User must be administrator to access another user.
        var isAdministrator = claimsPrincipal.IsInRole(UserRoles.Administrator);
        if (!userId.Value.Equals(authenticatedUserId) && !isAdministrator)
        {
            throw new SecurityException("Forbidden");
        }

        return userId.Value;
    }

    /// <summary>
    /// Checks if the user can update an entry.
    /// </summary>
    /// <param name="claimsPrincipal">The <see cref="ClaimsPrincipal"/> for the current request.</param>
    /// <param name="user">The user id.</param>
    /// <param name="restrictUserPreferences">Whether to restrict the user preferences.</param>
    /// <returns>A <see cref="bool"/> whether the user can update the entry.</returns>
    internal static bool AssertCanUpdateUser(ClaimsPrincipal claimsPrincipal, User user, bool restrictUserPreferences)
    {
        var authenticatedUserId = claimsPrincipal.GetUserId();
        var isAdministrator = claimsPrincipal.IsInRole(UserRoles.Administrator);

        // If they're going to update the record of another user, they must be an administrator
        if (!user.Id.Equals(authenticatedUserId) && !isAdministrator)
        {
            return false;
        }

        // TODO the EnableUserPreferenceAccess policy does not seem to be used elsewhere
        if (!restrictUserPreferences || isAdministrator)
        {
            return true;
        }

        return user.EnableUserPreferenceAccess;
    }

    /// <summary>
    /// Get the session based on http request.
    /// </summary>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="httpContext">The http context.</param>
    /// <param name="userId">The optional userid.</param>
    /// <returns>The session.</returns>
    /// <exception cref="ResourceNotFoundException">Session not found.</exception>
    public static async Task<SessionInfo> GetSession(ISessionManager sessionManager, IUserManager userManager, HttpContext httpContext, Guid? userId = null)
    {
        userId ??= httpContext.User.GetUserId();
        User? user = null;
        if (!userId.IsNullOrEmpty())
        {
            user = userManager.GetUserById(userId.Value);
        }

        var session = await sessionManager.LogSessionActivity(
            httpContext.User.GetClient(),
            httpContext.User.GetVersion(),
            httpContext.User.GetDeviceId(),
            httpContext.User.GetDevice(),
            httpContext.GetNormalizedRemoteIP().ToString(),
            user).ConfigureAwait(false);

        if (session is null)
        {
            throw new ResourceNotFoundException("Session not found.");
        }

        return session;
    }

    internal static async Task<string> GetSessionId(ISessionManager sessionManager, IUserManager userManager, HttpContext httpContext)
    {
        var session = await GetSession(sessionManager, userManager, httpContext).ConfigureAwait(false);

        return session.Id;
    }

    internal static QueryResult<BaseItemDto> CreateQueryResult(
        QueryResult<(BaseItem Item, ItemCounts ItemCounts)> result,
        DtoOptions dtoOptions,
        IDtoService dtoService,
        bool includeItemTypes,
        User? user)
    {
        var dtos = result.Items.Select(i =>
        {
            var (baseItem, counts) = i;
            var dto = dtoService.GetItemByNameDto(baseItem, dtoOptions, null, user);

            if (includeItemTypes)
            {
                dto.ChildCount = counts.ItemCount;
                dto.ProgramCount = counts.ProgramCount;
                dto.SeriesCount = counts.SeriesCount;
                dto.EpisodeCount = counts.EpisodeCount;
                dto.MovieCount = counts.MovieCount;
                dto.TrailerCount = counts.TrailerCount;
                dto.AlbumCount = counts.AlbumCount;
                dto.SongCount = counts.SongCount;
                dto.ArtistCount = counts.ArtistCount;
            }

            return dto;
        });

        return new QueryResult<BaseItemDto>(
            result.StartIndex,
            result.TotalRecordCount,
            dtos.ToArray());
    }
}
