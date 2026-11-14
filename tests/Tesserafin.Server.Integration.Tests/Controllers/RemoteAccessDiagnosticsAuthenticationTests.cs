using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Controller.Library;
using Tesserafin.Data;
using Tesserafin.Data.Enums;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.Controllers;

/// <summary>
/// Who may ask the administrator diagnostics anything at all (R1-P, #248).
/// </summary>
/// <remarks>
/// Every case drives the real pipeline — Authorization header, AuthorizationContext, AuthService,
/// CustomAuthenticationHandler, the role claims it issues, <c>Policies.RequiresElevation</c>, and
/// only then the controller. Nothing here substitutes a permissive test handler; a test that
/// replaced the authorization it is checking would be checking itself.
///
/// EACH CASE GETS ITS OWN HOST. The states are mutually exclusive — startup incomplete, startup
/// complete, the administrator disabled, the administrator de-elevated — and a shared host would
/// make the suite order-dependent, which is exactly the property these assertions must not have.
///
/// Every rejected caller is additionally required to leave the collector untouched. A 401 that
/// still collected would mean the server had already read its own network posture for a caller it
/// then refused.
/// </remarks>
public sealed class RemoteAccessDiagnosticsAuthenticationTests
{
    private const string Route = "/System/RemoteAccess/Diagnostics";

    private static StringContent Body()
        => new(
            """{"Hostname": null, "IPv4Policy": "Unspecified", "IPv6Policy": "Unspecified"}""",
            Encoding.UTF8,
            "application/json");

