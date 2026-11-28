using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Server.Implementations.Security.PlaybackCredentials;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.PlaybackCredentials;

/// <summary>
/// Request-level evidence for the media authorization boundary (#153-A0-R1), one real HTTP request
/// at a time, over every media class #153 names.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS. A0 proved the credential primitives in isolation and the route table by
/// reflection, and neither can answer the question an operator actually has: what does the server
/// return to this request. The structural test keeps passing while a route serves media to nobody
/// in particular, because a route table says nothing about what the action does. This suite makes
/// every claim against a booted server, a real library item, and real bytes on disk.
///
/// WHAT A REFUSAL HAS TO LOOK LIKE. 401 or 403, and a body that is not the fixture's media. Both
/// halves matter: a 200 carrying an error page would be a refusal by status alone, and a 401 that
/// still streamed the file would be a refusal by nothing at all.
/// </remarks>
[Collection(MediaBoundarySuite.Name)]
public sealed class MediaAuthorizationBoundaryTests
{
    private readonly MediaBoundaryFixture _fixture;

    public MediaAuthorizationBoundaryTests(MediaBoundaryFixture fixture)
    {
        _fixture = fixture;
    }

    public static IEnumerable<object[]> AllRoutes()
        => MediaRouteCatalog
            .For(Guid.Empty, string.Empty, MediaBoundaryFixture.SubtitleStreamIndex, MediaRouteSessions.None)
            .Select(route => new object[] { route.Name });

    public static IEnumerable<object[]> ItemBoundRoutes()
        => MediaRouteCatalog
            .For(Guid.Empty, string.Empty, MediaBoundaryFixture.SubtitleStreamIndex, MediaRouteSessions.None)
            .Where(route => route.ItemBound)
            .Select(route => new object[] { route.Name });

    public static IEnumerable<object[]> PlaySessionBoundRoutes()
        => MediaRouteCatalog
            .For(Guid.Empty, string.Empty, MediaBoundaryFixture.SubtitleStreamIndex, MediaRouteSessions.None)
            .Where(route => route.PlaySessionBound)
            .Select(route => new object[] { route.Name });

    public static IEnumerable<object[]> MediaSourceBoundRoutes()
        => MediaRouteCatalog
            .For(Guid.Empty, string.Empty, MediaBoundaryFixture.SubtitleStreamIndex, MediaRouteSessions.None)
            .Where(route => route.MediaSourceBound)
            .Select(route => new object[] { route.Name });

    /// <summary>
    /// Every media route that names its media source in the QUERY rather than in the path. Only
    /// there does the caller get to choose the demand, which is the property
    /// <see cref="ADifferentMediaSourceNamedInTheRequest_IsRefusedForTheSameItem"/> measures. A
    /// path-bound route takes its demand from the route itself and changing it changes which
    /// resource is addressed, not which demand is made.
    /// </summary>
    /// <returns>The route names.</returns>
    public static IEnumerable<object[]> MediaSourceInQueryRoutes()
        => MediaRouteCatalog
            .For(Guid.Empty, string.Empty, MediaBoundaryFixture.SubtitleStreamIndex, MediaRouteSessions.None)
            .Where(route => route.MediaSourceBound
                && route.Scope == PlaybackCapabilityScope.Media
                && route.Path.Contains("mediaSourceId=", StringComparison.Ordinal))
            .Select(route => new object[] { route.Name });

    // ---------------------------------------------------------------------------------------
    // 1. No credential at all.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The headline claim of R1. Before the repair, five of these routes answered 200 with the
    /// fixture's own bytes and two more answered 200 with the fixture's subtitle text, to a client
    /// that presented nothing.
    /// </summary>
    /// <param name="routeName">The route under test.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task No_credential_is_refused_and_never_returns_media(string routeName)
    {
        var route = Route(routeName);
        using var client = _fixture.AnonymousClient();

        var (status, body) = await MediaBoundaryFixture.SendAsync(client, route.Method, route.Path);

        AssertRefused(status, body, route, "a request that presented no credential at all");
    }

    // ---------------------------------------------------------------------------------------
    // 2. The durable token still works, in a header and in the query string.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A0's compatibility promise, kept: the legacy request still reaches its action.
    /// </summary>
    /// <param name="routeName">The route under test.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task The_durable_token_in_a_header_still_reaches_the_action(string routeName)
    {
        var route = Route(routeName);
        using var client = _fixture.DurableHeaderClient();

        var (status, _) = await MediaBoundaryFixture.SendAsync(client, route.Method, route.Path);

        AssertReachedTheAction(status, route, "the durable token in the Authorization header");
    }

