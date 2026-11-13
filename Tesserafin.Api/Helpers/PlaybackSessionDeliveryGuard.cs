using System;
using System.Linq;
using System.Security.Claims;
using Tesserafin.Api.Constants;
using Tesserafin.Api.Extensions;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Data;
using Tesserafin.Database.Implementations.Enums;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Playback.Execution;

namespace Tesserafin.Api.Helpers;

/// <summary>
/// The one access decision of every media route a playback session owns (its file route, its HLS
/// route): what the session's retained plan lets this caller read right now. The client names
/// nothing that is served; the item, media source and play session it sends are only compared with
/// what the session already holds.
/// </summary>
/// <remarks>
/// Asked on every request. A deleted, expired or re-planned session, an item or an alternate
/// version withdrawn from the owner after planning, and a disabled owner all stop being served at
/// the next request. A revoked capability is refused by authentication before this is reached.
/// </remarks>
public sealed class PlaybackSessionDeliveryGuard
{
    private static readonly TimeSpan _keepAliveAfter = TimeSpan.FromMinutes(15);

    private readonly IPlaybackSessionManager _playbackSessionManager;
    private readonly IV2PlanStore _v2PlanStore;
    private readonly IUserManager _userManager;
    private readonly IItemAccessService _itemAccessService;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackSessionDeliveryGuard"/> class.
    /// </summary>
    /// <param name="playbackSessionManager">Instance of the <see cref="IPlaybackSessionManager"/> interface.</param>
    /// <param name="v2PlanStore">Instance of the <see cref="IV2PlanStore"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="itemAccessService">Instance of the <see cref="IItemAccessService"/> interface.</param>
    public PlaybackSessionDeliveryGuard(
        IPlaybackSessionManager playbackSessionManager,
        IV2PlanStore v2PlanStore,
        IUserManager userManager,
        IItemAccessService itemAccessService)
    {
        _playbackSessionManager = playbackSessionManager;
        _v2PlanStore = v2PlanStore;
        _userManager = userManager;
        _itemAccessService = itemAccessService;
    }

    /// <summary>
    /// Decides whether a caller may be served a session's media.
    /// </summary>
    /// <param name="id">The session.</param>
    /// <param name="caller">The authenticated caller.</param>
    /// <param name="itemId">The item the caller names; must be the session's.</param>
    /// <param name="mediaSourceId">The media source the caller names; must be the plan's.</param>
    /// <param name="playSessionId">The play session the caller names; must be the session's.</param>
    /// <param name="servedBy">Whether the retained plan is one the asking route serves.</param>
    /// <param name="allowAdministrator">Whether an administrator may read a session it does not own.</param>
    /// <returns>The decision, with what to serve when it is <see cref="PlaybackSessionDeliveryOutcome.Granted"/>.</returns>
    public PlaybackSessionDelivery Resolve(
        PlaybackSessionId id,
        ClaimsPrincipal caller,
        Guid itemId,
        string mediaSourceId,
        string playSessionId,
        Func<PlaybackExecutionPlan, bool> servedBy,
        bool allowAdministrator)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(servedBy);

        var session = _playbackSessionManager.Get(id);
        var options = session?.Request?.Options;
        if (session is null || options is null)
        {
            return PlaybackSessionDelivery.NotFound;
        }

        if (!options.UserId.Equals(caller.GetUserId())
            && !(allowAdministrator && caller.IsInRole(UserRoles.Administrator)))
        {
            return PlaybackSessionDelivery.Forbidden;
        }

        if (session.Plan.StreamInfo is not null
            || !_v2PlanStore.TryGet(session.Id, out var record)
            || record?.ExecutionPlan is not { } plan
            || !servedBy(plan)
            || !options.ItemId.Equals(itemId)
            || !string.Equals(plan.SourceId, mediaSourceId, StringComparison.Ordinal)
            || string.IsNullOrEmpty(session.PlaySessionId)
            || !string.Equals(session.PlaySessionId, playSessionId, StringComparison.Ordinal))
        {
            return PlaybackSessionDelivery.NotFound;
        }

        // Access can be withdrawn after planning (library access, parental rating, disabled
        // account): the session's owner must still see the item on every request.
        var owner = _userManager.GetUserById(options.UserId);
        if (owner is null || owner.HasPermission(PermissionKind.IsDisabled)
            || _itemAccessService.GetVisibleItemById<BaseItem>(itemId, owner) is null)
        {
            return PlaybackSessionDelivery.NotFound;
        }

        // An alternate version is its own item, with its own access rules: the version the plan
        // selected must still be visible to the owner too, not only the film it belongs to.
        if (!Guid.TryParse(plan.SourceId, out var sourceItemId)
            || (!sourceItemId.Equals(itemId) && _itemAccessService.GetVisibleItemById<BaseItem>(sourceItemId, owner) is null))
        {
            return PlaybackSessionDelivery.NotFound;
        }

        var source = options.MediaSources.FirstOrDefault(s => string.Equals(s.Id, plan.SourceId, StringComparison.Ordinal));
        if (!LocalVideoPlanner.IsLocalFileSource(source) || !System.IO.File.Exists(source!.Path))
        {
            return PlaybackSessionDelivery.NotFound;
        }

        // The session's routes are the only thing a long playback asks of it: a film paused and
        // resumed, or simply longer than the manager's idle limit, must not lose its session while
        // it is being read. Track with the session's own plan refreshes that stamp and, for a
        // session a client planned, changes nothing else. Not on every segment: once the stamp is
        // old enough to matter.
        if (DateTimeOffset.UtcNow - session.UpdatedAt > _keepAliveAfter)
        {
            _playbackSessionManager.Track(session.Kind, session.Plan, session.PlaySessionId, session.PlaybackAttemptId);
        }

        return new PlaybackSessionDelivery(PlaybackSessionDeliveryOutcome.Granted, session, options, plan, source);
    }
}
