#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Tesserafin.Api.Auth;
using Tesserafin.Common.Extensions;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Net;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Controller.Session;
using Tesserafin.Data;
using Tesserafin.Extensions;

namespace Tesserafin.Api.WebSockets
{
    public class WebSocketManager : IWebSocketManager
    {
        /// <summary>
        /// The query key a single-use WebSocket ticket travels in (#153).
        /// </summary>
        /// <remarks>
        /// Deliberately not <c>api_key</c>. A ticket is consumed here and nowhere else, so it can
        /// authenticate an upgrade and cannot authenticate an HTTP request of any kind — media or
        /// general — because no HTTP path reads this key.
        /// </remarks>
        public const string TicketQueryKey = "webSocketTicket";

        private readonly IWebSocketListener[] _webSocketListeners;
        private readonly IAuthService _authService;
        private readonly ILogger<WebSocketManager> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IPlaybackCredentialService _credentialService;
        private readonly IUserManager _userManager;
        private readonly ISessionManager _sessionManager;

        public WebSocketManager(
            IAuthService authService,
            IEnumerable<IWebSocketListener> webSocketListeners,
            ILogger<WebSocketManager> logger,
            ILoggerFactory loggerFactory,
            IPlaybackCredentialService credentialService,
            IUserManager userManager,
            ISessionManager sessionManager)
        {
            _webSocketListeners = webSocketListeners.ToArray();
            _authService = authService;
            _logger = logger;
            _loggerFactory = loggerFactory;
            _credentialService = credentialService;
            _userManager = userManager;
            _sessionManager = sessionManager;
        }

        /// <inheritdoc />
        public async Task WebSocketRequestHandler(HttpContext context)
        {
            var authorizationInfo = await AuthenticateUpgrade(context).ConfigureAwait(false);
            if (!authorizationInfo.IsAuthenticated)
            {
                throw new SecurityException("Token is required");
            }

            try
            {
                _logger.LogInformation("WS {IP} request", context.Connection.RemoteIpAddress);

                WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                var connection = new WebSocketConnection(
                    _loggerFactory.CreateLogger<WebSocketConnection>(),
                    webSocket,
                    authorizationInfo,
                    context.GetNormalizedRemoteIP())
                {
                    RequestUICulture = CultureInfo.CurrentUICulture
                };
                connection.OnReceive = result =>
                {
                    connection.ApplyRequestCulture();
                    return ProcessWebSocketMessageReceived(result);
                };
                await using (connection.ConfigureAwait(false))
                {
                    var tasks = new Task[_webSocketListeners.Length];
                    for (var i = 0; i < _webSocketListeners.Length; ++i)
                    {
                        tasks[i] = _webSocketListeners[i].ProcessWebSocketConnectedAsync(connection, context);
                    }

                    await Task.WhenAll(tasks).ConfigureAwait(false);

                    await connection.ReceiveAsync().ConfigureAwait(false);
                    _logger.LogInformation("WS {IP} closed", context.Connection.RemoteIpAddress);
                }
            }
            catch (Exception ex) // Otherwise ASP.Net will ignore the exception
            {
                _logger.LogError(ex, "WS {IP} WebSocketRequestHandler error", context.Connection.RemoteIpAddress);
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = 500;
                }
            }
        }

        /// <summary>
        /// Authenticates a WebSocket upgrade, preferring a single-use ticket over the durable token.
        /// </summary>
        /// <remarks>
        /// A PRESENTED TICKET IS NEVER ALLOWED TO FALL BACK. If a ticket is present and refused —
        /// unknown, expired, revoked or already consumed — this returns an unauthenticated result
        /// rather than trying the durable token. Falling back would mean a replayed ticket quietly
        /// succeeding whenever the client also happened to send its session token, which is exactly
        /// the silent-fallback failure the contract forbids.
        ///
        /// Consumption happens HERE, before the socket is accepted, not after a successful upgrade.
        /// Consuming on success only would leave the ticket replayable by racing the handshake.
        /// </remarks>
        private async Task<AuthorizationInfo> AuthenticateUpgrade(HttpContext context)
        {
            var presentedTicket = context.Request.Query[TicketQueryKey].ToString();
            if (string.IsNullOrEmpty(presentedTicket))
            {
                return await _authService.Authenticate(context.Request).ConfigureAwait(false);
            }

            var consumed = _credentialService.ConsumeWebSocketTicket(presentedTicket);
            if (!consumed.IsValid)
            {
                return new AuthorizationInfo { IsAuthenticated = false };
            }

            // CONSUMPTION IS NOT REDEMPTION. A valid ticket proves only that this value was minted,
            // has not expired and has not been used. It says nothing about whether the session it
            // names is still live, whether its user still exists, or whether the bindings it
            // carries still agree with that session — all three of which can have changed since it
            // was minted, and any of which makes the identity it would produce a fiction.
            var boundIdentity = TryResolveBoundIdentity(consumed);
            if (boundIdentity is null)
            {
                // Deliberately the same unauthenticated result as a refused consumption, and
                // deliberately NOT a fall-through to _authService.Authenticate. The ticket has
                // already been spent by the call above and stays spent; a client that also sent a
                // durable token must not have this upgrade rescued by it, for exactly the reason
                // stated in the remarks. Returning here is also what keeps the refusal ahead of
                // AcceptWebSocketAsync, which lives in the caller.
                return new AuthorizationInfo { IsAuthenticated = false };
            }

            // WHY THE PRINCIPAL IS SET AND NOT JUST RETURNED. Every WebSocket listener resolves its
            // session from context.User, not from this AuthorizationInfo —
            // SessionWebSocketListener.ProcessWebSocketConnectedAsync calls
            // RequestHelpers.GetSession(httpContext), which reads the user id, client, version,
            // device id and device name off the principal. A ticket upgrade that leaves the
            // principal anonymous therefore authenticates the handshake and then produces a socket
            // that is accepted and immediately torn down when the listener cannot resolve it. The
            // durable-token path does not hit this because the authentication middleware has
            // already populated the principal by the time the upgrade runs.
            //
            // The projection is shared with CustomAuthenticationHandler rather than repeated, so
            // the two identities cannot drift apart claim by claim.
            context.User = AuthorizationInfoPrincipal.CreatePrincipal(boundIdentity, TicketQueryKey);

            return boundIdentity;
        }