    /// <summary>
    /// The transport #153 exists to replace is still accepted, because A0-R1 does not remove it —
    /// it stops it being the only thing standing between an anonymous caller and the file.
    /// </summary>
    /// <param name="routeName">The route under test.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task The_durable_token_in_the_query_string_still_reaches_the_action(string routeName)
    {
        var route = Route(routeName);
        using var client = _fixture.AnonymousClient();

        var (status, _) = await MediaBoundaryFixture.SendAsync(
            client,
            route.Method,
            route.WithQuery("ApiKey", _fixture.DurableToken));

        AssertReachedTheAction(status, route, "the durable token in the ApiKey query parameter");
    }

    // ---------------------------------------------------------------------------------------
    // 3. A correctly scoped capability.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The capability reaches the real action, and where the fixture can produce bytes it returns
    /// exactly the fixture's bytes.
    /// </summary>
    /// <param name="routeName">The route under test.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task A_correctly_scoped_capability_reaches_the_action(string routeName)
    {
        var route = Route(routeName);
        var capability = await _fixture.MintForAsync(route);

        using var client = _fixture.AnonymousClient();
        var (status, body) = await MediaBoundaryFixture.SendAsync(
            client,
            route.Method,
            route.WithQuery("playbackCapability", Uri.EscapeDataString(capability.Value)));

        AssertReachedTheAction(status, route, "a correctly scoped capability");

        if (route.Evidence == MediaRouteEvidence.Bytes && route.Scope == PlaybackCapabilityScope.Media)
        {
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(_fixture.GetMediaBytes(), body);
        }
    }

    // ---------------------------------------------------------------------------------------
    // 4. Expiry and revocation.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Expiry is decided by the clock the validator reads, not by the clock the minter read.
    /// Nothing here sleeps.
    /// </summary>
    /// <param name="routeName">The route under test.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task An_expired_capability_is_refused(string routeName)
    {
        var route = Route(routeName);
        var capability = await _fixture.MintForAsync(route);
        var path = route.WithQuery("playbackCapability", Uri.EscapeDataString(capability.Value));

        // The negative direction first: one tick before expiry it still works. Without this, a
        // validator that refused everything would pass the assertion below.
        using (var early = _fixture.AnonymousClient())
        {
            var (acceptedStatus, _) = await MediaBoundaryFixture.SendAsync(early, route.Method, path);
            AssertReachedTheAction(acceptedStatus, route, "a capability one tick inside its lifetime");
        }

        _fixture.Factory.Clock.Advance(PlaybackCredentialService.CapabilityLifetime + TimeSpan.FromSeconds(1));

        using var client = _fixture.AnonymousClient();
        var (status, body) = await MediaBoundaryFixture.SendAsync(client, route.Method, path);

        AssertRefused(status, body, route, "an expired capability");
    }

    /// <summary>
    /// Revocation reaches the capability through the session that owns it. The session revoked here
    /// is a second device, so the rest of the suite keeps its own token.
    /// </summary>
    /// <param name="routeName">The route under test.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task A_revoked_capability_is_refused(string routeName)
    {
        var route = Route(routeName);
        var deviceId = "r1-revocation-" + Guid.NewGuid().ToString("N");
        var token = await _fixture.AuthenticateAsync(deviceId);

        using var owner = _fixture.ClientFor(deviceId, token);
        var capability = await MediaBoundaryFixture.MintWithAsync(
            owner,
            [route.Scope],
            route.ItemBound ? _fixture.ItemId : null,
            route.MediaSourceBound ? _fixture.MediaSourceId : null);

        using (var logout = await owner.PostAsync("/Sessions/Logout", content: null, TestContext.Current.CancellationToken))
        {
            Assert.True(logout.IsSuccessStatusCode, $"Logout answered {(int)logout.StatusCode}.");
        }

        using var client = _fixture.AnonymousClient();
        var (status, body) = await MediaBoundaryFixture.SendAsync(
            client,
            route.Method,
            route.WithQuery("playbackCapability", Uri.EscapeDataString(capability.Value)));

        AssertRefused(status, body, route, "a capability whose owning session was logged out");
    }

