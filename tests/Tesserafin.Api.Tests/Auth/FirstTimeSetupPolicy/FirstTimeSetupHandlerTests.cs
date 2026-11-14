using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using AutoFixture;
using AutoFixture.AutoMoq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Tesserafin.Api.Auth.DefaultAuthorizationPolicy;
using Tesserafin.Api.Auth.FirstTimeSetupPolicy;
using Tesserafin.Api.Constants;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Net;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Xunit;

namespace Tesserafin.Api.Tests.Auth.FirstTimeSetupPolicy
{
    public class FirstTimeSetupHandlerTests
    {
        private readonly Mock<IConfigurationManager> _configurationManagerMock;
        private readonly List<IAuthorizationRequirement> _requirements;
        private readonly DefaultAuthorizationHandler _defaultAuthorizationHandler;
        private readonly FirstTimeSetupHandler _firstTimeSetupHandler;
        private readonly IAuthorizationService _authorizationService;
        private readonly Mock<IUserManager> _userManagerMock;
        private readonly Mock<IHttpContextAccessor> _httpContextAccessor;
        private readonly Mock<IAuthorizationContext> _authorizationContextMock;

        public FirstTimeSetupHandlerTests()
        {
            var fixture = new Fixture().Customize(new AutoMoqCustomization());
            _configurationManagerMock = fixture.Freeze<Mock<IConfigurationManager>>();
            _requirements = new List<IAuthorizationRequirement> { new FirstTimeSetupRequirement() };
            _userManagerMock = fixture.Freeze<Mock<IUserManager>>();
            _httpContextAccessor = fixture.Freeze<Mock<IHttpContextAccessor>>();
            _authorizationContextMock = fixture.Freeze<Mock<IAuthorizationContext>>();

            _firstTimeSetupHandler = fixture.Create<FirstTimeSetupHandler>();
            _defaultAuthorizationHandler = fixture.Create<DefaultAuthorizationHandler>();

            var services = new ServiceCollection();
            services.AddAuthorizationCore();
            services.AddLogging();
            services.AddOptions();
            services.AddSingleton<IAuthorizationHandler>(_defaultAuthorizationHandler);
            services.AddSingleton<IAuthorizationHandler>(_firstTimeSetupHandler);
            services.AddAuthorization(options =>
            {
                options.AddPolicy("FirstTime", policy => policy.Requirements.Add(new FirstTimeSetupRequirement()));
                options.AddPolicy("FirstTimeNoAdmin", policy => policy.Requirements.Add(new FirstTimeSetupRequirement(false, false)));
                options.AddPolicy("FirstTimeSchedule", policy => policy.Requirements.Add(new FirstTimeSetupRequirement(true, false)));
            });
            _authorizationService = services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
        }

        // A clean pre-onboarding server must still permit the real wizard flow: the shipped wizard
        // presents no token at all and calls endpoints on the onboarding surface.
        [Fact]
        public async Task PreOnboarding_OnboardingEndpoint_NoToken_Succeeds()
        {
            TestHelpers.SetupConfigurationManager(_configurationManagerMock, false);
            SetupRequest(isOnboardingEndpoint: true, token: null);

            var allowed = await _authorizationService.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), "FirstTime");

