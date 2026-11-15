using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Threading.Tasks;
using Tesserafin.Api.Constants;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Controller.Session;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.PlaybackCredentials;

/// <summary>
/// Request-level evidence that a consumed WebSocket ticket is redeemed against LIVE state, and that
/// the identity it produces is the same identity a durable token produces (#153-A0-R3).
/// </summary>
/// <remarks>
/// WHAT R2 LEFT OPEN. R2 proved a ticket authenticates an upgrade, is single-use, and is never
/// rescued by a durable token. It did not prove what the upgrade does with the binding the ticket
/// carries. The frozen candidate resolves the bound session with
/// <c>Sessions.FirstOrDefault(...)</c> and then reads its client, version and device name through
/// <c>?.</c>, so a ticket naming a session that is no longer live produces an authenticated
/// connection carrying three empty strings — and an empty client and device id are a DIFFERENT
/// session key, so the socket attaches to a session the ticket was never issued for. The same
/// shape holds for a user id that no longer resolves.
///
/// HOW ACCEPTANCE IS OBSERVED, AND WHY NOT THE WATCHLIST. R2's discriminator was
/// <c>SessionWebSocketListener</c>'s watchlist. That instrument cannot grade R3's cases: the
/// watchlist is filled inside <c>KeepAliveWebSocket</c>, downstream of
/// <c>RequestHelpers.GetSession</c>, so a socket that IS accepted and then dies resolving its
/// session leaves the watchlist untouched and is indistinguishable from a refusal. Every hostile
/// control this file names produces exactly that shape. <see cref="UpgradeRecorder"/> is therefore
/// the load-bearing instrument here — it records inside the same listener loop, before anything can
/// throw — and the watchlist is asserted alongside it rather than instead of it.
/// </remarks>
[Collection(WebSocketUpgradeSuite.Name)]
public sealed class WebSocketUpgradeIdentityTests
{
    private const int TestTimeoutMs = 60_000;

    /// <summary>
    /// The nine claims <c>CustomAuthenticationHandler</c> issues, which is the set a
    /// ticket-authenticated principal must match exactly.
    /// </summary>
    private static readonly string[] _expectedClaimTypes =
    [
        ClaimTypes.Name,
        ClaimTypes.Role,
        InternalClaimTypes.UserId,
        InternalClaimTypes.DeviceId,
        InternalClaimTypes.Device,
        InternalClaimTypes.Client,
        InternalClaimTypes.Version,
        InternalClaimTypes.Token,
        InternalClaimTypes.IsApiKey
    ];

    private static readonly TimeSpan _settle = TimeSpan.FromSeconds(10);

    private readonly WebSocketUpgradeFixture _fixture;

    public WebSocketUpgradeIdentityTests(WebSocketUpgradeFixture fixture)
    {
        _fixture = fixture;
    }

    // -------------------------------------------------------------------------------------
    // 1-3. Redemption against live state.
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// A consumed ticket naming a session that is not in <c>ISessionManager.Sessions</c> is refused
    /// before <c>AcceptWebSocketAsync</c>.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_ticket_naming_a_session_that_is_not_live_is_refused_before_acceptance()
    {
        var absent = "r3-absent-session-" + Guid.NewGuid().ToString("N");
        Assert.DoesNotContain(_fixture.Sessions(), session => string.Equals(session.Id, absent, StringComparison.Ordinal));

        var ticket = _fixture.MintTicketDirectly(
            _fixture.UserId,
            absent,
            WebSocketUpgradeFixture.PrimaryDeviceId);

        await AssertRefusedBeforeAcceptanceAsync(
            () => _fixture.ConnectWithTicketAsync(ticket, TestContext.Current.CancellationToken),
            "a ticket naming a session that is not live");
    }