    // ---------------------------------------------------------------------------------------
    // 5. No fallback.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A bad capability is not repaired by a good durable token. If it were, a client could present
    /// both and the capability's scope, item, expiry and revocation would all be decoration.
    /// </summary>
    /// <param name="routeName">The route under test.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task An_invalid_capability_is_not_rescued_by_a_valid_durable_token(string routeName)
    {
        var route = Route(routeName);
        using var client = _fixture.DurableHeaderClient();

        var (status, body) = await MediaBoundaryFixture.SendAsync(
            client,
            route.Method,
            route.WithQuery("playbackCapability", "not-a-capability"));

        AssertRefused(status, body, route, "an invalid capability presented alongside a valid durable token");
    }

    // ---------------------------------------------------------------------------------------
    // 6. Binding.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A capability minted for another item does not reach this one.
    /// </summary>
    /// <param name="routeName">The route under test.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(ItemBoundRoutes))]
    public async Task A_capability_bound_to_another_item_is_refused(string routeName)
    {
        var route = Route(routeName);
        var capability = await _fixture.MintAsync(
            [route.Scope],
            _fixture.OtherItemId,
            route.MediaSourceBound ? _fixture.OtherMediaSourceId : null,
            playSessionId: route.PlaySessionId ?? MediaBoundaryFixture.DefaultPlaySession);

        using var client = _fixture.AnonymousClient();
        var (status, body) = await MediaBoundaryFixture.SendAsync(
            client,
            route.Method,
            route.WithQuery("playbackCapability", Uri.EscapeDataString(capability.Value)));

        AssertRefused(status, body, route, "a capability bound to a different item");
    }

    /// <summary>
    /// A capability minted for a different playback entirely — another item and that item's own
    /// media source — does not reach this one.
    /// </summary>
    /// <remarks>
    /// The sharper case, same item and a foreign media source, cannot be built through HTTP any
    /// more: item 3's minting checks refuse a media source that does not belong to the item it is
    /// paired with, so no such capability can exist. That leaves the delivery-side media-source
    /// rule without a request-level fixture, which is why the four exact-binding permutations are
    /// asserted directly against the validator in <c>PlaybackCredentialServiceTests</c>. Two of
    /// them do survive at this level and are asserted here:
    /// <see cref="A_media_source_bound_capability_is_refused_where_the_route_names_no_media_source"/>
    /// and <see cref="An_unbound_capability_is_refused_where_the_route_names_a_media_source"/>.
    /// </remarks>
    /// <param name="routeName">The route under test.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(MediaSourceBoundRoutes))]
    public async Task A_capability_minted_for_a_different_playback_is_refused(string routeName)
    {
        var route = Route(routeName);
        var capability = await _fixture.MintAsync([route.Scope], _fixture.OtherItemId, _fixture.OtherMediaSourceId, playSessionId: route.PlaySessionId ?? MediaBoundaryFixture.DefaultPlaySession);

        using var client = _fixture.AnonymousClient();
        var (status, body) = await MediaBoundaryFixture.SendAsync(
            client,
            route.Method,
            route.WithQuery("playbackCapability", Uri.EscapeDataString(capability.Value)));

        AssertRefused(status, body, route, "a capability minted for a different item and media source");
    }

    /// <summary>
    /// #153-LTV-S1. The theory below must never run over an empty set. The session routes name
    /// their media source in the query; if they ever stop, the property loses its subject and this
    /// fails rather than passing vacuously.
    /// </summary>
    [Fact]
    public void TheMediaSourceInQueryRoutes_AreNotEmpty()
    {
        var names = MediaSourceInQueryRoutes().Select(row => (string)row[0]).ToList();

        Assert.NotEmpty(names);
        Assert.Contains("session file", names);
    }

    /// <summary>
    /// #153-LTV-S1. A route that reads its media-source demand from the request lets the CALLER
    /// choose the demand, so "it reads the query" and "it ignores the media source" look identical
    /// unless a mismatch is measured. Same item, same live capability, a media source the
    /// capability is not bound to: refused.
    /// </summary>
    /// <param name="routeName">The route under test.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(MediaSourceInQueryRoutes))]
    public async Task ADifferentMediaSourceNamedInTheRequest_IsRefusedForTheSameItem(string routeName)
    {
        var route = Route(routeName);

        var capability = await _fixture.MintAsync([route.Scope], _fixture.ItemId, _fixture.MediaSourceId, playSessionId: route.PlaySessionId ?? MediaBoundaryFixture.DefaultPlaySession);

        // The media source is REPLACED, never appended: two `mediaSourceId` values would arrive as
        // one comma-joined string, which refuses for a reason that has nothing to do with the
        // binding under test.
        var path = route
            .WithQuery("playbackCapability", Uri.EscapeDataString(capability.Value))
            .Replace("mediaSourceId=" + _fixture.MediaSourceId, "mediaSourceId=" + _fixture.OtherMediaSourceId, StringComparison.Ordinal);

        Assert.Contains("mediaSourceId=" + _fixture.OtherMediaSourceId, path, StringComparison.Ordinal);
        Assert.DoesNotContain("mediaSourceId=" + _fixture.MediaSourceId, path, StringComparison.Ordinal);

        using var client = _fixture.AnonymousClient();
        var (status, body) = await MediaBoundaryFixture.SendAsync(client, route.Method, path);

        AssertRefused(status, body, route, "a capability bound to one media source while the request names another");
    }

