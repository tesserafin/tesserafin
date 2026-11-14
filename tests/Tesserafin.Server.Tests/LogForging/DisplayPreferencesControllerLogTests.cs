using System;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Tesserafin.Api.Constants;
using Tesserafin.Api.Controllers;
using Tesserafin.Controller;
using Tesserafin.Data.Entities;
using Tesserafin.Model.Dto;
using Xunit;

namespace Tesserafin.Server.Tests.LogForging
{
    /// <summary>
    /// <see cref="DisplayPreferencesController"/> logs the rejected <c>landing-*</c> view type
    /// straight from the request body. Nothing parses, encodes or validates that value before the
    /// logger sees it — the enum parse that rejects it happens after — so this is one of the two
    /// statements in this tranche where a caller really can end the physical record.
    /// </summary>
    /// <remarks>
    /// The endpoint carries <see cref="AuthorizeAttribute"/>, so the caller must hold a session;
    /// it is not an unauthenticated surface, and that is stated rather than glossed. What makes it
    /// part of this tranche is the shape of the value, not the strength of the gate.
    /// </remarks>
    public sealed class DisplayPreferencesControllerLogTests
    {
        private const string Client = "test-client";

        [Fact]
        public void Controller_StillRequiresAuthorization()
        {
            // The tranche changes a logging argument; it must not have relaxed the gate.
            Assert.NotEmpty(typeof(DisplayPreferencesController)
                .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
                .ToArray());
        }

        [Theory]
        [InlineData("Movies\r\n" + RealFormatterLogProbe.ForgedRecordPrefix)]
        [InlineData("Mov\ries")]
        [InlineData("Mov\nies")]
        public void UpdateDisplayPreferences_HostileViewType_WritesExactlyOnePhysicalRecord(string viewType)
        {
            using var probe = RealFormatterLogProbe.Text();

            Update(probe, viewType);

            Assert.Single(probe.Lines());
            Assert.Equal(1, probe.TextRecordCount());
            Assert.DoesNotContain('\r', probe.Raw);
        }

        [Theory]
        [InlineData("Movies\r")]
        [InlineData("Movies\n")]
        [InlineData("Movies\r\n")]
        public void UpdateDisplayPreferences_TrailingSeparatorOnly_IsAcceptedByTheEnumParseAndNeverLogged(string viewType)
        {
            using var probe = RealFormatterLogProbe.Text();

            // Recorded rather than assumed: Enum.TryParse trims trailing whitespace, so these
            // shapes are valid view types and the logging statement is not reached at all.
            var dto = Update(probe, viewType);

            Assert.Equal(string.Empty, probe.Raw);
            Assert.True(dto.CustomPrefs.ContainsKey("landing-1"));
        }

        [Fact]
        public void UpdateDisplayPreferences_ForgedRecordPrefix_StaysInsideTheRealRecord()
        {
            using var probe = RealFormatterLogProbe.Text();

            Update(probe, "Movies\r\n" + RealFormatterLogProbe.ForgedRecordPrefix);

            var record = Assert.Single(probe.Lines());

            // One prefix, and it is the server's own: the forged "[12:00:00.000] [ERR]" is now text
            // inside the message.
            Assert.Contains("[ERR] [1] Tesserafin.Api.Controllers.DisplayPreferencesController:", record, StringComparison.Ordinal);
            Assert.Contains("Invalid ViewType: Movies\\r\\n[12:00:00.000] [ERR]", record, StringComparison.Ordinal);
            Assert.Contains("administrator account deleted by mallory", record, StringComparison.Ordinal);
        }

        [Fact]
        public void UpdateDisplayPreferences_OrdinaryInvalidViewType_LogsTheSameLineItLoggedBefore()
        {
            using var probe = RealFormatterLogProbe.Text();

            Update(probe, "NotAViewType");

            var record = Assert.Single(probe.Lines());
            Assert.EndsWith("Invalid ViewType: NotAViewType", record, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("Movies")]
        [InlineData("movies")]
        [InlineData("Books")]
        public void UpdateDisplayPreferences_ValidViewType_IsKeptAndNotLogged(string viewType)
        {
            using var probe = RealFormatterLogProbe.Text();

            var dto = Update(probe, viewType);

            Assert.Equal(string.Empty, probe.Raw);
            Assert.True(dto.CustomPrefs.ContainsKey("landing-1"));
        }

        [Fact]
        public void UpdateDisplayPreferences_RejectedViewType_IsStillRemovedFromCustomPrefs()
        {
            using var probe = RealFormatterLogProbe.Text();

            var dto = Update(probe, "Movies\r\n" + RealFormatterLogProbe.ForgedRecordPrefix);

            // Existing behaviour: the invalid entry does not survive into stored preferences.
            Assert.False(dto.CustomPrefs.ContainsKey("landing-1"));
        }

        private static DisplayPreferencesDto Update(RealFormatterLogProbe probe, string viewType)
        {
            var userId = Guid.NewGuid();
            var itemId = Guid.NewGuid();

            var manager = new Mock<IDisplayPreferencesManager>();
            manager
                .Setup(x => x.GetDisplayPreferences(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()))
                .Returns(new DisplayPreferences(userId, itemId, Client));
            manager
                .Setup(x => x.GetItemDisplayPreferences(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()))
                .Returns(new ItemDisplayPreferences(userId, itemId, Client));

            var controller = new DisplayPreferencesController(
                manager.Object,
                probe.LoggerFor<DisplayPreferencesController>())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(
                            new[] { new Claim(InternalClaimTypes.UserId, userId.ToString("N")) },
                            authenticationType: "Test"))
                    }
                }
            };

            var dto = new DisplayPreferencesDto();
            dto.CustomPrefs["landing-1"] = viewType;

            var result = controller.UpdateDisplayPreferences(itemId.ToString("N"), userId, Client, dto);

            Assert.IsType<NoContentResult>(result);
            return dto;
        }
    }
}
