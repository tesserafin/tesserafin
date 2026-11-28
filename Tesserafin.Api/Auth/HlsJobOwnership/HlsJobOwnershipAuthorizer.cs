using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Tesserafin.Api.Auth.PlaybackCapabilityPolicy;
using Tesserafin.Api.Extensions;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Controller.Session;
using Tesserafin.Extensions;

namespace Tesserafin.Api.Auth.HlsJobOwnership;

/// <summary>
/// Compares the caller of an HLS resource with the transcoding job that produced it
/// (#153-LTV-R3).
/// </summary>
/// <remarks>
/// WHAT #153-LTV-R2 MEASURED, AND WHY A POLICY COULD NOT FIX IT. <c>Policies.MediaDelivery</c>
/// succeeds for <c>GetIsApiKey() || !GetUserId().IsEmpty()</c>; <c>DefaultAuthorizationHandler</c>
/// applies exactly two gates to a durable principal — remote access when off the local network,
/// and the parental schedule — and succeeds an administrator outright. None of those is job
/// ownership, and an administrator passes every permission policy in this server because
/// <c>UserPermissionRequirement</c> subclasses <c>DefaultAuthorizationRequirement</c>. So a second
/// authenticated user read the first user's live segment bytes. The comparison has to happen here,
/// against the job, and it admits no exemption for a role, for remote access or for the local
/// network.
///
/// TWO CREDENTIALS, NEVER ONE FALLING BACK ON THE OTHER. If a capability was presented, the
/// capability decides — and if it does not match the job the answer is a refusal, not a second
/// attempt as a durable token. Falling back would make an invalid capability strictly better than
/// no capability at all.
///
/// REVOCATION IS AT THE REQUEST BOUNDARY, NOT INSIDE A RESPONSE (#153-LTV-R5, documenting R4
/// finding F7). What a stop — <c>DELETE Videos/ActiveEncodings</c>, a teardown, a reap — actually
/// does to a request in progress was measured by R4 Phase 4b: an already-authorized read, whose job
/// was killed at 274 ms, still answered 200 with 93 228 bytes at 330 ms. That is accepted, and this
/// is the semantics it is accepted under:
///
/// <list type="bullet">
/// <item>THE LINEARIZATION POINT is the successful authorization decision taken while the job's
/// binding still exists. <see cref="Decide"/> resolves the binding once and returns it inside the
/// decision, so everything downstream reads that snapshot and never the registry again.</item>
/// <item>A REQUEST PAST THAT POINT MAY FINISH SENDING. Its bytes were authorized when the decision
/// was taken; the stop does not reach back into a response already in flight.</item>
/// <item>THE STOP REMOVES THE BINDING FOR EVERY NEW REQUEST. The job leaves the registry, so the
/// next <see cref="Decide"/> resolves nothing. A stop that did not end the job's process is not
/// one: the binding stays, and so does its owner (POLISH-2-R4).</item>
/// <item>NO NEW OPENING CAN BE AUTHORIZED AFTERWARDS. <c>NoSuchJob</c> carries no binding at all,
/// and a refusal carries none either, so a request that arrives after the stop has no path to
/// open — not even to a residual file still on disk.</item>
/// <item>NO BINDING IS EVER RECREATED, AND THERE IS NO FALLBACK. The historical resolution — name a
/// file in the shared transcode folder — is gone. R4 Phase 4c measured the stronger fact that the
/// stop also deletes the residual segments: 88 files on disk, zero afterwards, the dead job's
/// deepest segment reachable by nobody including its own owner.</item>
/// </list>
///
/// WHO CAN EVER BE IN THAT STATE. Only the job's own owner. A non-owner is refused AT the
/// linearization point and therefore never has an authorized request to leave in flight, which is
/// why completing one is a liveness property rather than a hole. The four interlocks are pinned by
/// <c>InFlightRevocationTests</c>.
/// </remarks>
public sealed class HlsJobOwnershipAuthorizer : IHlsJobOwnershipAuthorizer
{
    private readonly IHlsSegmentBindingRegistry _bindings;
    private readonly ISessionManager _sessionManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsJobOwnershipAuthorizer"/> class.
    /// </summary>
    /// <param name="bindings">The job binding registry.</param>
    /// <param name="sessionManager">The session manager, used to resolve a capability's session to its device.</param>
    public HlsJobOwnershipAuthorizer(IHlsSegmentBindingRegistry bindings, ISessionManager sessionManager)
    {
        _bindings = bindings;
        _sessionManager = sessionManager;
    }

    /// <inheritdoc />
    public HlsJobOwnershipDecision AuthorizeByPlaylistId(HttpContext context, string playlistId)
        => Decide(context, _bindings.ResolveByPlaylistId(playlistId));

    /// <inheritdoc />
    public HlsJobOwnershipDecision AuthorizeByOutputPath(HttpContext context, string outputPath)
        => Decide(context, _bindings.ResolveByOutputPath(outputPath));

