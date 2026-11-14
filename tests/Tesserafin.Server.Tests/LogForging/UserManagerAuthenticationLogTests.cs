using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Common;
using Tesserafin.Common.Net;
using Tesserafin.Controller;
using Tesserafin.Controller.Authentication;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Drawing;
using Tesserafin.Controller.Events;
using Tesserafin.Controller.Net;
using Tesserafin.Data;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Database.Implementations;
using Tesserafin.Database.Implementations.Locking;
using Tesserafin.Database.Providers.Sqlite;
using Tesserafin.Model.Cryptography;
using Tesserafin.Server.Implementations.Users;
using Xunit;

namespace Tesserafin.Server.Tests.LogForging
{
    /// <summary>
    /// The three remaining pre-authentication statements in
    /// <see cref="UserManager.AuthenticateUser(string, string, string, bool)"/> log the submitted
    /// user name. These tests drive the real authentication path — a real SQLite database, the real
    /// default authentication provider, the real lock and query code — until each of those three
    /// statements is the one that runs, and assert on the bytes the shipped text formatter wrote.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reachability, recorded rather than assumed: all three statements run only after
    /// <c>GetUserByName</c> has matched a stored user, so the submitted name must already equal a
    /// stored one. <c>ThrowIfInvalidUsername</c> rejects an embedded <c>CR</c> or <c>LF</c>, so an
    /// account whose name contains one in the middle cannot be created through the API; a name with
    /// a trailing <c>LF</c> is accepted, because <c>$</c> in the validating expression matches
    /// before a final newline. The fixtures below therefore write the hostile name straight into the
    /// database: what is under test is what the logger does with a stored name, not how it got
    /// there.
    /// </para>
    /// <para>
    /// The name is the only untrusted string on these statements. <c>remoteEndPoint</c> is
    /// <c>IPAddress.ToString()</c> at both call sites into this method and cannot carry a separator;
    /// it is left exactly as it was, and pinned here so that stays true.
    /// </para>
    /// </remarks>
    public sealed class UserManagerAuthenticationLogTests : IDisposable
    {
        private const string OrdinaryName = "alice";
        private const string RemoteEndPoint = "203.0.113.5";

        // The stored value is null in these fixtures, so any non-empty input fails the default
        // provider. Deliberately not credential-shaped: nothing here is a secret, and nothing here
        // should read as one to a scanner or a human.
        private const string NonMatchingInput = "mismatch";

        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<TesserafinDbContext> _dbOptions;
        private readonly Mock<INetworkManager> _networkManager = new();

        public UserManagerAuthenticationLogTests()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();

            _dbOptions = new DbContextOptionsBuilder<TesserafinDbContext>()
                .UseSqlite(_connection)
                .Options;

            using var context = CreateDbContext();
            context.Database.EnsureCreated();

            _networkManager.Setup(x => x.IsInLocalNetwork(It.IsAny<string>())).Returns(false);
        }

        public static TheoryData<string> HostileNames() => new()
        {
            OrdinaryName + "\r\n" + RealFormatterLogProbe.ForgedRecordPrefix,
            OrdinaryName + "\r",
            OrdinaryName + "\n",
            "ali\rce",
            "ali\nce",
        };

        public void Dispose() => _connection.Dispose();

        [Theory]
        [MemberData(nameof(HostileNames))]
        public async Task DisabledAccount_HostileStoredName_WritesExactlyOnePhysicalRecord(string username)
        {
            using var probe = RealFormatterLogProbe.Text();
            using var userManager = CreateUserManager(probe);
            await SeedAsync(userManager, username, user => user.SetPermission(PermissionKind.IsDisabled, true));

            await Assert.ThrowsAsync<SecurityException>(
                () => userManager.AuthenticateUser(username, NonMatchingInput, RemoteEndPoint, true));

            AssertSingleRecord(probe, "has been denied because this account is currently disabled");
        }

