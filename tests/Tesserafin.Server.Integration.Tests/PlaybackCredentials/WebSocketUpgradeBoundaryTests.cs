using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Server.Implementations.Security.PlaybackCredentials;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.PlaybackCredentials;

/// <summary>
/// Request-level evidence for the WebSocket ticket, through the real upgrade pipeline
/// (#153-A0-R2).
/// </summary>
/// <remarks>
/// Every case here goes through <c>WebSocketHandlerMiddleware</c>, <c>WebSocketManager</c> and a
/// real handshake. <c>PlaybackCredentialServiceTests</c> proves the store; nothing in it can say
/// whether the upgrade path consumes before accepting, whether a refused ticket falls back, or what
/// identity the accepted socket carries.
///
/// A refusal is asserted as "the session listener never saw a connection", not as an exception
/// type: <c>ConnectAsync</c> throws the same <see cref="InvalidOperationException"/> whether the
/// server refused before accepting or accepted and then dropped.
/// </remarks>
[Collection(WebSocketUpgradeSuite.Name)]
public sealed class WebSocketUpgradeBoundaryTests
{
    /// <summary>
    /// Every case is bounded. A mutation that leaves the upgrade half-open — control T2 accepts the
    /// socket before authenticating — must FAIL the control, and a hung control is not a result.
    /// </summary>
    private const int TestTimeoutMs = 60_000;

    private static readonly TimeSpan _settle = TimeSpan.FromSeconds(10);

    private readonly WebSocketUpgradeFixture _fixture;

    public WebSocketUpgradeBoundaryTests(WebSocketUpgradeFixture fixture)
    {
        _fixture = fixture;
    }