    /// <summary>
    /// A ticket whose user no longer resolves is refused before acceptance, even though its session
    /// is still live and every binding on it still agrees.
    /// </summary>
    /// <remarks>
    /// THE CONSTRUCTION, AND WHY IT IS NOT AN ARTIFICIAL ONE. <c>DELETE /Users/{id}</c> calls
    /// <c>RevokeUserTokens</c> first, which logs out every device of that user and therefore ends
    /// its sessions and revokes its tickets — so driving the deletion over HTTP would tear the
    /// session down too and this case would silently degenerate into the session case above,
    /// leaving the "missing-user refusal removed" control inert. <c>IUserManager.DeleteUserAsync</c>
    /// is the component that actually removes the row; it publishes <c>UserDeletedEventArgs</c> and
    /// touches no session state, and no consumer of that event touches session state either.
    /// Calling it directly reproduces the window the guard exists for: the user is gone, the
    /// in-memory session is not, and the ticket minted a moment earlier is still live.
    ///
    /// The precondition is asserted rather than assumed. If a future change makes user deletion end
    /// the session, this test fails on the assertion with the reason attached instead of quietly
    /// re-proving a different guard.
    /// </remarks>
    /// <returns>A task.</returns>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_ticket_whose_user_no_longer_resolves_is_refused_before_acceptance()
    {
        var deviceId = "r3-vanishing-user-" + Guid.NewGuid().ToString("N");
        var (userId, _) = await _fixture.CreateUserSessionAsync(deviceId, TestContext.Current.CancellationToken);

        var session = _fixture.SessionForDevice(deviceId);
        Assert.NotNull(session);
        Assert.Equal(userId, session!.UserId);

        var ticket = _fixture.MintTicketDirectly(userId, session.Id, deviceId);

        await _fixture.Service<IUserManager>().DeleteUserAsync(userId);

        // The two halves of the state this case is about, both asserted.
        Assert.Null(_fixture.Service<IUserManager>().GetUserById(userId));
        var surviving = _fixture.SessionForDevice(deviceId);
        Assert.True(
            surviving is not null && string.Equals(surviving.Id, session.Id, StringComparison.Ordinal),
            "deleting the user also ended its session, so this case can no longer isolate the missing-user guard");

        await AssertRefusedBeforeAcceptanceAsync(
            () => _fixture.ConnectWithTicketAsync(ticket, TestContext.Current.CancellationToken, deviceId: deviceId),
            "a ticket whose user no longer resolves");
    }

    /// <summary>
    /// A ticket whose own bindings disagree with the live session it names is refused, in every
    /// direction the binding can disagree.
    /// </summary>
    /// <param name="kind">Which field is made to disagree.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// Both users in the <c>user</c> case resolve, and both sessions in the <c>session</c> case are
    /// live, so none of these can be caught by the two guards above. That is deliberate: it is what
    /// makes the "device/session comparison removed" control fail here and nowhere else.
    /// </remarks>
    [Theory(Timeout = TestTimeoutMs)]
    [InlineData("user")]
    [InlineData("device")]
    [InlineData("session")]
    public async Task A_ticket_whose_bindings_disagree_with_its_live_session_is_refused(string kind)
    {
        var ownDevice = "r3-mismatch-own-" + Guid.NewGuid().ToString("N");
        var otherDevice = "r3-mismatch-other-" + Guid.NewGuid().ToString("N");

        var ownToken = await _fixture.AuthenticateAsync(ownDevice, TestContext.Current.CancellationToken);
        Assert.NotEmpty(ownToken);
        var ownSession = _fixture.SessionForDevice(ownDevice);
        Assert.NotNull(ownSession);

        var (otherUserId, _) = await _fixture.CreateUserSessionAsync(otherDevice, TestContext.Current.CancellationToken);
        var otherSession = _fixture.SessionForDevice(otherDevice);
        Assert.NotNull(otherSession);

        var ticket = kind switch
        {
            // A different, fully resolvable user. The user guard cannot catch this one.
            "user" => _fixture.MintTicketDirectly(otherUserId, ownSession!.Id, ownDevice),

            // The right session, a device it does not own.
            "device" => _fixture.MintTicketDirectly(_fixture.UserId, ownSession!.Id, otherDevice),

            // A different, live session. The session guard cannot catch this one either.
            _ => _fixture.MintTicketDirectly(_fixture.UserId, otherSession!.Id, ownDevice),
        };

        await AssertRefusedBeforeAcceptanceAsync(
            () => _fixture.ConnectWithTicketAsync(ticket, TestContext.Current.CancellationToken, deviceId: ownDevice),
            $"a ticket whose {kind} binding disagrees with its live session");
    }