        [Theory]
        [MemberData(nameof(HostileNames))]
        public async Task RemoteAccessDisabled_HostileStoredName_WritesExactlyOnePhysicalRecord(string username)
        {
            using var probe = RealFormatterLogProbe.Text();
            using var userManager = CreateUserManager(probe);
            await SeedAsync(
                userManager,
                username,
                user =>
                {
                    user.SetPermission(PermissionKind.IsDisabled, false);
                    user.SetPermission(PermissionKind.EnableRemoteAccess, false);
                });

            await Assert.ThrowsAsync<SecurityException>(
                () => userManager.AuthenticateUser(username, NonMatchingInput, RemoteEndPoint, true));

            AssertSingleRecord(probe, "remote access disabled and user not in local network");
        }

        [Theory]
        [MemberData(nameof(HostileNames))]
        public async Task ParentalScheduleDenied_HostileStoredName_WritesExactlyOnePhysicalRecord(string username)
        {
            using var probe = RealFormatterLogProbe.Text();
            using var userManager = CreateUserManager(probe);
            await SeedAsync(
                userManager,
                username,
                user =>
                {
                    user.SetPermission(PermissionKind.IsDisabled, false);
                    user.SetPermission(PermissionKind.EnableRemoteAccess, true);
                    user.AccessSchedules.Add(new AccessSchedule(NotToday(), 0, 24, user.Id));
                });

            await Assert.ThrowsAsync<SecurityException>(
                () => userManager.AuthenticateUser(username, NonMatchingInput, RemoteEndPoint, true));

            AssertSingleRecord(probe, "not allowed at this time due parental restrictions");
        }

        [Fact]
        public async Task DisabledAccount_ForgedRecordPrefix_StaysInsideTheRealRecord()
        {
            const string Hostile = OrdinaryName + "\r\n" + RealFormatterLogProbe.ForgedRecordPrefix;

            using var probe = RealFormatterLogProbe.Text();
            using var userManager = CreateUserManager(probe);
            await SeedAsync(userManager, Hostile, user => user.SetPermission(PermissionKind.IsDisabled, true));

            await Assert.ThrowsAsync<SecurityException>(
                () => userManager.AuthenticateUser(Hostile, NonMatchingInput, RemoteEndPoint, true));

            var record = Assert.Single(probe.Lines());
            Assert.Contains("[INF] [1] Tesserafin.Server.Implementations.Users.UserManager:", record, StringComparison.Ordinal);
            Assert.Contains("alice\\r\\n[12:00:00.000] [ERR]", record, StringComparison.Ordinal);
            Assert.Contains("administrator account deleted by mallory", record, StringComparison.Ordinal);
            Assert.EndsWith("(IP: 203.0.113.5).", record, StringComparison.Ordinal);
        }

