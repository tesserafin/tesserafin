using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Api.Models.PlaybackCredentialDtos;
using Tesserafin.Api.Models.StartupDtos;
using Tesserafin.Api.WebSocketListeners;
using Tesserafin.Controller.Net;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Controller.Session;
using Tesserafin.Extensions.Json;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.PlaybackCredentials;

/// <summary>
/// One booted server for the #153-A0-R2 WebSocket upgrade matrix: real
/// <c>WebSocketHandlerMiddleware</c>, real <c>WebSocketManager</c>, real upgrade handshake.
/// </summary>
/// <remarks>
/// WHY NOT THE PRIMITIVE TESTS. <c>PlaybackCredentialServiceTests</c> proves
/// <c>ConsumeWebSocketTicket</c> in isolation, which is a statement about a dictionary. It cannot
/// say whether the upgrade path calls it, whether it calls it BEFORE accepting the socket, whether
/// a refused ticket falls back to the durable token, or what identity the accepted connection ends
/// up carrying. Those are the properties the contract actually promises, and every one of them
/// lives in <c>WebSocketManager.AuthenticateUpgrade</c> rather than in the store.
///
/// HOW A REFUSAL IS OBSERVED. Not by the exception type: <c>ConnectAsync</c> throws
/// <see cref="InvalidOperationException"/> both when the server refuses before accepting and when
/// it accepts and then drops the connection, so the exception alone proves nothing about ordering.
/// The discriminator is <c>SessionWebSocketListener</c>'s watchlist — a connection reaches it only
/// through <c>ProcessWebSocketConnectedAsync</c>, which runs only after
/// <c>AcceptWebSocketAsync</c> has returned. A refusal that never accepts leaves the watchlist
/// untouched, and that is what every negative case here asserts.
/// </remarks>
public sealed class WebSocketUpgradeFixture : IAsyncLifetime
{
    /// <summary>The device the fixture's own session uses.</summary>
    public const string PrimaryDeviceId = "r2-primary-device";

    /// <summary>A second device, for tickets that belong to somebody else.</summary>
    public const string OtherDeviceId = "r2-other-device";

    private string _userName = string.Empty;
    private string _password = string.Empty;

    /// <summary>Gets the booted server.</summary>
    public WebSocketUpgradeApplicationFactory Factory { get; private set; } = null!;

    /// <summary>Gets the primary session's durable token.</summary>
    public string DurableToken { get; private set; } = string.Empty;

    /// <summary>Gets the fixture user's id.</summary>
    public Guid UserId { get; private set; }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        Factory = new WebSocketUpgradeApplicationFactory();
        var cancellationToken = TestContext.Current.CancellationToken;