    [Fact]
    public async Task DiagnosticsWithoutAuthentication_Is401AndNeverCollects()
    {
        var factory = new RemoteAccessDiagnosticsApplicationFactory();
        try
        {
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(Route, Body(), TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(0, factory.Addresses.CallCount);
            Assert.Equal(0, factory.Resolver.CallCount);
        }
        finally
        {
            await factory.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task FirstTimeSetupGrant_DoesNotSatisfyRequiresElevation()
    {
        // Startup is deliberately left INCOMPLETE, so the pre-onboarding grant is live. It exists
        // to let the wizard configure a server that has no administrator yet.
        //
        // STRUCTURAL PROOF, NOT A POLICY DISCRIMINATOR. FirstTimeSetupHandler applies that grant
        // only to endpoints marked [FirstTimeSetupEndpoint] and only to tokenless requests, so an
        // anonymous caller is refused here whatever FirstTimeSetup* policy the endpoint were given.
        // That is the property this case pins - the grant provably cannot leak to an unmarked
        // endpoint - and it is why the discriminating case below uses a de-elevated user instead.
        var factory = new RemoteAccessDiagnosticsApplicationFactory();
        try
        {
            using var client = factory.CreateClient();

            // Proof the grant really is live on this host: a first-time-setup endpoint answers.
            using var wizard = await client.GetAsync("/Startup/User", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, wizard.StatusCode);

            using var response = await client.PostAsync(Route, Body(), TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(0, factory.Addresses.CallCount);
        }
        finally
        {
            await factory.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DisabledAdministrator_IsRejectedBeforeAuthorization()
    {
        // The token is real and was valid a moment ago. That is the whole point: a fabricated or
        // expired token would prove nothing about disabled users. AuthService re-reads the user on
        // every request and refuses a disabled one BEFORE any policy runs, so the rejection is 401
        // (authentication) rather than 403 (authorization).
        var factory = new RemoteAccessDiagnosticsApplicationFactory();
        try
        {
            using var client = factory.CreateClient();
            var token = await factory.AdminTokenAsync(client);
            client.DefaultRequestHeaders.AddAuthHeader(token);

            using var before = await client.PostAsync(Route, Body(), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
            var collectedWhileEnabled = factory.Addresses.CallCount;
            Assert.Equal(1, collectedWhileEnabled);

            var userManager = factory.Services.GetRequiredService<IUserManager>();
            var user = userManager.GetUsers().First();
            user.SetPermission(PermissionKind.IsDisabled, true);
            await userManager.UpdateUserAsync(user);

            using var after = await client.PostAsync(Route, Body(), TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
            // Unchanged: the disabled caller never reached collection.
            Assert.Equal(collectedWhileEnabled, factory.Addresses.CallCount);
        }
        finally
        {
            await factory.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task AdministratorWithoutCurrentElevationClaim_Is403()
    {
        // De-elevated, NOT disabled — a different boundary from the test above and it must produce
        // a different status. Authentication still succeeds (the token is valid and the user is
        // enabled), so CustomAuthenticationHandler issues a principal; it simply issues the `User`
        // role instead of `Administrator`, and RequiresElevation refuses it. 403, not 401.
        var factory = new RemoteAccessDiagnosticsApplicationFactory();
        try
        {
            using var client = factory.CreateClient();
            var token = await factory.AdminTokenAsync(client);
            client.DefaultRequestHeaders.AddAuthHeader(token);

            using var before = await client.PostAsync(Route, Body(), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
            var collectedWhileElevated = factory.Addresses.CallCount;

            var userManager = factory.Services.GetRequiredService<IUserManager>();
            var user = userManager.GetUsers().First();
            user.SetPermission(PermissionKind.IsAdministrator, false);
            user.SetPermission(PermissionKind.IsDisabled, false);
            await userManager.UpdateUserAsync(user);

            using var after = await client.PostAsync(Route, Body(), TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
            Assert.Equal(collectedWhileElevated, factory.Addresses.CallCount);

            // Authentication genuinely succeeded — the same token still reaches an endpoint that
            // only requires an authenticated user, which is what separates 403 from 401 here.
            using var stillAuthenticated = await client.GetAsync("/Users/Me", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, stillAuthenticated.StatusCode);
        }
        finally
        {
            await factory.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task FirstTimeSetupOrDefaultGrant_DoesNotReachTheDiagnostics()
    {
        // THE DISCRIMINATOR. A caller that genuinely satisfies a FirstTimeSetup* policy and just as
        // genuinely fails RequiresElevation, proven in that order against the real pipeline.
        //
        // WHY NOT THE ANONYMOUS PRE-ONBOARDING GRANT. FirstTimeSetupHandler only applies that grant
        // when the endpoint carries [FirstTimeSetupEndpoint] AND the request presented no token.
        // Diagnostics carries no such marking and must not acquire one, so an anonymous caller is
        // refused whichever FirstTimeSetup* policy the endpoint were given - which makes an
        // anonymous request unable to tell the two policies apart.
        //
        // A de-elevated user can. FirstTimeSetupRequirement(requireAdmin: false) - the requirement
        // behind Policies.FirstTimeSetupOrDefault - succeeds for any caller in the User role, while
        // RequiresElevation is a claims policy that demands the Administrator role outright.
        var factory = new RemoteAccessDiagnosticsApplicationFactory();
        try
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.AddAuthHeader(await factory.AdminTokenAsync(client));

            var userManager = factory.Services.GetRequiredService<IUserManager>();
            var user = userManager.GetUsers().First();
            user.SetPermission(PermissionKind.IsAdministrator, false);
            user.SetPermission(PermissionKind.IsDisabled, false);
            await userManager.UpdateUserAsync(user);

            // GRANT PROOF, FIRST AND SEPARATELY. LocalizationController is real production code
            // carrying [Authorize(Policy = Policies.FirstTimeSetupOrDefault)]; this caller answers
            // there, so the grant is live for it. The policy is not re-implemented here - the test
            // asks the real one.
            using var granted = await client.GetAsync("/Localization/Options", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, granted.StatusCode);

            var collectedBefore = factory.Addresses.CallCount;

            using var response = await client.PostAsync(Route, Body(), TestContext.Current.CancellationToken);

            // The same caller, the same host, one request apart: refused here, and never collected.
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(collectedBefore, factory.Addresses.CallCount);
            Assert.Equal(0, factory.Resolver.CallCount);
        }
        finally
        {
            await factory.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task InheritedApiKey_SatisfiesRequiresElevation()
    {
        // RECORDING INHERITED BEHAVIOUR, NOT ENDORSING IT. CustomAuthenticationHandler assigns the
        // Administrator role whenever AuthorizationContext resolves an API key, with no user and
        // therefore no disabled check. Every elevated endpoint in this server inherits that, and
        // R1-P does not redesign authentication — so the honest thing is a test that states what
        // actually happens. A test asserting a denial R1-P does not implement would be fiction.
        //
        // The key travels in the ordinary MediaBrowser authorization header. R1-P adds no query
        // credential and the operation declares no api_key parameter.
        var factory = new RemoteAccessDiagnosticsApplicationFactory();
        try
        {
            using var admin = factory.CreateClient();
            admin.DefaultRequestHeaders.AddAuthHeader(await factory.AdminTokenAsync(admin));

            using var created = await admin.PostAsync(
                "/Auth/Keys?app=r1p-diagnostics-test", content: null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);

            using var listed = await admin.GetAsync("/Auth/Keys", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            using var keys = JsonDocument.Parse(
                await listed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var key = keys.RootElement.GetProperty("Items").EnumerateArray()
                .First(i => i.GetProperty("AppName").GetString() == "r1p-diagnostics-test")
                .GetProperty("AccessToken").GetString();
            Assert.False(string.IsNullOrWhiteSpace(key));

            var before = factory.Addresses.CallCount;

            using var apiKeyClient = factory.CreateClient();
            apiKeyClient.DefaultRequestHeaders.AddAuthHeader(key!);

            using var response = await apiKeyClient.PostAsync(Route, Body(), TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(before + 1, factory.Addresses.CallCount);
            Assert.Contains(
                "no-store",
                string.Join(",", response.Headers.GetValues("Cache-Control")),
                StringComparison.Ordinal);

            var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            foreach (var unknown in new[]
                     {
                         "ExternalReachabilityUnverified", "FirewallStateUnknown",
                         "RouterMappingUnknown", "CertificateReadinessUnverified"
                     })
            {
                Assert.Contains(unknown, json, StringComparison.Ordinal);
            }
        }
        finally
        {
            await factory.DisposeAsync().ConfigureAwait(true);
        }
    }
}
