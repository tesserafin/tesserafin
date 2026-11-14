using System;
using System.Globalization;
using System.Security.Claims;
using Tesserafin.Api.Constants;
using Tesserafin.Controller.Net;
using Tesserafin.Data;
using Tesserafin.Data.Enums;

namespace Tesserafin.Api.Auth
{
    /// <summary>
    /// The one place an <see cref="AuthorizationInfo"/> is turned into a
    /// <see cref="ClaimsIdentity"/> (#153-A0-R3).
    /// </summary>
    /// <remarks>
    /// WHY THIS TYPE EXISTS. Two authentication paths end up producing the identity every
    /// downstream reader consumes: <see cref="CustomAuthenticationHandler"/> for the durable session
    /// token, and <c>WebSocketManager</c> for a consumed single-use ticket. R2 gave the ticket path
    /// its own copy of the claim list, and a copy is a promise that someone will have to keep by
    /// hand. The claims are not decoration — <c>RequestHelpers.GetSession</c> rebuilds the session
    /// key from <c>Client</c>, <c>DeviceId</c> and <c>Device</c>, so a single claim that drifts
    /// silently attaches a socket to a different session rather than failing.
    ///
    /// WHY IT LIVES IN <c>Tesserafin.Api</c>. <c>Tesserafin.Server.Core</c> references
    /// <c>Tesserafin.Api</c>, not the other way round, and the claim names it projects
    /// (<see cref="InternalClaimTypes"/>, <see cref="UserRoles"/>) already live here. Putting the
    /// projector anywhere else would either invert that reference or split the constants from the
    /// only code that writes them.
    ///
    /// WHAT A CALLER IS STILL ALLOWED TO DECIDE. Exactly one thing: the authentication scheme, which
    /// is the caller's own identity and cannot be derived from an <see cref="AuthorizationInfo"/>.
    /// Everything else — which claims exist, how many times, and what each one is derived from — is
    /// decided here. A caller that wants a claim to differ says so by handing over a different
    /// <see cref="AuthorizationInfo"/>, which is what the ticket path does for
    /// <see cref="AuthorizationInfo.Token"/> and <see cref="AuthorizationInfo.IsApiKey"/>.
    /// </remarks>
    public static class AuthorizationInfoPrincipal
    {
        /// <summary>
        /// Projects an authorization into the identity every downstream reader consumes.
        /// </summary>
        /// <param name="authorizationInfo">The resolved authorization.</param>
        /// <param name="authenticationScheme">The scheme the identity is issued under.</param>
        /// <returns>The identity.</returns>
        public static ClaimsIdentity CreateIdentity(AuthorizationInfo authorizationInfo, string authenticationScheme)
        {
            ArgumentNullException.ThrowIfNull(authorizationInfo);

            var role = UserRoles.User;
            if (authorizationInfo.IsApiKey
                || (authorizationInfo.User?.HasPermission(PermissionKind.IsAdministrator) ?? false))
            {
                role = UserRoles.Administrator;
            }

            var claims = new[]
            {
                new Claim(ClaimTypes.Name, authorizationInfo.User?.Username ?? string.Empty),
                new Claim(ClaimTypes.Role, role),
                new Claim(InternalClaimTypes.UserId, authorizationInfo.UserId.ToString("N", CultureInfo.InvariantCulture)),
                new Claim(InternalClaimTypes.DeviceId, authorizationInfo.DeviceId ?? string.Empty),
                new Claim(InternalClaimTypes.Device, authorizationInfo.Device ?? string.Empty),
                new Claim(InternalClaimTypes.Client, authorizationInfo.Client ?? string.Empty),
                new Claim(InternalClaimTypes.Version, authorizationInfo.Version ?? string.Empty),
                new Claim(InternalClaimTypes.Token, authorizationInfo.Token ?? string.Empty),
                new Claim(InternalClaimTypes.IsApiKey, authorizationInfo.IsApiKey.ToString(CultureInfo.InvariantCulture))
            };

            return new ClaimsIdentity(claims, authenticationScheme);
        }

        /// <summary>
        /// Projects an authorization into a principal carrying exactly one such identity.
        /// </summary>
        /// <param name="authorizationInfo">The resolved authorization.</param>
        /// <param name="authenticationScheme">The scheme the identity is issued under.</param>
        /// <returns>The principal.</returns>
        public static ClaimsPrincipal CreatePrincipal(AuthorizationInfo authorizationInfo, string authenticationScheme)
            => new(CreateIdentity(authorizationInfo, authenticationScheme));
    }
}