            Assert.True(allowed.Succeeded);
        }

        // An endpoint that carries a first-time-setup policy for the sake of its "or elevated" half
        // must not inherit anonymous pre-onboarding access.
        [Fact]
        public async Task PreOnboarding_UnmarkedEndpoint_NoToken_IsRejected()
        {
            TestHelpers.SetupConfigurationManager(_configurationManagerMock, false);
            SetupRequest(isOnboardingEndpoint: false, token: null);

            var allowed = await _authorizationService.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), "FirstTime");

            Assert.False(allowed.Succeeded);
        }

        // A caller that presents a token is judged on that token, never on the setup window. A
        // malformed or invalid token leaves an unauthenticated principal, which must not be
        // promoted to administrator by the pre-onboarding branch.
        [Theory]
        [InlineData("not-a-real-token")]
        [InlineData("MediaBrowser Token=\"garbage\"")]
        public async Task PreOnboarding_InvalidTokenPresented_IsRejected(string token)
        {
            TestHelpers.SetupConfigurationManager(_configurationManagerMock, false);
            SetupRequest(isOnboardingEndpoint: true, token: token);

            var allowed = await _authorizationService.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), "FirstTime");

            Assert.False(allowed.Succeeded);
        }

        // An ordinary authenticated user is denied a privileged operation during the pre-onboarding
        // window, exactly as after onboarding.
        [Fact]
        public async Task PreOnboarding_OrdinaryUserToken_IsRejected()
        {
            TestHelpers.SetupConfigurationManager(_configurationManagerMock, false);
            var claims = TestHelpers.SetupUser(_userManagerMock, _httpContextAccessor, UserRoles.User);
            SetupRequest(isOnboardingEndpoint: true, token: "a-valid-user-token");

            var allowed = await _authorizationService.AuthorizeAsync(claims, "FirstTime");

            Assert.False(allowed.Succeeded);
        }

        // An administrator keeps access through the role branch, with or without the setup window.
        [Fact]
        public async Task PreOnboarding_AdministratorToken_Succeeds()
        {
            TestHelpers.SetupConfigurationManager(_configurationManagerMock, false);
            var claims = TestHelpers.SetupUser(_userManagerMock, _httpContextAccessor, UserRoles.Administrator);
            SetupRequest(isOnboardingEndpoint: true, token: "a-valid-admin-token");

            var allowed = await _authorizationService.AuthorizeAsync(claims, "FirstTime");

            Assert.True(allowed.Succeeded);
        }

        // The setup window is derived from configuration that is read per request, so a restart
        // while onboarding is incomplete preserves exactly the same restricted surface.
        [Fact]
        public async Task RestartBeforeCompletion_PreservesRestrictedSurface()
        {
            TestHelpers.SetupConfigurationManager(_configurationManagerMock, false);

            SetupRequest(isOnboardingEndpoint: false, token: null);
            Assert.False((await _authorizationService.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), "FirstTime")).Succeeded);

            // Same server, later request: the surface is unchanged.
            SetupRequest(isOnboardingEndpoint: false, token: null);
            Assert.False((await _authorizationService.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), "FirstTime")).Succeeded);

            SetupRequest(isOnboardingEndpoint: true, token: null);
            Assert.True((await _authorizationService.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), "FirstTime")).Succeeded);
        }

        // Once onboarding is complete the setup grant is gone, including on the onboarding surface
        // itself, and it does not come back on restart.
        [Fact]
        public async Task RestartAfterCompletion_DoesNotReopenSetupAuthorization()
        {
            TestHelpers.SetupConfigurationManager(_configurationManagerMock, true);
            SetupRequest(isOnboardingEndpoint: true, token: null);

            var allowed = await _authorizationService.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), "FirstTime");

            Assert.False(allowed.Succeeded);
        }

        [Theory]
        [InlineData(UserRoles.Administrator, true)]
        [InlineData(UserRoles.Guest, false)]
        [InlineData(UserRoles.User, false)]
        public async Task ShouldRequireAdministratorIfStartupWizardComplete(string userRole, bool shouldSucceed)
        {
            TestHelpers.SetupConfigurationManager(_configurationManagerMock, true);
            var claims = TestHelpers.SetupUser(
                _userManagerMock,
                _httpContextAccessor,
                userRole);

            var allowed = await _authorizationService.AuthorizeAsync(claims, "FirstTime");

            Assert.Equal(shouldSucceed, allowed.Succeeded);
        }

        [Theory]
        [InlineData(UserRoles.Administrator, true)]
        [InlineData(UserRoles.Guest, false)]
        [InlineData(UserRoles.User, true)]
        public async Task ShouldRequireUserIfNotAdministrator(string userRole, bool shouldSucceed)
        {
            TestHelpers.SetupConfigurationManager(_configurationManagerMock, true);
            var claims = TestHelpers.SetupUser(
                _userManagerMock,
                _httpContextAccessor,
                userRole);

            var allowed = await _authorizationService.AuthorizeAsync(claims, "FirstTimeNoAdmin");

            Assert.Equal(shouldSucceed, allowed.Succeeded);
        }

        [Fact]
        public async Task ShouldDisallowUserIfOutsideSchedule()
        {
            AccessSchedule[] accessSchedules = { new AccessSchedule(DynamicDayOfWeek.Everyday, 0, 0, Guid.Empty) };

            TestHelpers.SetupConfigurationManager(_configurationManagerMock, true);
            var claims = TestHelpers.SetupUser(
                _userManagerMock,
                _httpContextAccessor,
                UserRoles.User,
                accessSchedules);

            var allowed = await _authorizationService.AuthorizeAsync(claims, "FirstTimeSchedule");

            Assert.False(allowed.Succeeded);
        }

        // Points the handler at a request with, or without, the onboarding marker, and with or
        // without a presented token.
        private void SetupRequest(bool isOnboardingEndpoint, string? token)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Connection.RemoteIpAddress = new IPAddress(0);

            var metadata = isOnboardingEndpoint
                ? new EndpointMetadataCollection(new FirstTimeSetupEndpointAttribute())
                : EndpointMetadataCollection.Empty;
            httpContext.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, "test"));

            _httpContextAccessor.Setup(h => h.HttpContext).Returns(httpContext);

            _authorizationContextMock
                .Setup(a => a.GetAuthorizationInfo(It.IsAny<HttpContext>()))
                .ReturnsAsync(new AuthorizationInfo { Token = token });
        }
    }
}
