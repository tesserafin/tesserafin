using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Tesserafin.Common.Configuration;
using Tesserafin.Common.Net;
using Tesserafin.Controller.Configuration;
using Xunit;

namespace Tesserafin.Api.Middleware.Tests;

/// <summary>
/// <see cref="BaseUrlRedirectionMiddleware"/> is the first thing in the pipeline, so the
/// <c>Location</c> it emits is reachable without authentication and before the rest of the
/// application is wired up. That makes the shape of that header a security property, not a
/// cosmetic one: a caller-selected absolute or scheme-relative target would be an open redirect
/// even though the server never follows the response itself.
///
/// The header is built by relativising a URI made from the request path against a URI made from
/// the operator-configured base path. These tests drive the middleware through a real Kestrel
/// listener with raw request lines — <see cref="System.Net.Http.HttpClient"/> normalises the
/// request target before it reaches the wire, which would quietly drop most of the interesting
/// cases — and pin that the emitted target is always a relative reference that resolves back onto
/// the request's own origin.
/// </summary>
public sealed class BaseUrlRedirectionMiddlewareTests
{
    private const string ForeignHost = "attacker.invalid";

    /// <summary>
    /// Request targets that a caller can put on the wire and that a naive implementation would
    /// echo into <c>Location</c> verbatim. Every one of them must come back as a relative
    /// reference pointing at the configured redirect path.
    /// </summary>
    /// <returns>The request targets to probe.</returns>
    public static TheoryData<string> HostChangingTargets() => new()
    {
        "http://" + ForeignHost + "/",
        "https://" + ForeignHost + "/",
        "//" + ForeignHost + "/path",
        "///" + ForeignHost + "/path",
        "/%2F%2F" + ForeignHost + "/path",
        "/%2f%2f" + ForeignHost + "/path",
        "/%252F%252F" + ForeignHost + "/path",
        "/\\" + ForeignHost + "/path",
        "/\\\\" + ForeignHost + "/path",
        "/%5C%5C" + ForeignHost + "/path",
        "//user@" + ForeignHost + "/path",
        "/https://user:pw@" + ForeignHost + "/path",
        "//127.0.0.1@" + ForeignHost + "/path",
        "//" + ForeignHost + "%2F@127.0.0.1/path",
        "/%0D%0ALocation:%20http://" + ForeignHost + "/",
        "/%0d%0aSet-Cookie:%20injected=1",
        "/%00/" + ForeignHost,
        "/..%2F..%2F" + ForeignHost,
    };

    /// <summary>
    /// Ordinary shapes the feature actually exists to serve.
    /// </summary>
    /// <returns>The request targets to probe.</returns>
    public static TheoryData<string> LocalTargets() => new()
    {
        "/",
        "/movies",
        "/a/b/c/d/e",
        "/movies?sort=name",
        // A literal '#' fragment is never sent by a browser. A hash in the path is encoded.
        "/movies%23anchor",
    };

    [Theory]
    [MemberData(nameof(HostChangingTargets))]
    public async Task Redirect_HostChangingRequestTarget_NeverLeavesTheRequestOrigin(string requestTarget)
    {
        await using var server = await RawServer.StartAsync(baseUrl: string.Empty);

        var response = await server.SendAsync("GET " + requestTarget + " HTTP/1.1");

        AssertNoInjectedHeader(response);
        if (response.StatusCode is null)
        {
            // Kestrel refused to parse the request line at all: the value never reaches the sink.
            return;
        }

        if (response.Location is null)
        {
            // Not a redirect (for example a 400 from the server's own request-line validation).
            Assert.NotEqual(302, response.StatusCode);
            return;
        }

        AssertStaysOnOrigin(response.Location, server.Port);
    }

    [Theory]
    [MemberData(nameof(HostChangingTargets))]
    public async Task Redirect_HostChangingRequestTarget_NeverLeavesTheRequestOriginUnderConfiguredBaseUrl(string requestTarget)
    {
        await using var server = await RawServer.StartAsync(baseUrl: "/tesserafin");

        var response = await server.SendAsync("GET " + requestTarget + " HTTP/1.1");

        AssertNoInjectedHeader(response);
        if (response.StatusCode is null || response.Location is null)
        {
            return;
        }

        AssertStaysOnOrigin(response.Location, server.Port);
    }

    [Theory]
    [MemberData(nameof(HostChangingTargets))]
    public async Task Redirect_ForeignHostHeader_NeverLeavesTheRequestOrigin(string requestTarget)
    {
        await using var server = await RawServer.StartAsync(baseUrl: string.Empty);

        var response = await server.SendAsync("GET " + requestTarget + " HTTP/1.1", hostHeader: ForeignHost);

        AssertNoInjectedHeader(response);
        if (response.StatusCode is null || response.Location is null)
        {
            return;
        }

        // The Host header decides both sides of the relativisation, so it cancels out and can
        // never appear on its own in the emitted target.
        Assert.DoesNotContain(ForeignHost, response.Location, StringComparison.OrdinalIgnoreCase);
        AssertStaysOnOrigin(response.Location, server.Port);
    }

