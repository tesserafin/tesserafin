using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// A software M3U tuner on loopback: <c>/playlist.m3u</c> names one channel, <c>/channel1.ts</c>
/// serves the fixture paced at its own bitrate with no <c>Content-Length</c>, which is what a real
/// tuner looks like to the server. Served all at once, the server's shared stream would reach the
/// end within milliseconds, treat the channel as finished and delete what it had just written.
/// </summary>
public sealed class SoftwareTuner : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly byte[] _body;
    private readonly int _bytesPerSecond;
    private readonly Task _loop;
    private int _openStreams;

    private SoftwareTuner(string fixturePath, int durationSeconds, int port)
    {
        _body = File.ReadAllBytes(fixturePath);
        _bytesPerSecond = Math.Max(1, _body.Length / durationSeconds);
        Port = port;
        _listener.Prefixes.Add(FormattableString.Invariant($"http://127.0.0.1:{port}/"));
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    /// <summary>Gets the loopback port the tuner listens on.</summary>
    public int Port { get; }

    /// <summary>Gets the playlist url to register as an M3U tuner host.</summary>
    public string PlaylistUrl => FormattableString.Invariant($"http://127.0.0.1:{Port}/playlist.m3u");

    /// <summary>Gets the number of channel streams currently being read from the tuner.</summary>
    public int OpenStreams => Volatile.Read(ref _openStreams);

    /// <summary>Gets the number of channel streams the tuner was asked for so far.</summary>
    public int StreamsOpened { get; private set; }

    /// <summary>
    /// Starts a tuner serving <paramref name="fixturePath"/> as its one channel.
    /// </summary>
    /// <param name="fixturePath">The MPEG-TS body of the channel.</param>
    /// <param name="durationSeconds">The fixture's duration, which sets the pace.</param>
    /// <returns>The running tuner.</returns>
    public static SoftwareTuner Start(string fixturePath, int durationSeconds)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return new SoftwareTuner(fixturePath, durationSeconds, port);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        _listener.Close();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
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
        var path = context.Request.Url?.AbsolutePath ?? string.Empty;
        try
        {
            if (path == "/playlist.m3u")
            {
                var playlist = Encoding.UTF8.GetBytes(FormattableString.Invariant(
                    $"#EXTM3U\n#EXTINF:-1 tvg-id=\"e2e-1\" tvg-name=\"E2E One\",E2E One\nhttp://127.0.0.1:{Port}/channel1.ts\n"));
                context.Response.ContentType = "audio/x-mpegurl";
                await context.Response.OutputStream.WriteAsync(playlist, _stop.Token).ConfigureAwait(false);
                context.Response.Close();
                return;
            }

            if (path != "/channel1.ts")
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            StreamsOpened++;
            Interlocked.Increment(ref _openStreams);
            try
            {
                context.Response.ContentType = "video/mp2t";
                context.Response.SendChunked = true;
                var chunk = Math.Max(188, _bytesPerSecond / 10 / 188 * 188);
                for (var offset = 0; offset < _body.Length && !_stop.IsCancellationRequested; offset += chunk)
                {
                    var length = Math.Min(chunk, _body.Length - offset);
                    await context.Response.OutputStream.WriteAsync(_body.AsMemory(offset, length), _stop.Token).ConfigureAwait(false);
                    await Task.Delay(100, _stop.Token).ConfigureAwait(false);
                }

                context.Response.Close();
            }
            finally
            {
                Interlocked.Decrement(ref _openStreams);
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // The reader went away (the server closed its tuner stream) or the tuner is stopping.
        }
    }
}