        [Fact]
        public async Task DisabledAccount_OrdinaryName_LogsTheSameLineItLoggedBefore()
        {
            using var probe = RealFormatterLogProbe.Text();
            using var userManager = CreateUserManager(probe);
            await SeedAsync(userManager, OrdinaryName, user => user.SetPermission(PermissionKind.IsDisabled, true));

            await Assert.ThrowsAsync<SecurityException>(
                () => userManager.AuthenticateUser(OrdinaryName, NonMatchingInput, RemoteEndPoint, true));

            var record = Assert.Single(probe.Lines());
            Assert.EndsWith(
                "Authentication request for alice has been denied because this account is currently disabled (IP: 203.0.113.5).",
                record,
                StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("")]
        [InlineData("::1")]
        [InlineData("192.168.1.10")]
        public async Task DisabledAccount_EndpointValues_AreRenderedVerbatim(string remoteEndPoint)
        {
            using var probe = RealFormatterLogProbe.Text();
            using var userManager = CreateUserManager(probe);
            await SeedAsync(userManager, OrdinaryName, user => user.SetPermission(PermissionKind.IsDisabled, true));

            await Assert.ThrowsAsync<SecurityException>(
                () => userManager.AuthenticateUser(OrdinaryName, NonMatchingInput, remoteEndPoint, true));

            // The endpoint argument is untouched by this tranche; it must still print as it did.
            var record = Assert.Single(probe.Lines());
            Assert.EndsWith("(IP: " + remoteEndPoint + ").", record, StringComparison.Ordinal);
        }

        [Fact]
        public async Task UnknownName_StillTakesTheAlreadyFlattenedDeniedPath()
        {
            const string Hostile = "nobody\r\n" + RealFormatterLogProbe.ForgedRecordPrefix;

            using var probe = RealFormatterLogProbe.Text();
            using var userManager = CreateUserManager(probe);
            await SeedAsync(userManager, OrdinaryName, _ => { });

            // No stored user matches, so the flow ends at the statement an earlier tranche already
            // flattened. Pinned so this tranche cannot be read as having introduced that guarantee.
            await Assert.ThrowsAsync<AuthenticationException>(
                () => userManager.AuthenticateUser(Hostile, NonMatchingInput, RemoteEndPoint, true));

            AssertSingleRecord(probe, "has been denied (IP:");
        }

        private static void AssertSingleRecord(RealFormatterLogProbe probe, string expectedFragment)
        {
            // One physical line, not merely one record prefix: a bare LF would split the record
            // in two even though the second half carries no forged timestamp of its own.
            Assert.Single(probe.Lines());
            Assert.Equal(1, probe.TextRecordCount());
            Assert.DoesNotContain('\r', probe.Raw);
            Assert.Contains(expectedFragment, probe.Raw, StringComparison.Ordinal);
        }

        private static DynamicDayOfWeek NotToday()
            // Three days away rather than one, so a run that crosses local midnight between seeding
            // and asserting still lands on a day the schedule does not cover.
            => (DynamicDayOfWeek)(((int)DateTime.Now.DayOfWeek + 3) % 7);

        private TesserafinDbContext CreateDbContext()
            => new(
                _dbOptions,
                NullLogger<TesserafinDbContext>.Instance,
                new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
                new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));

        private async Task SeedAsync(UserManager userManager, string username, Action<User> configure)
        {
            var created = await userManager.CreateUserAsync(OrdinaryName);

            using var context = CreateDbContext();
            var user = context.Users
                .Include(u => u.Permissions)
                .Include(u => u.Preferences)
                .Include(u => u.AccessSchedules)
                .First(u => u.Id.Equals(created.Id));

            // Written straight to the row: ThrowIfInvalidUsername would refuse most of these, and
            // what is under test is the logger, not the create path.
            user.Username = username;
            user.NormalizedUsername = username.ToUpperInvariant();
            configure(user);

            await context.SaveChangesAsync();
        }

        private UserManager CreateUserManager(RealFormatterLogProbe probe)
        {
            var factory = new Mock<IDbContextFactory<TesserafinDbContext>>();
            factory.Setup(f => f.CreateDbContext()).Returns(CreateDbContext);
            factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateDbContext);

            var configManager = new Mock<IServerConfigurationManager>();
            var appPaths = new Mock<IServerApplicationPaths>();
            appPaths.Setup(x => x.ProgramDataPath).Returns(Path.GetTempPath());
            configManager.Setup(x => x.ApplicationPaths).Returns(appPaths.Object);

            var appHost = new Mock<IApplicationHost>();

            var defaultAuthProvider = new DefaultAuthenticationProvider(
                NullLogger<DefaultAuthenticationProvider>.Instance,
                new Mock<ICryptoProvider>().Object);
            var defaultPasswordResetProvider = new DefaultPasswordResetProvider(
                configManager.Object,
                appHost.Object);

            return new UserManager(
                factory.Object,
                new NoopEventManager(),
                _networkManager.Object,
                appHost.Object,
                new Mock<IImageProcessor>().Object,
                probe.LoggerFor<UserManager>(),
                configManager.Object,
                new IPasswordResetProvider[] { defaultPasswordResetProvider },
                new IAuthenticationProvider[] { defaultAuthProvider, new InvalidAuthProvider() });
        }

        private sealed class NoopEventManager : IEventManager
        {
            public void Publish<T>(T eventArgs)
                where T : EventArgs
            {
            }

            public Task PublishAsync<T>(T eventArgs)
                where T : EventArgs
                => Task.CompletedTask;
        }
    }
}