    [Fact]
    public async Task Redirect_RootWithoutConfiguredBaseUrl_PointsAtTheConfiguredRedirectPath()
    {
        await using var server = await RawServer.StartAsync(baseUrl: string.Empty);

        var response = await server.SendAsync("GET / HTTP/1.1");

        Assert.Equal(302, response.StatusCode);
        Assert.NotNull(response.Location);
        AssertStaysOnOrigin(response.Location!, server.Port);
        Assert.Equal("/web/", Resolve(response.Location!, "/", server.Port).AbsolutePath);
    }

    [Theory]
    [InlineData("/movies")]
    [InlineData("/a/b/c/d/e")]
    [InlineData("/health")]
    [InlineData("/System/Info/Public")]
    public async Task Redirect_WithoutConfiguredBaseUrl_LeavesOrdinaryPathsAlone(string requestTarget)
    {
        await using var server = await RawServer.StartAsync(baseUrl: string.Empty);

        var response = await server.SendAsync("GET " + requestTarget + " HTTP/1.1");

        Assert.Equal(200, response.StatusCode);
        Assert.Null(response.Location);
    }

    [Theory]
    [MemberData(nameof(LocalTargets))]
    public async Task Redirect_OutsideConfiguredBaseUrl_PreservesTheConfiguredBaseUrl(string requestTarget)
    {
        await using var server = await RawServer.StartAsync(baseUrl: "/tesserafin");

        var response = await server.SendAsync("GET " + requestTarget + " HTTP/1.1");

        Assert.Equal(302, response.StatusCode);
        Assert.NotNull(response.Location);
        AssertStaysOnOrigin(response.Location!, server.Port);
        Assert.Equal("/tesserafin/web/", Resolve(response.Location!, requestTarget, server.Port).AbsolutePath);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/HEALTH/")]
    public async Task Redirect_HealthProbeOutsideConfiguredBaseUrl_KeepsTheProbeOnTheSameOrigin(string requestTarget)
    {
        await using var server = await RawServer.StartAsync(baseUrl: "/tesserafin");

        var response = await server.SendAsync("GET " + requestTarget + " HTTP/1.1");

        Assert.Equal(302, response.StatusCode);
        Assert.NotNull(response.Location);
        AssertStaysOnOrigin(response.Location!, server.Port);
        Assert.Equal("/tesserafin/health", Resolve(response.Location!, requestTarget, server.Port).AbsolutePath);
    }

    [Fact]
    public async Task Redirect_RequestInsideTheConfiguredBaseUrl_IsNotRedirectedAtAll()
    {
        await using var server = await RawServer.StartAsync(baseUrl: "/tesserafin");

        var response = await server.SendAsync("GET /tesserafin/web/index.html HTTP/1.1");

        Assert.Equal(200, response.StatusCode);
        Assert.Null(response.Location);
    }

    [Theory]
    [InlineData("GET  HTTP/1.1")]
    [InlineData("GET * HTTP/1.1")]
    [InlineData("GET ../../etc/passwd HTTP/1.1")]
    [InlineData("GET /movies#anchor HTTP/1.1")]
    [InlineData("GET / HTTP/9.9")]
    [InlineData("GET")]
    [InlineData("")]
    public async Task Redirect_MalformedRequestLine_NeverEmitsAForeignLocation(string requestLine)
    {
        await using var server = await RawServer.StartAsync(baseUrl: string.Empty);

        var response = await server.SendAsync(requestLine);

        AssertNoInjectedHeader(response);
        if (response.Location is not null)
        {
            AssertStaysOnOrigin(response.Location, server.Port);
        }
    }

    [Fact]
    public async Task Redirect_RequestWithoutHostHeader_StillEmitsARelativeTarget()
    {
        // Request.Host is the only other request-derived value feeding both UriBuilder calls. An
        // HTTP/1.0 request may legally omit Host, which leaves it empty on both sides.
        await using var server = await RawServer.StartAsync(baseUrl: "/tesserafin");

        var response = await server.SendRawAsync("GET /movies HTTP/1.0\r\n\r\n");

        AssertNoInjectedHeader(response);
        if (response.Location is not null)
        {
            AssertStaysOnOrigin(response.Location, server.Port);
            Assert.Equal("/tesserafin/web/", Resolve(response.Location, "/movies", server.Port).AbsolutePath);
        }
        else
        {
            Assert.NotEqual(302, response.StatusCode);
        }
    }

    [Fact]
    public async Task Redirect_LiteralCarriageReturnInRequestTarget_IsRejectedBeforeTheSink()
    {
        await using var server = await RawServer.StartAsync(baseUrl: string.Empty);

        // A literal CR/LF cannot survive the request line: either the parser refuses it or the
        // remainder is read as a separate (invalid) line. Either way no header is forged.
        var response = await server.SendRawAsync(
            "GET /a\r\nX-Injected: 1\r\n HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");

        Assert.DoesNotContain("X-Injected", response.Raw, StringComparison.OrdinalIgnoreCase);
        AssertNoInjectedHeader(response);
    }

    private static void AssertNoInjectedHeader(RawResponse response)
    {
        Assert.DoesNotContain("Set-Cookie", response.Raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("injected", response.Raw, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertStaysOnOrigin(string location, int port)
    {
        // A relative reference may not carry a scheme: RFC 3986 says its first segment must not
        // contain a colon before the first slash. (Uri.TryCreate with UriKind.Absolute is not the
        // right probe here — on Unix it accepts a bare "/path" as an absolute file URI.)
        Assert.False(
            HasScheme(location),
            $"Location '{location}' carries a scheme and can name any host.");
        Assert.False(
            location.StartsWith("//", StringComparison.Ordinal),
            $"Location '{location}' is scheme-relative and can name any host.");
        Assert.False(
            location.StartsWith('\\') || location.StartsWith("/\\", StringComparison.Ordinal),
            $"Location '{location}' uses a backslash form some clients read as scheme-relative.");
        Assert.DoesNotContain('\r', location);
        Assert.DoesNotContain('\n', location);

        var resolved = Resolve(location, "/", port);
        Assert.Equal("127.0.0.1", resolved.Host);
        Assert.Equal(port, resolved.Port);
    }

    private static bool HasScheme(string location)
    {
        var colon = location.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return false;
        }

        var slash = location.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0 && slash < colon)
        {
            return false;
        }

        return char.IsAsciiLetter(location[0])
               && location[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.');
    }

    private static Uri Resolve(string location, string requestTarget, int port)
    {
        var origin = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
        if (!Uri.TryCreate(origin + (requestTarget.StartsWith('/') ? requestTarget : "/"), UriKind.Absolute, out var requestUri))
        {
            requestUri = new Uri(origin + "/");
        }

        return new Uri(requestUri, location);
    }

    /// <summary>
    /// A real Kestrel listener running only the middleware under test, driven over a raw socket so
    /// that the request target reaches the server byte for byte.
    /// </summary>
    private sealed class RawServer : IAsyncDisposable
    {
        private readonly IHost _host;

        private RawServer(IHost host, int port)
        {
            _host = host;
            Port = port;
        }

        public int Port { get; }

        public static async Task<RawServer> StartAsync(string baseUrl)
        {
            var configurationManager = new Mock<IServerConfigurationManager>();
            configurationManager
                .Setup(x => x.GetConfiguration(NetworkConfigurationStore.StoreKey))
                .Returns(new NetworkConfiguration { BaseUrl = baseUrl });

            var host = new HostBuilder()
                .ConfigureWebHost(webBuilder => webBuilder
                    .UseKestrel(options => options.Listen(IPAddress.Loopback, 0))
                    .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(
                        new Dictionary<string, string?> { ["DefaultRedirectPath"] = "web/" }))
                    .ConfigureServices(services => services.AddSingleton(configurationManager.Object))
                    .Configure(app =>
                    {
                        app.UseMiddleware<BaseUrlRedirectionMiddleware>();
                        app.Run(context =>
                        {
                            context.Response.StatusCode = StatusCodes.Status200OK;
                            return Task.CompletedTask;
                        });
                    }))
                .Build();

            await host.StartAsync().ConfigureAwait(false);

            var address = host.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses
                .First();

            return new RawServer(host, new Uri(address).Port);
        }

        public Task<RawResponse> SendAsync(string requestLine, string? hostHeader = null)
            => SendRawAsync(
                requestLine + "\r\nHost: " + (hostHeader ?? "127.0.0.1") + "\r\nConnection: close\r\n\r\n");

        public async Task<RawResponse> SendRawAsync(string request)
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", Port).ConfigureAwait(false);

            using var stream = client.GetStream();
            var bytes = Encoding.ASCII.GetBytes(request);
            await stream.WriteAsync(bytes).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var buffer = new byte[8192];
            var received = new StringBuilder();
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellation.Token).ConfigureAwait(false)) > 0)
                {
                    received.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }
            }
            catch (OperationCanceledException)
            {
                // Server kept the connection open; whatever arrived is enough to inspect.
            }

            return RawResponse.Parse(received.ToString());
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync().ConfigureAwait(false);
            _host.Dispose();
        }
    }

    private sealed record RawResponse(int? StatusCode, string? Location, string Raw)
    {
        public static RawResponse Parse(string raw)
        {
            var lines = raw.Split("\r\n");
            int? status = null;
            if (lines.Length > 0)
            {
                var parts = lines[0].Split(' ');
                if (parts.Length > 1 && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var parsed))
                {
                    status = parsed;
                }
            }

            var location = lines
                .Skip(1)
                .TakeWhile(line => line.Length > 0)
                .FirstOrDefault(line => line.StartsWith("Location:", StringComparison.OrdinalIgnoreCase))
                ?["Location:".Length..]
                .Trim();

            return new RawResponse(status, location, raw);
        }
    }
}