    /// <summary>
    /// The mirror of the above: a capability bound to NO media source, presented on a route that
    /// does name one. The route's demand is specific; a capability that cannot answer it is not
    /// entitled to be treated as if it had.
    /// </summary>
    /// <param name="routeName">The route under test.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(MediaSourceBoundRoutes))]
    public async Task An_unbound_capability_is_refused_where_the_route_names_a_media_source(string routeName)
    {
        var route = Route(routeName);
        if (route.Scope == PlaybackCapabilityScope.Fonts)
        {
            return;
        }

        var capability = await _fixture.MintAsync([route.Scope], _fixture.ItemId, mediaSourceId: null, playSessionId: route.PlaySessionId ?? MediaBoundaryFixture.DefaultPlaySession);

        using var client = _fixture.AnonymousClient();
        var (status, body) = await MediaBoundaryFixture.SendAsync(
            client,
            route.Method,
            route.WithQuery("playbackCapability", Uri.EscapeDataString(capability.Value)));

        AssertRefused(status, body, route, "a capability bound to no media source on a route that names one");
    }

    /// <summary>
    /// Every route that exposes <c>playSessionId</c> compare it. A capability is bound to one
    /// playback; naming a different one in the URL is a claim about somebody else's.
    /// </summary>
    /// <remarks>
    /// The positive half runs first and is not decoration: the demand is only read when the request
    /// carries the parameter, so a wiring bug that never read it at all would pass the refusal
    /// assertion for the wrong reason. Absence is deliberately NOT a refusal — see
    /// <see cref="PlaybackCapabilityDemand"/> — and the third assertion pins that down, because a
    /// later "tighten this up" would silently make an optional query parameter mandatory.
    /// </remarks>
    /// <param name="routeName">The route under test.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(PlaySessionBoundRoutes))]
    public async Task A_capability_bound_to_another_play_session_is_refused(string routeName)
    {
        var route = Route(routeName);

        // A session route names its session's own play session, and a session serves only that
        // one: the capability is minted for it, and the request names it, or another one, or none.
        var owned = route.PlaySessionId ?? "r1-owned-play-session";

        var capability = await _fixture.MintAsync(
            [route.Scope],
            _fixture.ItemId,
            route.MediaSourceBound ? _fixture.MediaSourceId : null,
            owned);

        var withCapability = route.WithQuery("playbackCapability", Uri.EscapeDataString(capability.Value));

        using (var matching = _fixture.AnonymousClient())
        {
            var (status, _) = await MediaBoundaryFixture.SendAsync(
                matching,
                route.Method,
                WithPlaySession(withCapability, owned));

            AssertReachedTheAction(status, route, "a capability naming its own play session");
        }

        using (var absent = _fixture.AnonymousClient())
        {
            var (status, _) = await MediaBoundaryFixture.SendAsync(absent, route.Method, WithPlaySession(withCapability, null));

            AssertReachedTheAction(status, route, "a request that named no play session at all");
        }

        using var client = _fixture.AnonymousClient();
        var (refused, body) = await MediaBoundaryFixture.SendAsync(
            client,
            route.Method,
            WithPlaySession(withCapability, "someone-elses-playback"));

        AssertRefused(refused, body, route, "a capability bound to a different play session");
    }