        using (var setup = Factory.CreateClient())
        {
            var startupUser = await setup
                .GetFromJsonAsync<StartupUserDto>("/Startup/User", JsonDefaults.Options, cancellationToken)
                .ConfigureAwait(false);

            _userName = startupUser!.Name ?? string.Empty;
            _password = startupUser.Password ?? string.Empty;

            using var complete = await setup.PostAsync("/Startup/Complete", new ByteArrayContent([]), cancellationToken).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);
        }

        DurableToken = await AuthenticateAsync(PrimaryDeviceId, cancellationToken).ConfigureAwait(false);

        using var me = ClientFor(PrimaryDeviceId, DurableToken);
        using var document = JsonDocument.Parse(await me.GetStringAsync("/Users/Me", cancellationToken).ConfigureAwait(false));
        UserId = Guid.Parse(document.RootElement.GetProperty("Id").GetString()!);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Factory?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Authenticates the fixture's user on one device.
    /// </summary>
    /// <param name="deviceId">The device to authenticate as.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>That session's durable token.</returns>
    public async Task<string> AuthenticateAsync(string deviceId, CancellationToken cancellationToken)
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, MediaBoundaryFixture.AuthorizationHeader(deviceId, null));
        request.Content = JsonContent.Create(new { Username = _userName, Pw = _password }, options: JsonDefaults.Options);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return document.RootElement.GetProperty("AccessToken").GetString()!;
    }

    /// <summary>
    /// Creates an HTTP client presenting one device's durable token in the header.
    /// </summary>
    /// <param name="deviceId">The device id.</param>
    /// <param name="token">That device's token.</param>
    /// <returns>An authenticated client.</returns>
    public HttpClient ClientFor(string deviceId, string token)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            AuthHelper.AuthHeaderName,
            MediaBoundaryFixture.AuthorizationHeader(deviceId, token));
        return client;
    }

    /// <summary>
    /// Mints a WebSocket ticket over real HTTP, durable token in a header.
    /// </summary>
    /// <param name="deviceId">The device to mint for.</param>
    /// <param name="token">That device's durable token.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The minted ticket.</returns>
    public async Task<WebSocketTicketDto> MintTicketAsync(string deviceId, string token, CancellationToken cancellationToken)
    {
        using var client = ClientFor(deviceId, token);
        using var response = await client.PostAsync("/WebSocket/Tickets", content: null, cancellationToken).ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content
            .ReadFromJsonAsync<WebSocketTicketDto>(JsonDefaults.Options, cancellationToken)
            .ConfigureAwait(false);

        Assert.NotNull(dto);
        Assert.NotEmpty(dto!.Value);
        return dto;
    }

    /// <summary>
    /// Mints a ticket for the fixture's primary device.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The minted ticket.</returns>
    public Task<WebSocketTicketDto> MintTicketAsync(CancellationToken cancellationToken) => MintTicketAsync(PrimaryDeviceId, DurableToken, cancellationToken);

    /// <summary>
    /// Opens the real socket presenting ONLY a ticket in the query string — no header, no token.
    /// </summary>
    /// <param name="ticket">The ticket value.</param>
    /// <param name="cancellationToken">Cancels the handshake.</param>
    /// <param name="durableToken">An optional durable token to also present, for no-fallback cases.</param>
    /// <param name="deviceId">The device the optional header claims.</param>
    /// <returns>The connected socket.</returns>
    public Task<WebSocket> ConnectWithTicketAsync(string ticket, CancellationToken cancellationToken, string? durableToken = null, string deviceId = PrimaryDeviceId)
        => ConnectAsync($"webSocketTicket={Uri.EscapeDataString(ticket)}", cancellationToken, durableToken, deviceId);

    /// <summary>
    /// Opens the real socket the legacy way, durable token in the header.
    /// </summary>
    /// <param name="durableToken">The durable token.</param>
    /// <param name="cancellationToken">Cancels the handshake.</param>
    /// <param name="deviceId">The device the header claims.</param>
    /// <returns>The connected socket.</returns>
    public Task<WebSocket> ConnectWithDurableTokenAsync(string durableToken, CancellationToken cancellationToken, string deviceId = PrimaryDeviceId)
        => ConnectAsync(query: null, cancellationToken, durableToken, deviceId);

    /// <summary>
    /// Opens the real socket with an arbitrary query string.
    /// </summary>
    /// <param name="query">The query string, without the leading '?'.</param>
    /// <param name="cancellationToken">Cancels the handshake.</param>
    /// <param name="durableToken">An optional durable token for the header.</param>
    /// <param name="deviceId">The device the optional header claims.</param>
    /// <returns>The connected socket.</returns>
    public async Task<WebSocket> ConnectAsync(string? query, CancellationToken cancellationToken, string? durableToken = null, string deviceId = PrimaryDeviceId)
    {
        var client = Factory.Server.CreateWebSocketClient();
        if (durableToken is not null)
        {
            client.ConfigureRequest = request =>
                request.Headers.Authorization = MediaBoundaryFixture.AuthorizationHeader(deviceId, durableToken);
        }

        var uri = new UriBuilder(Factory.Server.BaseAddress)
        {
            Scheme = "ws",
            Path = "websocket",
            Query = query ?? string.Empty
        }.Uri;

        return await client.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the real socket with a query string and a verbatim authorization header, so a case can
    /// make the request claim a client, version and device the ticket does not name (#153-A0-R3).
    /// </summary>
    /// <param name="query">The query string, without the leading '?'.</param>
    /// <param name="authorizationHeader">The complete header value, or null for none.</param>
    /// <param name="cancellationToken">Cancels the handshake.</param>
    /// <returns>The connected socket.</returns>
    public async Task<WebSocket> ConnectWithHeaderAsync(string? query, string? authorizationHeader, CancellationToken cancellationToken)
    {
        var client = Factory.Server.CreateWebSocketClient();
        if (authorizationHeader is not null)
        {
            client.ConfigureRequest = request => request.Headers.Authorization = authorizationHeader;
        }

        var uri = new UriBuilder(Factory.Server.BaseAddress)
        {
            Scheme = "ws",
            Path = "websocket",
            Query = query ?? string.Empty
        }.Uri;

        return await client.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------------------
    // #153-A0-R3: live-session redemption and principal parity.
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The authorization header, with every field the session key is built from under the caller's
    /// control.
    /// </summary>
    /// <param name="deviceId">The device id.</param>
    /// <param name="token">The durable token, or null for none.</param>
    /// <param name="client">The client name.</param>
    /// <param name="device">The device name.</param>
    /// <param name="version">The application version.</param>
    /// <returns>The header value.</returns>
    /// <remarks>
    /// <c>MediaBoundaryFixture.AuthorizationHeader</c> varies only the device id, so it can prove
    /// that a ticket ignores a lying device but cannot say anything about a lying client or version
    /// — and a session is keyed on client and device id, with the version carried alongside. R3
    /// needs all three to move independently.
    /// </remarks>
    public static string AuthorizationHeader(string deviceId, string? token, string client, string device, string version)
    {
        var header = $"MediaBrowser Client=\"{client}\", DeviceId=\"{deviceId}\", Device=\"{device}\", Version=\"{version}\"";
        return token is null ? header : header + $", Token=\"{token}\"";
    }

    /// <summary>
    /// Gets a service out of the running server.
    /// </summary>
    /// <typeparam name="T">The service type.</typeparam>
    /// <returns>The service.</returns>
    public T Service<T>()
        where T : notnull
        => Factory.Services.GetRequiredService<T>();

    /// <summary>
    /// Gets the recorder that observes every accepted upgrade.
    /// </summary>
    /// <returns>The recorder.</returns>
    public UpgradeRecorder Recorder() => Factory.Recorder;

    /// <summary>
    /// A stable snapshot of the live sessions.
    /// </summary>
    /// <returns>The sessions.</returns>
    /// <remarks>
    /// <c>ISessionManager.Sessions</c> projects a live <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
    /// through <c>OrderByDescending</c>, so enumerating it while another connection is opening
    /// throws. The same hazard the fixture's <see cref="Watchlist"/> already retries around.
    /// </remarks>
    public IReadOnlyList<SessionInfo> Sessions()
    {
        var manager = Service<ISessionManager>();
        while (true)
        {
            try
            {
                return manager.Sessions.ToArray();
            }
            catch (InvalidOperationException)
            {
                // Mutated mid-enumeration; retry.
            }
        }
    }

    /// <summary>
    /// The single live session belonging to one device, or null.
    /// </summary>
    /// <param name="deviceId">The device id.</param>
    /// <returns>The session, or null.</returns>
    public SessionInfo? SessionForDevice(string deviceId)
        => Sessions().FirstOrDefault(
            session => string.Equals(session.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Mints a WebSocket ticket through the real credential service, with the binding stated
    /// directly rather than derived from an authenticated request.
    /// </summary>
    /// <param name="userId">The user to bind to.</param>
    /// <param name="sessionId">The session to bind to.</param>
    /// <param name="deviceId">The device to bind to.</param>
    /// <returns>The ticket value.</returns>
    /// <remarks>
    /// WHY THIS SEAM IS LEGITIMATE. <c>POST /WebSocket/Tickets</c> derives all three fields from an
    /// authenticated request, so by construction it can only ever mint a ticket whose session, user
    /// and device already agree with each other and with a live session. That is exactly why it
    /// cannot exercise the guards R3 adds. This mints through the same singleton
    /// <see cref="IPlaybackCredentialService"/> the upgrade path consumes from — the store is real,
    /// the ticket is real, the consumption is real. Only the binding is chosen.
    ///
    /// The service is registered <c>AddSingleton</c>, so a ticket minted here is the same ticket the
    /// upgrade later consumes. A scoped registration would have made every one of these cases refuse
    /// for the wrong reason — at consumption, as an unknown value — and left the matching controls
    /// inert.
    /// </remarks>
    public string MintTicketDirectly(Guid userId, string sessionId, string deviceId)
        => Service<IPlaybackCredentialService>()
            .MintWebSocketTicket(new WebSocketTicketRequest(userId, sessionId, deviceId))
            .Value;

    /// <summary>
    /// Creates a second user and signs it in on its own device, producing a live session that can
    /// be torn about without disturbing the fixture's own.
    /// </summary>
    /// <param name="deviceId">The device to sign the new user in on.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The new user's id and that session's durable token.</returns>
    public async Task<(Guid UserId, string Token)> CreateUserSessionAsync(string deviceId, CancellationToken cancellationToken)
    {
        var password = "r3-" + Guid.NewGuid().ToString("N");
        var name = "r3-user-" + Guid.NewGuid().ToString("N")[..8];

        using var admin = ClientFor(PrimaryDeviceId, DurableToken);
        using var created = await admin.PostAsJsonAsync(
            "/Users/New",
            new { Name = name, Password = password },
            JsonDefaults.Options,
            cancellationToken).ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        using var document = JsonDocument.Parse(
            await created.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var userId = Guid.Parse(document.RootElement.GetProperty("Id").GetString()!);

        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation(
            AuthHelper.AuthHeaderName,
            MediaBoundaryFixture.AuthorizationHeader(deviceId, null));
        request.Content = JsonContent.Create(new { Username = name, Pw = password }, options: JsonDefaults.Options);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var authenticated = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        return (userId, authenticated.RootElement.GetProperty("AccessToken").GetString()!);
    }

    /// <summary>
    /// Gets the connections <c>SessionWebSocketListener</c> is watching. A connection reaches this
    /// list only after <c>AcceptWebSocketAsync</c> returned, so it is the instrument that separates
    /// "refused before accepting" from "accepted then dropped".
    /// </summary>
    /// <returns>A snapshot of the watchlist.</returns>
    public IReadOnlyList<IWebSocketConnection> Watchlist()
    {
        var listener = Factory.Services.GetRequiredService<IEnumerable<IWebSocketListener>>()
            .OfType<SessionWebSocketListener>()
            .Single();

        var field = typeof(SessionWebSocketListener)
            .GetField("_webSockets", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);

        var watchlist = (IEnumerable<IWebSocketConnection>)field!.GetValue(listener)!;

        while (true)
        {
            try
            {
                return watchlist.ToArray();
            }
            catch (InvalidOperationException)
            {
                // The keep-alive watchdog mutated the set mid-enumeration; retry.
            }
        }
    }

    /// <summary>
    /// Waits until the watchlist reaches the expected size, or the timeout expires.
    /// </summary>
    /// <param name="expected">The size to wait for.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The final snapshot.</returns>
    public async Task<IReadOnlyList<IWebSocketConnection>> WaitForWatchlistAsync(int expected, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var snapshot = Watchlist();
            if (snapshot.Count >= expected || DateTime.UtcNow >= deadline)
            {
                return snapshot;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Closes a socket and waits for the listener to forget it, so the next case starts from a
    /// known watchlist.
    /// </summary>
    /// <param name="socket">The socket to close.</param>
    /// <param name="cancellationToken">Stops waiting for the listener; the close itself always runs.</param>
    /// <returns>A task.</returns>
    public async Task CloseAndDrainAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);

        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test over", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
            // Already gone.
        }

        socket.Dispose();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Watchlist().Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends one bounded message and reads one back, so an accepted socket is proven to carry real
    /// traffic rather than merely to have completed a handshake.
    /// </summary>
    /// <param name="socket">The accepted socket.</param>
    /// <param name="messageType">The message type to send.</param>
    /// <param name="cancellationToken">Cancels the exchange, in addition to its own 20-second bound.</param>
    /// <returns>The first message received, as text.</returns>
    public static async Task<string> ExchangeAsync(WebSocket socket, string messageType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);

        var payload = Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{{\"MessageType\":\"{messageType}\"}}"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);

        var buffer = new byte[8192];
        var received = await socket.ReceiveAsync(buffer, timeout.Token).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer, 0, received.Count);
    }
}