    // -------------------------------------------------------------------------------------
    // Positive proof.
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The headline claim: a ticket alone opens a usable socket, and the server associates the
    /// user and device the TICKET names — not whatever the request claims.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_ticket_alone_opens_a_socket_the_server_associates_with_the_ticket_owner()
    {
        var ticket = await _fixture.MintTicketAsync(TestContext.Current.CancellationToken);

        var socket = await _fixture.ConnectWithTicketAsync(ticket.Value, TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(WebSocketState.Open, socket.State);

            // Accepting the handshake is not the claim. The claim is that the connection reaches
            // the session listener with the right identity — which is where a ticket-authenticated
            // socket that cannot resolve its session dies instead.
            var watched = await _fixture.WaitForWatchlistAsync(1, _settle, TestContext.Current.CancellationToken);
            var connection = Assert.Single(watched);

            Assert.Equal(_fixture.UserId, connection.AuthorizationInfo.User?.Id);
            Assert.Equal(WebSocketUpgradeFixture.PrimaryDeviceId, connection.AuthorizationInfo.DeviceId);
            Assert.False(connection.AuthorizationInfo.IsApiKey);
        }
        finally
        {
            await _fixture.CloseAndDrainAsync(socket, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// An accepted socket carries real traffic. A handshake that completes and then dies is not a
    /// working credential, and the state of the socket immediately after <c>ConnectAsync</c> does
    /// not distinguish the two.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task An_accepted_ticket_socket_exchanges_a_real_message()
    {
        var ticket = await _fixture.MintTicketAsync(TestContext.Current.CancellationToken);

        var socket = await _fixture.ConnectWithTicketAsync(ticket.Value, TestContext.Current.CancellationToken);
        try
        {
            var reply = await WebSocketUpgradeFixture.ExchangeAsync(socket, "KeepAlive", TestContext.Current.CancellationToken);

            Assert.False(string.IsNullOrWhiteSpace(reply), "the server closed the socket instead of answering");
            Assert.Contains("MessageType", reply, StringComparison.Ordinal);
        }
        finally
        {
            await _fixture.CloseAndDrainAsync(socket, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Legacy compatibility: the durable token in a header still opens a socket. R2 must not fix
    /// the ticket path by breaking the path every client uses today.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_durable_token_upgrade_still_works()
    {
        var socket = await _fixture.ConnectWithDurableTokenAsync(_fixture.DurableToken, TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(WebSocketState.Open, socket.State);

            var watched = await _fixture.WaitForWatchlistAsync(1, _settle, TestContext.Current.CancellationToken);
            var connection = Assert.Single(watched);
            Assert.Equal(_fixture.UserId, connection.AuthorizationInfo.User?.Id);
        }
        finally
        {
            await _fixture.CloseAndDrainAsync(socket, TestContext.Current.CancellationToken);
        }
    }

    // -------------------------------------------------------------------------------------
    // Refusals. Each asserts the listener never saw a connection.
    // -------------------------------------------------------------------------------------

    [Fact(Timeout = TestTimeoutMs)]
    public async Task No_credential_is_refused_before_the_socket_is_accepted()
        => await AssertRefusedAsync(() => _fixture.ConnectAsync(query: null, cancellationToken: TestContext.Current.CancellationToken), "a request presenting no credential at all");

    [Fact(Timeout = TestTimeoutMs)]
    public async Task An_unknown_ticket_is_refused_before_the_socket_is_accepted()
        => await AssertRefusedAsync(
            () => _fixture.ConnectWithTicketAsync("not-a-ticket", TestContext.Current.CancellationToken),
            "a ticket that was never minted");

    /// <summary>
    /// Expiry is decided by the clock the validator reads. Nothing here sleeps.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task An_expired_ticket_is_refused()
    {
        var ticket = await _fixture.MintTicketAsync(TestContext.Current.CancellationToken);

        _fixture.Factory.Clock.Advance(PlaybackCredentialService.WebSocketTicketLifetime + TimeSpan.FromSeconds(1));

        await AssertRefusedAsync(() => _fixture.ConnectWithTicketAsync(ticket.Value, TestContext.Current.CancellationToken), "an expired ticket");

        // The negative direction, so the assertion above is not satisfied by a validator that
        // refuses everything: a ticket minted after the clock moved is still accepted.
        var fresh = await _fixture.MintTicketAsync(TestContext.Current.CancellationToken);
        var socket = await _fixture.ConnectWithTicketAsync(fresh.Value, TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(WebSocketState.Open, socket.State);
        }
        finally
        {
            await _fixture.CloseAndDrainAsync(socket, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Logging the owning session out revokes its tickets. The session revoked is a second device,
    /// so the rest of the suite keeps its own.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_ticket_whose_owning_session_was_logged_out_is_refused()
    {
        var deviceId = "r2-revoked-" + Guid.NewGuid().ToString("N");
        var token = await _fixture.AuthenticateAsync(deviceId, TestContext.Current.CancellationToken);
        var ticket = await _fixture.MintTicketAsync(deviceId, token, TestContext.Current.CancellationToken);

        using (var owner = _fixture.ClientFor(deviceId, token))
        using (var logout = await owner.PostAsync("/Sessions/Logout", content: null, TestContext.Current.CancellationToken))
        {
            Assert.True(logout.IsSuccessStatusCode, $"logout answered {(int)logout.StatusCode}");
        }

        await AssertRefusedAsync(
            () => _fixture.ConnectWithTicketAsync(ticket.Value, TestContext.Current.CancellationToken, deviceId: deviceId),
            "a ticket whose owning session was logged out");
    }

    /// <summary>
    /// Deleting the device revokes its tickets by the same seam.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_ticket_whose_device_was_deleted_is_refused()
    {
        var deviceId = "r2-deleted-" + Guid.NewGuid().ToString("N");
        var token = await _fixture.AuthenticateAsync(deviceId, TestContext.Current.CancellationToken);
        var ticket = await _fixture.MintTicketAsync(deviceId, token, TestContext.Current.CancellationToken);

        using (var admin = _fixture.ClientFor(WebSocketUpgradeFixture.PrimaryDeviceId, _fixture.DurableToken))
        using (var deleted = await admin.DeleteAsync(
            $"/Devices?id={Uri.EscapeDataString(deviceId)}",
            TestContext.Current.CancellationToken))
        {
            Assert.True(deleted.IsSuccessStatusCode, $"device delete answered {(int)deleted.StatusCode}");
        }

        await AssertRefusedAsync(
            () => _fixture.ConnectWithTicketAsync(ticket.Value, TestContext.Current.CancellationToken, deviceId: deviceId),
            "a ticket whose device was deleted");
    }

    /// <summary>
    /// One successful upgrade, then nothing — while the first socket is still OPEN.
    /// </summary>
    /// <remarks>
    /// The replay happens before the first socket closes, and that ordering is the whole test.
    /// Written the other way round — connect, close, then replay — it passes against a store with
    /// NO single-use guarantee at all, because closing the socket ends the session and
    /// <c>SessionManager.OnSessionEnded</c> revokes the session's tickets. Control T1 found exactly
    /// that: replacing the atomic <c>TryRemove</c> with a non-consuming lookup left the
    /// close-then-replay version green. Holding the socket open removes revocation from the picture
    /// and leaves consumption as the only thing that can refuse the second attempt.
    /// </remarks>
    /// <returns>A task.</returns>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_ticket_is_accepted_once_and_refused_on_replay()
    {
        var ticket = await _fixture.MintTicketAsync(TestContext.Current.CancellationToken);

        var socket = await _fixture.ConnectWithTicketAsync(ticket.Value, TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(WebSocketState.Open, socket.State);

            await AssertRefusedAsync(
                () => _fixture.ConnectWithTicketAsync(ticket.Value, TestContext.Current.CancellationToken),
                "a replayed ticket, while its first socket is still open",
                expectedWatchlist: 1);
        }
        finally
        {
            await _fixture.CloseAndDrainAsync(socket, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Two upgrades racing on one ticket. Exactly one may win.
    /// </summary>
    /// <remarks>
    /// Both <c>ConnectAsync</c> calls are started before either is awaited. If the test host turned
    /// out to serialise them this would prove ordering rather than atomicity, so the assertion is
    /// written as "exactly one", which is the property either way, and the count is reported when
    /// it fails.
    /// </remarks>
    /// <returns>A task.</returns>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Two_simultaneous_upgrades_with_one_ticket_yield_exactly_one_socket()
    {
        var ticket = await _fixture.MintTicketAsync(TestContext.Current.CancellationToken);

        var first = Attempt(ticket.Value);
        var second = Attempt(ticket.Value);
        var outcomes = await Task.WhenAll(first, second);

        var accepted = outcomes.Where(s => s is not null).ToArray();
        try
        {
            Assert.True(
                accepted.Length == 1,
                $"{accepted.Length} of 2 racing upgrades were accepted; exactly one must be");
        }
        finally
        {
            foreach (var socket in accepted)
            {
                await _fixture.CloseAndDrainAsync(socket!, TestContext.Current.CancellationToken);
            }
        }

        async Task<WebSocket?> Attempt(string value)
        {
            try
            {
                return await _fixture.ConnectWithTicketAsync(value, TestContext.Current.CancellationToken);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// A ticket names its own device, and presenting it from a request that claims a different one
    /// does not re-attribute the socket. The header is not what decides.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_ticket_binds_its_own_device_even_when_the_request_claims_another()
    {
        var otherToken = await _fixture.AuthenticateAsync(WebSocketUpgradeFixture.OtherDeviceId, TestContext.Current.CancellationToken);
        var ticket = await _fixture.MintTicketAsync(WebSocketUpgradeFixture.OtherDeviceId, otherToken, TestContext.Current.CancellationToken);

        var socket = await _fixture.ConnectWithTicketAsync(
            ticket.Value,
            TestContext.Current.CancellationToken,
            durableToken: _fixture.DurableToken,
            deviceId: WebSocketUpgradeFixture.PrimaryDeviceId);

        try
        {
            var watched = await _fixture.WaitForWatchlistAsync(1, _settle, TestContext.Current.CancellationToken);
            var connection = Assert.Single(watched);

            Assert.Equal(
                WebSocketUpgradeFixture.OtherDeviceId,
                connection.AuthorizationInfo.DeviceId);
        }
        finally
        {
            await _fixture.CloseAndDrainAsync(socket, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// The no-fallback rule, at the boundary that owns it. A presented ticket that is refused must
    /// never be rescued by a durable token the client also happened to send.
    /// </summary>
    /// <param name="kind">Which flavour of bad ticket to present.</param>
    /// <returns>A task.</returns>
    [Theory(Timeout = TestTimeoutMs)]
    [InlineData("unknown")]
    [InlineData("expired")]
    [InlineData("replayed")]
    public async Task A_refused_ticket_is_never_rescued_by_a_valid_durable_token(string kind)
    {
        var value = kind switch
        {
            "unknown" => "not-a-ticket-either",
            "expired" => await ExpiredTicketAsync(),
            _ => await ReplayedTicketAsync(),
        };

        await AssertRefusedAsync(
            () => _fixture.ConnectWithTicketAsync(value, TestContext.Current.CancellationToken, durableToken: _fixture.DurableToken),
            $"a {kind} ticket presented alongside a valid durable token");
    }

    /// <summary>
    /// A ticket authenticates an upgrade and nothing else. No HTTP route reads its query key, and
    /// smuggling it into the keys the general path does read does not help.
    /// </summary>
    /// <param name="path">The HTTP route to try.</param>
    /// <param name="key">The query key to smuggle it into.</param>
    /// <returns>A task.</returns>
    [Theory(Timeout = TestTimeoutMs)]
    [InlineData("/Items", "webSocketTicket")]
    [InlineData("/Items", "ApiKey")]
    [InlineData("/Items", "api_key")]
    [InlineData("/Users/Me", "webSocketTicket")]
    [InlineData("/System/Info", "ApiKey")]
    [InlineData("/FallbackFont/Fonts", "webSocketTicket")]
    [InlineData("/FallbackFont/Fonts", "ApiKey")]
    [InlineData("/FallbackFont/Fonts", "playbackCapability")]
    public async Task A_ticket_is_never_an_http_credential(string path, string key)
    {
        var ticket = await _fixture.MintTicketAsync(TestContext.Current.CancellationToken);

        using var anonymous = _fixture.Factory.CreateClient();
        using var response = await anonymous.GetAsync(
            $"{path}?{key}={Uri.EscapeDataString(ticket.Value)}",
            TestContext.Current.CancellationToken);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"GET {path} with the ticket in '{key}' answered {(int)response.StatusCode}");
    }

    /// <summary>
    /// No line Tesserafin itself writes may carry a ticket value — not the message, not a
    /// structured state value, not a scope, not an exception.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task No_tesserafin_log_line_ever_carries_a_ticket_value()
    {
        _fixture.Factory.Logs.Clear();

        var accepted = await _fixture.MintTicketAsync(TestContext.Current.CancellationToken);
        var socket = await _fixture.ConnectWithTicketAsync(accepted.Value, TestContext.Current.CancellationToken);
        await _fixture.CloseAndDrainAsync(socket, TestContext.Current.CancellationToken);

        var refused = await _fixture.MintTicketAsync(TestContext.Current.CancellationToken);
        await AssertRefusedAsync(
            () => _fixture.ConnectWithTicketAsync(refused.Value + "-tampered", TestContext.Current.CancellationToken),
            "a tampered ticket, purely to exercise the refusal log path");

        var ours = _fixture.Factory.Logs.Captured
            .Where(line => line.Contains(":Tesserafin.", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(ours);
        foreach (var value in new[] { accepted.Value, refused.Value })
        {
            Assert.DoesNotContain(ours, line => line.Contains(value, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// ASP.NET Core's own request diagnostics DO carry the ticket, because the ticket is in the
    /// query string and that is what those lines log. This test pins the fact rather than hiding
    /// it, and pins the mitigation with it.
    /// </summary>
    /// <remarks>
    /// The credential is in the URL — that is the whole of #153, and a ticket does not change it.
    /// What a ticket changes is the blast radius: this value lives thirty seconds, is consumed by
    /// exactly one upgrade, and authenticates nothing but that upgrade, where the durable token it
    /// replaces was a long-lived full-privilege credential.
    ///
    /// The shipped Serilog configuration overrides the entire <c>Microsoft</c> category to
    /// <c>Warning</c>, so these Information-level lines are never written by a real server. The
    /// test host captures every level regardless of configuration, which is why they are visible
    /// here at all. Asserting the override exists means a future configuration change that drops it
    /// fails HERE, with this explanation attached, instead of quietly starting to log credentials.
    /// </remarks>
    /// <returns>A task.</returns>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task The_framework_request_log_carries_the_ticket_and_is_suppressed_in_production()
    {
        _fixture.Factory.Logs.Clear();

        var ticket = await _fixture.MintTicketAsync(TestContext.Current.CancellationToken);
        var socket = await _fixture.ConnectWithTicketAsync(ticket.Value, TestContext.Current.CancellationToken);
        await _fixture.CloseAndDrainAsync(socket, TestContext.Current.CancellationToken);

        // The fact, stated. If this ever stops being true the mitigation below is no longer needed
        // and this test should be revisited rather than deleted.
        Assert.Contains(
            _fixture.Factory.Logs.Captured,
            line => line.Contains("Microsoft.AspNetCore.Hosting.Diagnostics", StringComparison.Ordinal)
                && line.Contains(ticket.Value, StringComparison.Ordinal));

        // The mitigation, asserted against the file the server actually ships.
        var loggingConfig = Path.Combine(
            AppContext.BaseDirectory,
            "Resources",
            "Configuration",
            "logging.json");

        Assert.True(File.Exists(loggingConfig), $"shipped logging configuration not found at {loggingConfig}");

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(loggingConfig, TestContext.Current.CancellationToken));
        var microsoftLevel = document.RootElement
            .GetProperty("Serilog")
            .GetProperty("MinimumLevel")
            .GetProperty("Override")
            .GetProperty("Microsoft")
            .GetString();

        Assert.Equal("Warning", microsoftLevel);
    }

    // -------------------------------------------------------------------------------------

    private async Task<string> ExpiredTicketAsync()
    {
        var ticket = await _fixture.MintTicketAsync(TestContext.Current.CancellationToken);
        _fixture.Factory.Clock.Advance(PlaybackCredentialService.WebSocketTicketLifetime + TimeSpan.FromSeconds(1));
        return ticket.Value;
    }

    private async Task<string> ReplayedTicketAsync()
    {
        var ticket = await _fixture.MintTicketAsync(TestContext.Current.CancellationToken);

        // Consumed and then released, so the value is spent. The caller asserts the refusal while
        // presenting a valid durable token, and that assertion is about fallback rather than about
        // why the ticket is dead, so closing here is fine.
        var socket = await _fixture.ConnectWithTicketAsync(ticket.Value, TestContext.Current.CancellationToken);
        await _fixture.CloseAndDrainAsync(socket, TestContext.Current.CancellationToken);
        return ticket.Value;
    }

    private async Task AssertRefusedAsync(Func<Task<WebSocket>> connect, string what, int? expectedWatchlist = null)
    {
        var before = expectedWatchlist ?? _fixture.Watchlist().Count;

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
            Assert.True(socket is null, $"the upgrade was accepted for {what}");

            // The load-bearing half. A connection reaches the listener only after
            // AcceptWebSocketAsync returned, so an unchanged watchlist is what proves the refusal
            // happened BEFORE acceptance rather than after it.
            await Task.Delay(250, TestContext.Current.CancellationToken);
            Assert.Equal(before, _fixture.Watchlist().Count);
        }
        finally
        {
            if (socket is not null)
            {
                await _fixture.CloseAndDrainAsync(socket, TestContext.Current.CancellationToken);
            }
        }
    }
}