    /// <inheritdoc />
    public HlsJobOwnershipDecision AuthorizeProgressiveByOutputPath(HttpContext context, string outputPath)
        => _bindings is IProgressiveJobBindingRegistry progressive
            ? Decide(context, progressive.ResolveProgressiveByOutputPath(outputPath))
            : throw new NotSupportedException("This registry cannot resolve a progressive job by its output.");

    /// <inheritdoc />
    public bool OwnsJob(HttpContext context, Guid ownerUserId, string? ownerDeviceId)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (ownerUserId.IsEmpty() || string.IsNullOrEmpty(ownerDeviceId))
        {
            return false;
        }

        var provenance = PlaybackCapabilityProvenance.Resolve(context);
        return provenance.Outcome != PlaybackCapabilityProvenanceOutcome.Refuse
               && CallerIsTheOwner(context, provenance.Capability, ownerUserId, ownerDeviceId);
    }

    private HlsJobOwnershipDecision Decide(HttpContext context, HlsSegmentBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(context);

        // STEP 1. The job's metadata, and nothing else. No file has been named yet, let alone
        // opened: this method returns a decision, never a path.
        if (binding is null)
        {
            // No fallback to the historical resolution. A live job's segment files outlive it
            // (#153-LTV-S0 measured 22 surviving a teardown); once the job is gone they are
            // unreachable rather than served to whoever names them.
            return HlsJobOwnershipDecision.NoSuchJob();
        }

        // STEP 2, CORROBORATIVE. A job with no resolvable owner belongs to nobody, and nobody can
        // be it. Both halves are reachable: an api-key principal resolves to Guid.Empty, and a
        // credential that names no device leaves the job device-less. An absent owner is not a
        // wildcard.
        //
        // MEASURED (#153-LTV-R4 finding A6 / F4, R5 repro p0-9). Deleting this block leaves the
        // suite at 69/69 green: an owner-less job still refuses every caller at GUARD 3 and
        // GUARD 4 below, because `callerUserId.Equals(Guid.Empty)` is false for any real caller and
        // `string.Equals(callerDeviceId, null)` is false for any caller with a device. It is
        // defence in depth, and it is stated as defence in depth rather than as a lock nothing
        // else provides.
        if (binding.UserId.IsEmpty() || string.IsNullOrEmpty(binding.DeviceId))
        {
            return HlsJobOwnershipDecision.Refused();
        }

        // STEP 3. Which credential is deciding.
        var provenance = PlaybackCapabilityProvenance.Resolve(context);
        if (provenance.Outcome == PlaybackCapabilityProvenanceOutcome.Refuse)
        {
            return HlsJobOwnershipDecision.Refused();
        }

        var capability = provenance.Capability;
        if (capability is not null && !CapabilityMatchesTheJob(capability, binding))
        {
            // A presented capability is the whole decision. There is deliberately no fallback to
            // the durable path below: falling back would make an invalid capability strictly
            // better than presenting none at all.
            return HlsJobOwnershipDecision.Refused();
        }

        // STEP 4. Who the caller is, resolved exactly as the job's own owner was recorded.
        return CallerIsTheOwner(context, capability, binding.UserId, binding.DeviceId)
            ? HlsJobOwnershipDecision.Authorized(binding)
            : HlsJobOwnershipDecision.Refused();
    }

    /// <summary>
    /// The identity comparison, shared by both credentials. It comes from the validated principal
    /// and from the validation result; a url parameter may corroborate it elsewhere, never create
    /// it here.
    /// </summary>
    /// <remarks>
    /// THE FOUR GUARDS, IN THE ORDER THEY RUN, AND WHICH ONE ACTUALLY DECIDES (#153-LTV-R5,
    /// repairing R4 finding F4). Each is annotated at its own site below.
    ///
    /// <list type="number">
    /// <item>authentication — CORROBORATIVE. An unauthenticated principal has no user id, so
    /// GUARD 3 would refuse it anyway; this refuses it one step earlier and without touching a
    /// claim.</item>
    /// <item>api key — CORROBORATIVE. Removing it changes nothing (measured); INVERTING it does,
    /// because that skips GUARD 3.</item>
    /// <item>user id — AUTHORITATIVE. This is the comparison the whole class exists for, and it is
    /// the one every removal control reds.</item>
    /// <item>device — AUTHORITATIVE. It is what separates two people on one shared device, and it
    /// is the guard the two credentials reach by DIFFERENT routes.</item>
    /// </list>
    ///
    /// WHERE THE DURABLE AND CAPABILITY PATHS DIVERGE — exactly twice, and nowhere else.
    /// <list type="bullet">
    /// <item>At GUARD 3 the user id comes from <c>capability.Validation.UserId</c> when a
    /// capability was presented, and from the principal's own claim otherwise. Both are server
    /// validated; neither is read from the url.</item>
    /// <item>At GUARD 4 a durable token carries a device claim while a capability carries none, so
    /// a capability's device is resolved through its session by
    /// <c>HlsJobOwnerDevice.Resolve</c> — the SAME function that recorded the job's owner device.
    /// Deriving it differently on the two sides would make a job unreachable by the client that
    /// started it.</item>
    /// </list>
    ///
    /// Everything else on the capability path is decided before this method is reached: STEP 3 of
    /// <c>Decide</c> refuses a capability whose provenance is <c>Refuse</c>, and
    /// <c>CapabilityMatchesTheJob</c> refuses one whose item, media source, play session, scope or
    /// validity is not the job's. This method never re-decides those.
    ///
    /// A COMMENT IS NOT A PROOF. The annotations here record what R4 and R5 MEASURED with hostile
    /// mutations; they are not evidence in themselves. The behavioural evidence is the
    /// <c>HlsOwnershipMatrixTests</c> families and the hostile controls in
    /// <c>ci/hostile-controls/manifest.json</c>; <c>ci/hls-ownership-guard-inventory.sh</c> only
    /// checks that this description has not drifted away from the branches it describes.
    /// </remarks>
    private bool CallerIsTheOwner(HttpContext context, ValidatedPlaybackCapability? capability, Guid ownerUserId, string? ownerDeviceId)
    {
        // GUARD 1 of 4, CORROBORATIVE. An unauthenticated principal reaches GUARD 3 with an empty
        // user id and is refused there too; this is the earlier and cheaper of the two.
        var user = context.User;
        if (user?.Identity is null || !user.Identity.IsAuthenticated)
        {
            return false;
        }

        // GUARD 2 of 4, CORROBORATIVE. An api key resolves to no user, so it cannot be anybody's
        // owner.
        //
        // MEASURED, AND NOT WHAT THIS COMMENT USED TO CLAIM (#153-LTV-R4 finding F4, repaired in
        // R5). It used to say that removing this guard "is a visible change rather than a silently
        // redundant one". That is false: deleting these four lines leaves the whole
        // LiveTvSegmentOwnership suite at 69/69 green, because an api-key principal reaches
        // GUARD 3 with `callerUserId` empty and is refused there instead. The guard is defence in
        // depth and stays; the claim about it does not. What IS load-bearing is the guard's
        // DIRECTION: the candidate's own hostile control `r3-accept-api-key-without-user` inverts
        // it to `return true` and is correctly caught, because that skips GUARD 3 entirely.
        if (user.GetIsApiKey())
        {
            return false;
        }

        // GUARD 3 of 4, AUTHORITATIVE — the first divergence between the two credentials. On the
        // capability path the user comes from the validation result rather than from a claim the
        // request carries. Both hostile controls that weaken this comparison are red.
        var callerUserId = capability?.Validation.UserId ?? user.GetUserId();
        if (callerUserId.IsEmpty() || !callerUserId.Equals(ownerUserId))
        {
            return false;
        }

        // GUARD 4 of 4, AUTHORITATIVE — the second and last divergence. The device, resolved by the
        // SAME function that recorded the job's. A durable token has a device claim; a capability
        // has none and is resolved through its session. Deriving it differently on the two sides
        // would make a job unreachable by the client that started it.
        var callerDeviceId = HlsJobOwnerDevice.Resolve(
            capability is null ? user.GetDeviceId() : null,
            capability?.Validation.SessionId,
            _sessionManager);

        return !string.IsNullOrEmpty(callerDeviceId)
               && string.Equals(callerDeviceId, ownerDeviceId, StringComparison.Ordinal);
    }

    /// <summary>
    /// The capability's own bindings, all of which have to be the job's. The user, the device and
    /// the session are NOT compared here: those are identity, and identity is compared once, in
    /// <see cref="CallerIsTheOwner"/>, for both credentials.
    /// </summary>
    private static bool CapabilityMatchesTheJob(ValidatedPlaybackCapability capability, HlsSegmentBinding binding)
    {
        // Defence in depth: the feature is written on the accepted branch only.
        if (!capability.Validation.IsValid)
        {
            return false;
        }

        // Scope. A capability minted for subtitles or fonts is not a media capability.
        if (capability.Scope != PlaybackCapabilityScope.Media)
        {
            return false;
        }

        // User, from the validation result rather than from any claim the request carries.
        if (!capability.Validation.UserId.Equals(binding.UserId))
        {
            return false;
        }

        // Item. A capability bound to no item cannot stand in for one bound to the job's.
        if (capability.ItemId is not { } boundItem || !boundItem.Equals(binding.ItemId))
        {
            return false;
        }

        // Media source, compared unconditionally — including null against non-null, which is the
        // item-only downgrade the mission forbids.
        if (!string.Equals(capability.MediaSourceId, binding.MediaSourceId, StringComparison.Ordinal))
        {
            return false;
        }

        // Play session, from the validation result rather than from the url. LTV-R0 minted a
        // capability under a play session the server had never issued and reached a segment with
        // it: 200, 387 468 bytes.
        var playSessionsAgree = string.IsNullOrEmpty(capability.PlaySessionId)
            ? string.IsNullOrEmpty(binding.PlaySessionId)
            : string.Equals(capability.PlaySessionId, binding.PlaySessionId, StringComparison.Ordinal);

        if (!playSessionsAgree)
        {
            return false;
        }

        return true;
    }
}
