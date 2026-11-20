using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// One file served over plain HTTP on loopback, with byte ranges: a remote source (a <c>.strm</c>
/// target, a channel-plugin item) the session journey does not read from a local path.
/// </summary>
public sealed class StaticHttpFile : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly byte[] _body;
    private readonly Task _loop;

    private StaticHttpFile(string path, int port)
    {
        _body = File.ReadAllBytes(path);
        Url = FormattableString.Invariant($"http://127.0.0.1:{port}/{Path.GetFileName(path)}");
        _listener.Prefixes.Add(FormattableString.Invariant($"http://127.0.0.1:{port}/"));
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    /// <summary>Gets the url the file is served at.</summary>
    public string Url { get; }

    /// <summary>
    /// Serves <paramref name="path"/> on a free loopback port.
    /// </summary>
    /// <param name="path">The file to serve.</param>
    /// <returns>The running server.</returns>
    public static StaticHttpFile Start(string path)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return new StaticHttpFile(path, port);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        _listener.Close();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
        }
    }

    private async Task AcceptAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(context));
        }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        try
        {
            var from = 0L;
            var to = _body.Length - 1L;
            var range = context.Request.Headers["Range"];
            if (range is not null && range.StartsWith("bytes=", StringComparison.Ordinal))
            {
                var parts = range["bytes=".Length..].Split('-');
                from = long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                if (parts.Length > 1 && parts[1].Length > 0)
                {
                    to = Math.Min(to, long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
                }

                context.Response.StatusCode = 206;
                context.Response.AddHeader("Content-Range", FormattableString.Invariant($"bytes {from}-{to}/{_body.Length}"));
            }

            context.Response.AddHeader("Accept-Ranges", "bytes");
            context.Response.ContentType = "video/mp4";
            context.Response.ContentLength64 = to - from + 1;
            if (context.Request.HttpMethod != "HEAD")
            {
                await context.Response.OutputStream.WriteAsync(_body.AsMemory((int)from, (int)(to - from + 1))).ConfigureAwait(false);
            }

            context.Response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The reader went away.
        }
    }
}