    // -------------------------------------------------------------------------------------
    // 4-5. What a valid ticket attaches to.
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// A valid ticket attaches to the EXACT session it was minted from: same id, same device id and
    /// name, same client and version, same user — and no session is created.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_valid_ticket_attaches_to_the_exact_pre_existing_session()
    {
        var deviceId = "r3-exact-" + Guid.NewGuid().ToString("N");
        var token = await _fixture.AuthenticateAsync(deviceId, TestContext.Current.CancellationToken);
        var before = _fixture.SessionForDevice(deviceId);
        Assert.NotNull(before);

        var ticket = await _fixture.MintTicketAsync(deviceId, token, TestContext.Current.CancellationToken);

        var countBefore = _fixture.Sessions().Count;
        _fixture.Recorder().Clear();

        var socket = await _fixture.ConnectWithTicketAsync(ticket.Value, TestContext.Current.CancellationToken, deviceId: deviceId);
        try
        {
            var accepted = await WaitForAcceptedAsync(1);
            var upgrade = Assert.Single(accepted);
            var info = upgrade.AuthorizationInfo;
            Assert.NotNull(info);

            Assert.Equal(before!.UserId, info!.User?.Id);
            Assert.Equal(before.DeviceId, info.DeviceId);
            Assert.Equal(before.DeviceName, info.Device);
            Assert.Equal(before.Client, info.Client);
            Assert.Equal(before.ApplicationVersion, info.Version);

            // The claims are what the session listener reads, so they are asserted too rather than
            // trusting that the AuthorizationInfo above is what reached the principal.
            Assert.Equal(before.DeviceId, ValueOf(upgrade, InternalClaimTypes.DeviceId));
            Assert.Equal(before.DeviceName, ValueOf(upgrade, InternalClaimTypes.Device));
            Assert.Equal(before.Client, ValueOf(upgrade, InternalClaimTypes.Client));
            Assert.Equal(before.ApplicationVersion, ValueOf(upgrade, InternalClaimTypes.Version));
            Assert.Equal(
                before.UserId.ToString("N", CultureInfo.InvariantCulture),
                ValueOf(upgrade, InternalClaimTypes.UserId));

            // The session listener resolves through LogSessionActivity, which CREATES a session
            // when the key it is handed does not match one. Attaching to the wrong identity is
            // therefore visible as an extra session, not as an error.
            var watched = await _fixture.WaitForWatchlistAsync(1, _settle, TestContext.Current.CancellationToken);
            Assert.Single(watched);

            var after = _fixture.SessionForDevice(deviceId);
            Assert.NotNull(after);
            Assert.Equal(before.Id, after!.Id);
            Assert.Equal(countBefore, _fixture.Sessions().Count);
        }
        finally
        {
            await _fixture.CloseAndDrainAsync(socket, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Request-supplied device, client and version cannot override the ticket's bound session.
    /// </summary>
    /// <remarks>
    /// R2 proved the device id alone. A session is keyed on client AND device id, and carries a
    /// version alongside, so a principal that took even one of those three from the request would
    /// attach the socket to a different session. All three move here, together, and away from the
    /// values the ticket's session holds.
    /// </remarks>
    /// <returns>A task.</returns>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Request_supplied_device_client_and_version_cannot_override_the_tickets_session()
    {
        var deviceId = "r3-bound-" + Guid.NewGuid().ToString("N");
        var token = await _fixture.AuthenticateAsync(deviceId, TestContext.Current.CancellationToken);
        var bound = _fixture.SessionForDevice(deviceId);
        Assert.NotNull(bound);

        var ticket = await _fixture.MintTicketAsync(deviceId, token, TestContext.Current.CancellationToken);

        var lyingDevice = "r3-lying-" + Guid.NewGuid().ToString("N");
        var header = WebSocketUpgradeFixture.AuthorizationHeader(
            lyingDevice,
            _fixture.DurableToken,
            client: "R3 Impostor Client",
            device: "r3-impostor-device-name",
            version: "99.99.99");

        // The request's own claims must differ from the session's, or the assertions below pass for
        // the wrong reason.
        Assert.NotEqual("R3 Impostor Client", bound!.Client);
        Assert.NotEqual("99.99.99", bound.ApplicationVersion);
        Assert.NotEqual("r3-impostor-device-name", bound.DeviceName);

        var countBefore = _fixture.Sessions().Count;
        _fixture.Recorder().Clear();

        var socket = await _fixture.ConnectWithHeaderAsync(
            $"webSocketTicket={Uri.EscapeDataString(ticket.Value)}",
            header,
            TestContext.Current.CancellationToken);

        try
        {
            var accepted = await WaitForAcceptedAsync(1);
            var upgrade = Assert.Single(accepted);

            Assert.Equal(bound.DeviceId, ValueOf(upgrade, InternalClaimTypes.DeviceId));
            Assert.Equal(bound.DeviceName, ValueOf(upgrade, InternalClaimTypes.Device));
            Assert.Equal(bound.Client, ValueOf(upgrade, InternalClaimTypes.Client));
            Assert.Equal(bound.ApplicationVersion, ValueOf(upgrade, InternalClaimTypes.Version));
            Assert.Equal(
                bound.UserId.ToString("N", CultureInfo.InvariantCulture),
                ValueOf(upgrade, InternalClaimTypes.UserId));

            await _fixture.WaitForWatchlistAsync(1, _settle, TestContext.Current.CancellationToken);
            var after = _fixture.SessionForDevice(deviceId);
            Assert.NotNull(after);
            Assert.Equal(bound.Id, after!.Id);
            Assert.Null(_fixture.SessionForDevice(lyingDevice));
            Assert.Equal(countBefore, _fixture.Sessions().Count);
        }
        finally
        {
            await _fixture.CloseAndDrainAsync(socket, TestContext.Current.CancellationToken);
        }
    }

    // -------------------------------------------------------------------------------------
    // 6. No fallback after consumption, and the ticket stays spent.
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// A failure that happens AFTER the ticket was consumed never falls back to a durable token the
    /// client also sent, and the ticket stays consumed.
    /// </summary>
    /// <remarks>
    /// R2's no-fallback theory covers failures that happen at consumption — unknown, expired,
    /// replayed. This covers the class R3 introduces, where consumption SUCCEEDED and the refusal
    /// comes from the live-state check after it. That is a different branch of the same method, and
    /// a repair that returned an unauthenticated result from the consumption branch while letting
    /// the new branch fall through to <c>_authService.Authenticate</c> would pass R2's theory and
    /// fail here.
    ///
    /// "Still consumed" is asserted against the store directly rather than by replaying the value
    /// through the upgrade: a second upgrade would be refused by the live-state check whether or not
    /// the ticket survived, so it could not tell the two apart.
    /// </remarks>
    /// <param name="kind">Which post-consumption failure to construct.</param>
    /// <returns>A task.</returns>
    [Theory(Timeout = TestTimeoutMs)]
    [InlineData("absent-session")]
    [InlineData("mismatched-device")]
    public async Task A_post_consumption_failure_never_falls_back_and_leaves_the_ticket_consumed(string kind)
    {
        var deviceId = "r3-nofallback-" + Guid.NewGuid().ToString("N");
        var token = await _fixture.AuthenticateAsync(deviceId, TestContext.Current.CancellationToken);
        Assert.NotEmpty(token);
        var session = _fixture.SessionForDevice(deviceId);
        Assert.NotNull(session);

        var ticket = kind == "absent-session"
            ? _fixture.MintTicketDirectly(_fixture.UserId, "r3-gone-" + Guid.NewGuid().ToString("N"), deviceId)
            : _fixture.MintTicketDirectly(_fixture.UserId, session!.Id, "r3-not-this-device-" + Guid.NewGuid().ToString("N"));

        await AssertRefusedBeforeAcceptanceAsync(
            () => _fixture.ConnectWithTicketAsync(ticket, TestContext.Current.CancellationToken, durableToken: _fixture.DurableToken),
            $"a {kind} ticket presented alongside a valid durable token");

        var second = _fixture.Service<IPlaybackCredentialService>().ConsumeWebSocketTicket(ticket);
        Assert.False(second.IsValid, "the refused upgrade left the ticket redeemable");
    }

    // -------------------------------------------------------------------------------------
    // 7. Principal parity.
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// For one and the same session, the ticket principal and the durable-token principal carry
    /// identical claim names, multiplicities and values — with exactly three declared exceptions.
    /// </summary>
    /// <remarks>
    /// The exceptions are the authentication scheme, the deliberately empty <c>Token</c>, and
    /// <c>IsApiKey</c>, which a consumed ticket always reports as false. Every other claim must
    /// agree, and both principals must carry exactly the nine claims the handler issues — asserted
    /// as an exact set with multiplicity, not as a difference, because a set difference is also
    /// satisfied by two principals that are both empty or both missing the same claim.
    /// </remarks>
    /// <returns>A task.</returns>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task The_ticket_and_durable_token_principals_agree_on_every_claim_but_the_declared_exceptions()
    {
        var deviceId = "r3-parity-" + Guid.NewGuid().ToString("N");
        var token = await _fixture.AuthenticateAsync(deviceId, TestContext.Current.CancellationToken);

        _fixture.Recorder().Clear();
        var durableSocket = await _fixture.ConnectWithDurableTokenAsync(token, TestContext.Current.CancellationToken, deviceId);
        AcceptedUpgrade durable;
        try
        {
            durable = Assert.Single(await WaitForAcceptedAsync(1));
        }
        finally
        {
            await _fixture.CloseAndDrainAsync(durableSocket, TestContext.Current.CancellationToken);
        }

        // The same session, reached the other way. Authenticating again would make a second
        // session and the comparison would be between two different identities.
        var reused = await _fixture.AuthenticateAsync(deviceId, TestContext.Current.CancellationToken);
        var ticket = await _fixture.MintTicketAsync(deviceId, reused, TestContext.Current.CancellationToken);

        _fixture.Recorder().Clear();
        var ticketSocket = await _fixture.ConnectWithTicketAsync(ticket.Value, TestContext.Current.CancellationToken, deviceId: deviceId);
        AcceptedUpgrade viaTicket;
        try
        {
            viaTicket = Assert.Single(await WaitForAcceptedAsync(1));
        }
        finally
        {
            await _fixture.CloseAndDrainAsync(ticketSocket, TestContext.Current.CancellationToken);
        }

        AssertExactClaimShape(durable, "the durable-token principal");
        AssertExactClaimShape(viaTicket, "the ticket principal");

        // Exception 1: the scheme.
        Assert.Equal(AuthenticationSchemes.CustomAuthentication, durable.AuthenticationType);
        Assert.Equal("webSocketTicket", viaTicket.AuthenticationType);

        // Exception 2: the token is deliberately empty for a consumed ticket, and deliberately
        // present for a durable one. Stated in both directions so that emptying the durable claim
        // does not accidentally satisfy this.
        Assert.NotEmpty(ValueOf(durable, InternalClaimTypes.Token));
        Assert.Equal(string.Empty, ValueOf(viaTicket, InternalClaimTypes.Token));

        // Exception 3: IsApiKey is deliberately false for a consumed ticket.
        Assert.Equal(false.ToString(CultureInfo.InvariantCulture), ValueOf(viaTicket, InternalClaimTypes.IsApiKey));

        foreach (var type in _expectedClaimTypes.Where(t => !string.Equals(t, InternalClaimTypes.Token, StringComparison.Ordinal)))
        {
            Assert.Equal(ValueOf(durable, type), ValueOf(viaTicket, type));
        }

        // Not vacuous: the values being compared are real.
        Assert.NotEmpty(ValueOf(viaTicket, InternalClaimTypes.UserId));
        Assert.NotEmpty(ValueOf(viaTicket, InternalClaimTypes.Client));
        Assert.NotEmpty(ValueOf(viaTicket, InternalClaimTypes.Version));
        Assert.NotEmpty(ValueOf(viaTicket, InternalClaimTypes.DeviceId));
        Assert.NotEmpty(ValueOf(viaTicket, InternalClaimTypes.Device));
        Assert.NotEmpty(ValueOf(viaTicket, ClaimTypes.Name));
        Assert.NotEmpty(ValueOf(viaTicket, ClaimTypes.Role));
    }

    // -------------------------------------------------------------------------------------

    private static void AssertExactClaimShape(AcceptedUpgrade upgrade, string what)
    {
        var byType = upgrade.Claims
            .GroupBy(claim => claim.Type, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        Assert.Equal(_expectedClaimTypes.Length, byType.Count);
        foreach (var type in _expectedClaimTypes)
        {
            Assert.True(byType.TryGetValue(type, out var count), $"{what} is missing {type}");
            Assert.True(count == 1, $"{what} carries {type} {count} times");
        }
    }

    private static string ValueOf(AcceptedUpgrade upgrade, string type)
        => upgrade.Claims.Single(claim => string.Equals(claim.Type, type, StringComparison.Ordinal)).Value;

    private async Task<IReadOnlyList<AcceptedUpgrade>> WaitForAcceptedAsync(int expected)
    {
        var deadline = DateTime.UtcNow + _settle;
        while (true)
        {
            var snapshot = _fixture.Recorder().Accepted;
            if (snapshot.Count >= expected || DateTime.UtcNow >= deadline)
            {
                return snapshot;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Asserts a refusal that happened before <c>AcceptWebSocketAsync</c> returned.
    /// </summary>
    /// <remarks>
    /// Three independent statements, all required. The recorder is the one that grades R3's
    /// controls: it observes acceptance regardless of what the session listener does afterwards,
    /// which the watchlist cannot. The watchlist is kept because it is R2's evidence and must not
    /// silently stop holding. The null socket is kept because it is the only one of the three that
    /// also fails when the server accepts and the recorder is somehow not reached.
    /// </remarks>
    private async Task AssertRefusedBeforeAcceptanceAsync(Func<Task<WebSocket>> connect, string what)
    {
        _fixture.Recorder().Clear();
        var watchedBefore = _fixture.Watchlist().Count;

        WebSocket? socket = null;
        try
        {
            socket = await connect();
        }
        catch (Exception)
        {
            // Refused at the handshake, which is the expected shape.
        }

        try
        {
            await Task.Delay(250, TestContext.Current.CancellationToken);

            Assert.True(
                _fixture.Recorder().Accepted.Count == 0,
                $"the upgrade was ACCEPTED for {what}; the refusal happened after AcceptWebSocketAsync, or not at all");

            Assert.True(socket is null, $"the handshake completed for {what}");
            Assert.Equal(watchedBefore, _fixture.Watchlist().Count);
        }
        finally
        {
            if (socket is not null)
            {
                // Cleanup must never become the reported failure. A socket the server accepted and
                // then abandoned is already disposed at its end, so closing it throws — and that
                // exception would replace the assertion above and hide which guard is missing.
                try
                {
                    await _fixture.CloseAndDrainAsync(socket, TestContext.Current.CancellationToken);
                }
                catch (Exception)
                {
                    // The connection is gone, which is the situation the assertion already reported.
                }
            }
        }
    }
}