        /// <summary>
        /// Resolves the identity a consumed ticket may act as, from live state only.
        /// </summary>
        /// <param name="consumed">The successfully consumed ticket.</param>
        /// <returns>The authorization, or null if the ticket may not be redeemed.</returns>
        /// <remarks>
        /// Every failure returns null rather than throwing, so the caller can refuse before the
        /// socket is accepted rather than after. Each check is a separate statement on purpose:
        /// they are independent claims about the world and each one is separately load-bearing.
        ///
        /// Nothing here reads the request. The client, version, device id and device name all come
        /// from the resolved session, which is what makes a request that claims a different device,
        /// client or version unable to move the socket to another session — the session key
        /// downstream is rebuilt from these very values.
        /// </remarks>
        private AuthorizationInfo TryResolveBoundIdentity(WebSocketTicketValidation consumed)
        {
            // The ticket must name a session that is live RIGHT NOW. Reading the fields off a
            // session that is merely absent — which is what a null-conditional does — yields empty
            // strings, and an empty client and device id are a perfectly usable session key for a
            // DIFFERENT session, so the socket would silently attach to the wrong one.
            var boundSession = SnapshotSessions().FirstOrDefault(
                session => string.Equals(session.Id, consumed.SessionId, StringComparison.Ordinal));
            if (boundSession is null)
            {
                return null;
            }

            // The user must still resolve. GetUserById rejects an empty id rather than answering
            // null, so the empty case is separated out instead of being allowed to throw out of an
            // authentication path.
            var user = consumed.UserId.IsEmpty() ? null : _userManager.GetUserById(consumed.UserId);
            if (user is null)
            {
                return null;
            }

            // The bindings the ticket carries must still be the session's own. Both directions are
            // checked, because a ticket can name a live session and a live user that have nothing
            // to do with each other.
            if (!string.Equals(boundSession.DeviceId, consumed.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!boundSession.UserId.Equals(user.Id))
            {
                return null;
            }

            return new AuthorizationInfo
            {
                IsAuthenticated = true,
                User = user,
                DeviceId = boundSession.DeviceId,
                Device = boundSession.DeviceName,
                Client = boundSession.Client,
                Version = boundSession.ApplicationVersion,

                // Never the ticket. It is consumed, it is not a bearer credential any more, and
                // putting it here would publish it into the principal's claims and from there into
                // anything that logs them.
                Token = string.Empty,
                IsApiKey = false
            };
        }

        /// <summary>
        /// A stable snapshot of the live sessions.
        /// </summary>
        /// <returns>The sessions.</returns>
        /// <remarks>
        /// <c>ISessionManager.Sessions</c> projects a live concurrent dictionary through
        /// <c>OrderByDescending</c>, so enumerating it while another connection opens or closes
        /// throws. An upgrade must not fail for that reason.
        /// </remarks>
        private IReadOnlyList<SessionInfo> SnapshotSessions()
        {
            while (true)
            {
                try
                {
                    return _sessionManager.Sessions.ToArray();
                }
                catch (InvalidOperationException)
                {
                    // Mutated mid-enumeration; retry.
                }
            }
        }

        /// <summary>
        /// Processes the web socket message received.
        /// </summary>
        /// <param name="result">The result.</param>
        private async Task ProcessWebSocketMessageReceived(WebSocketMessageInfo result)
        {
            var tasks = new Task[_webSocketListeners.Length];
            for (var i = 0; i < _webSocketListeners.Length; ++i)
            {
                tasks[i] = _webSocketListeners[i].ProcessMessageAsync(result);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }
}