    /// <summary>
    /// Scope is per route, not per credential. A Fonts capability is the sharpest case: it is
    /// item-less by contract, so if scope were not compared it would reach every media route
    /// without ever naming an item.
    /// </summary>
    /// <param name="routeName">The route under test.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task A_capability_for_the_wrong_scope_is_refused(string routeName)
    {
        var route = Route(routeName);

        // An item-bound substitute, so the capability can be minted with the SAME binding shape the
        // route names and scope is the only thing that differs. Fonts is never the substitute: it
        // is item-less by contract, so a Fonts capability would differ in binding as well as in
        // scope and the refusal would no longer be attributable.
        var wrongScope = route.Scope == PlaybackCapabilityScope.Media
            ? PlaybackCapabilityScope.Subtitles
            : PlaybackCapabilityScope.Media;

        var capability = await _fixture.MintAsync(
            [wrongScope],
            _fixture.ItemId,
            route.MediaSourceBound && route.Scope != PlaybackCapabilityScope.Fonts ? _fixture.MediaSourceId : null,
            playSessionId: route.PlaySessionId ?? MediaBoundaryFixture.DefaultPlaySession);

        using var client = _fixture.AnonymousClient();
        var (status, body) = await MediaBoundaryFixture.SendAsync(
            client,
            route.Method,
            route.WithQuery("playbackCapability", Uri.EscapeDataString(capability.Value)));

        AssertRefused(status, body, route, $"a {wrongScope} capability on a {route.Scope} route");
    }

    // ---------------------------------------------------------------------------------------
    // 7. Not a general-API credential.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A capability is not a credential outside media delivery, on any general endpoint, under any
    /// query key the general authorization path reads.
    /// </summary>
    /// <param name="path">The general endpoint to try.</param>
    /// <param name="key">The query key to smuggle the capability into.</param>
    /// <returns>A task.</returns>
    [Theory]
    [InlineData("/Items", "playbackCapability")]
    [InlineData("/Items", "ApiKey")]
    [InlineData("/Items", "api_key")]
    [InlineData("/Users/Me", "playbackCapability")]
    [InlineData("/Users/Me", "ApiKey")]
    [InlineData("/System/Info", "playbackCapability")]
    [InlineData("/System/Info", "ApiKey")]
    [InlineData("/Sessions", "playbackCapability")]
    [InlineData("/Sessions", "ApiKey")]
    [InlineData("/Library/VirtualFolders", "playbackCapability")]
    public async Task A_capability_is_not_a_credential_on_a_general_endpoint(string path, string key)
    {
        var capability = await _fixture.MintAsync([PlaybackCapabilityScope.Fonts], null, null);

        using var client = _fixture.AnonymousClient();
        var (status, _) = await MediaBoundaryFixture.SendAsync(
            client,
            "GET",
            $"{path}?{key}={Uri.EscapeDataString(capability.Value)}");

        Assert.True(
            status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"GET {path} with the capability in '{key}' answered {(int)status}. A capability must never authenticate a general endpoint.");
    }

    /// <summary>
    /// Names <paramref name="playSessionId"/> as the request's play session, REPLACING the one the
    /// route already names (two values would arrive as one comma-joined string and be refused for
    /// a reason unrelated to the binding), or naming none at all.
    /// </summary>
    private static string WithPlaySession(string path, string? playSessionId)
    {
        var named = System.Text.RegularExpressions.Regex.Replace(path, "[?&]playSessionId=[^&]*", string.Empty);
        if (named.Contains('&', StringComparison.Ordinal) && !named.Contains('?', StringComparison.Ordinal))
        {
            var first = named.IndexOf('&', StringComparison.Ordinal);
            named = string.Concat(named.AsSpan(0, first), "?", named.AsSpan(first + 1));
        }

        return playSessionId is null
            ? named
            : named + (named.Contains('?', StringComparison.Ordinal) ? '&' : '?') + "playSessionId=" + playSessionId;
    }

    private MediaRoute Route(string name)
        => _fixture.Routes.Single(route => string.Equals(route.Name, name, StringComparison.Ordinal));

    private void AssertRefused(HttpStatusCode status, byte[] body, MediaRoute route, string what)
    {
        Assert.True(
            status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"[{route.MediaClass}] {route.Method} {route.Path} answered {(int)status} to {what}. Expected 401 or 403.");

        var media = _fixture.GetMediaBytes();
        Assert.False(
            body.Length == media.Length && body.SequenceEqual(media),
            $"[{route.MediaClass}] {route.Method} {route.Path} returned the fixture's media bytes to {what}.");
    }

    private static void AssertReachedTheAction(HttpStatusCode status, MediaRoute route, string what)
    {
        Assert.False(
            status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"[{route.MediaClass}] {route.Method} {route.Path} answered {(int)status} to {what}. Authorization refused a request it had to admit.");
    }
}
