using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Moq;
using Tesserafin.Api.Constants;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Library;
using Tesserafin.Data;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Configuration;
using Tesserafin.Server.Implementations.Users;
using AccessSchedule = Tesserafin.Data.Entities.AccessSchedule;

namespace Tesserafin.Api.Tests
{
    public static class TestHelpers
    {
        /// <summary>
        /// A user manager in which every id is one ordinary user, with the default permissions.
        /// </summary>
        /// <returns>The user manager.</returns>
        public static IUserManager AnyUser()
        {
            var user = new User("caller", typeof(DefaultAuthenticationProvider).FullName!, typeof(DefaultPasswordResetProvider).FullName!);
            user.AddDefaultPermissions();
            user.AddDefaultPreferences();
            return Mock.Of<IUserManager>(m => m.GetUserById(It.IsAny<Guid>()) == user);
        }

        public static ClaimsPrincipal SetupUser(
            Mock<IUserManager> userManagerMock,
            Mock<IHttpContextAccessor> httpContextAccessorMock,
            string role,
            IEnumerable<AccessSchedule>? accessSchedules = null)
        {
            var user = new User(
                "tesserafin",
                typeof(DefaultAuthenticationProvider).FullName!,
                typeof(DefaultPasswordResetProvider).FullName!);

            user.AddDefaultPermissions();
            user.AddDefaultPreferences();

            // Set administrator flag.
            user.SetPermission(PermissionKind.IsAdministrator, role.Equals(UserRoles.Administrator, StringComparison.OrdinalIgnoreCase));

            // Add access schedules if set.
            if (accessSchedules is not null)
            {
                foreach (var accessSchedule in accessSchedules)
                {
                    user.AccessSchedules.Add(accessSchedule);
                }
            }

            var claims = new[]
            {
                new Claim(ClaimTypes.Role, role),
                new Claim(ClaimTypes.Name, "tesserafin"),
                new Claim(InternalClaimTypes.UserId, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                new Claim(InternalClaimTypes.DeviceId, Guid.Empty.ToString("N", CultureInfo.InvariantCulture)),
                new Claim(InternalClaimTypes.Device, "test"),
                new Claim(InternalClaimTypes.Client, "test"),
                new Claim(InternalClaimTypes.Version, "test"),
                new Claim(InternalClaimTypes.Token, "test"),
            };

            var identity = new ClaimsIdentity(claims);

            userManagerMock
                .Setup(u => u.GetUserById(It.IsAny<Guid>()))
                .Returns(user);

            httpContextAccessorMock
                .Setup(h => h.HttpContext!.Connection.RemoteIpAddress)
                .Returns(new IPAddress(0));

            return new ClaimsPrincipal(identity);
        }

        public static void SetupConfigurationManager(in Mock<IConfigurationManager> configurationManagerMock, bool startupWizardCompleted)
        {
            var commonConfiguration = new BaseApplicationConfiguration
            {
                IsStartupWizardCompleted = startupWizardCompleted
            };

            configurationManagerMock
                .Setup(c => c.CommonConfiguration)
                .Returns(commonConfiguration);
        }
    }
}
