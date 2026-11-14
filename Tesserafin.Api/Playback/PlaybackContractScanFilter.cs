using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tesserafin.Controller.Configuration;
using Tesserafin.Model.Configuration;
using Tesserafin.Playback.Contract.Diagnostics;
using Tesserafin.Playback.Contract.Scan;
using Tesserafin.Playback.Shadow;

namespace Tesserafin.Api.Playback;

/// <summary>
/// Issue #75 slice 75b: the resource filter that runs the bounded structural scan of the raw
/// playback request body - strictly BEFORE model binding, and strictly behind the EXISTING shadow
/// gate and sampling.
/// </summary>
/// <remarks>
/// <para>
/// A resource filter runs before model binding, which is the only place the raw body is still
/// untouched, and the only place a KNOWN member's original JSON token kind (for the WrongType
/// signal) is still visible. The result is stashed in <see cref="HttpContext.Items"/> under
/// <see cref="ScanResultKey"/>; <c>PlaybackSessionsController</c> reads it back and hands it to the
/// same ambient capture scope the shadow run already reads its other request-scoped facts from.
/// </para>
/// <para>
/// THREE GUARANTEES this filter is written around:
/// <list type="number">
/// <item><description>
/// Shadow OFF changes nothing. When the effective mode is <see cref="PlaybackEngineMode.Legacy"/>
/// (the default) the filter returns before touching the request: no <see cref="HttpRequestRewindExtensions.EnableBuffering(HttpRequest)"/>,
/// no read, no allocation. Body handling is byte-for-byte what it was before this filter existed.
/// </description></item>
/// <item><description>
/// Sampling gates the scan exactly as it gates the shadow run. At <c>SampleRate</c> 0 the filter
/// never scans; at 1.0 it always does; the draw uses the same comparison the planner uses, so the
/// two stay aligned at the deterministic ends and diverge only harmlessly in between.
/// </description></item>
/// <item><description>
/// The scan can never affect the request. The body is rewound in a <c>finally</c>, and every
/// exception the read/scan might raise is swallowed and logged at Debug - a lost diagnostic must
/// never fail live playback, and model binding must always see the whole body from position 0.
/// </description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class PlaybackContractScanFilter : IAsyncResourceFilter
{
    /// <summary>
    /// The <see cref="HttpContext.Items"/> key the scan result is stashed under. A
    /// <see cref="ContractStructuralScan"/> when the request was scanned; absent otherwise.
    /// </summary>
    public const string ScanResultKey = "Tesserafin.Playback.Contract.Scan#75b";

    /// <summary>
    /// The explicit upper bound, in bytes, on how much of the request body the scan reads. The bound
    /// is on the SCAN only - model binding still sees the whole body. A body larger than this is
    /// reported as <see cref="ContractStructuralScan.BodyLimitExceeded"/> rather than parsed.
    /// </summary>
    public const int BodySizeLimitBytes = 256 * 1024;

    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly PlaybackContractScanModelProvider _modelProvider;
    private readonly ILogger<PlaybackContractScanFilter> _logger;

    /// <summary>Initializes a new instance of the <see cref="PlaybackContractScanFilter"/> class.</summary>
    /// <param name="serverConfigurationManager">Source of the live shadow gate/sampling configuration - the same one the planner reads.</param>
    /// <param name="modelProvider">Provides the cached contract topology, with names from the binder's own metadata.</param>
    /// <param name="logger">Logs a swallowed scan fault at Debug. Optional; defaults to the null logger.</param>
    public PlaybackContractScanFilter(
        IServerConfigurationManager serverConfigurationManager,
        PlaybackContractScanModelProvider modelProvider,
        ILogger<PlaybackContractScanFilter>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(serverConfigurationManager);
        ArgumentNullException.ThrowIfNull(modelProvider);

        _serverConfigurationManager = serverConfigurationManager;
        _modelProvider = modelProvider;
        _logger = logger ?? NullLogger<PlaybackContractScanFilter>.Instance;
    }

    /// <inheritdoc/>
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        await TryScanAsync(context.HttpContext).ConfigureAwait(false);
        await next().ConfigureAwait(false);
    }

    private static bool ShouldScan(PlaybackShadowOptions options)
    {
        // Guarantee 1: shadow off => no scan, and the caller returns before touching the body.
        if (options.GetEffectiveMode() == PlaybackEngineMode.Legacy)
        {
            return false;
        }

        // Guarantee 2: sampled exactly as the planner samples - deterministic at 0 (never) and 1.0
        // (always), matching ShadowPlaybackSessionPlanner.PrepareShadow's own draw.
        if (options.SampleRate < 1.0 && Random.Shared.NextDouble() >= options.SampleRate)
        {
            return false;
        }

        return true;
    }

    private async Task TryScanAsync(HttpContext httpContext)
    {
        var options = _serverConfigurationManager.Configuration.PlaybackShadow;
        if (!ShouldScan(options))
        {
            return;
        }

        var request = httpContext.Request;
        var buffer = ArrayPool<byte>.Shared.Rent(BodySizeLimitBytes + 1);
        try
        {
            request.EnableBuffering();

            var (count, exceeded) = await ReadBoundedAsync(request.Body, buffer, httpContext.RequestAborted).ConfigureAwait(false);
            var scanLength = exceeded ? BodySizeLimitBytes : count;

            // Select the root whose known-name set matches the DTO the binder will use for this
            // method: POST binds CreatePlaybackSessionRequest, PUT binds ReplacePlaybackSessionRequest.
            var root = HttpMethods.IsPut(request.Method) ? _modelProvider.ReplaceRoot : _modelProvider.CreateRoot;

            var scan = PlaybackContractScanner.Scan(
                buffer.AsSpan(0, scanLength),
                root,
                count,
                exceeded);

            httpContext.Items[ScanResultKey] = scan;
        }
#pragma warning disable CA1031 // Do not catch general exception types - the scan must never fail live playback.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Guarantee 3: a scan fault is best-effort observability lost, never a failed request.
            _logger.LogDebug(ex, "Issue #75 structural scan of the playback request body was skipped after a fault; the request is unaffected.");
        }
        finally
        {
            // Guarantee 3: model binding must read the whole body from the start, byte-for-byte.
            if (request.Body.CanSeek)
            {
                request.Body.Position = 0;
            }

            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<(int Count, bool Exceeded)> ReadBoundedAsync(System.IO.Stream body, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await body.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        // buffer is limit + 1 bytes: reaching its end means at least one byte past the limit exists.
        var exceeded = total > BodySizeLimitBytes;
        return (total, exceeded);
    }
}
