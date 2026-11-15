using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Common;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Extensions.Json;
using Tesserafin.Extensions.Json.Converters;
using Tesserafin.MediaEncoding.Probing;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.MediaInfo;

namespace Tesserafin.MediaEncoding.Encoder;

/// <summary>Owns media-info probe lifetime and interpretation after native/pump termination.</summary>
internal sealed class MediaProbeService : IDisposable
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
    private readonly IFfmpegProcessRunner _runner;
    private readonly ILogger _logger;
    private readonly ILocalizationManager _localization;
    private readonly TimeSpan _timeout;
    private readonly FfmpegOperationScope _operations = new();
    private readonly JsonSerializerOptions _json = new(JsonDefaults.Options);

    public MediaProbeService(IFfmpegProcessRunner runner, ILogger logger, ILocalizationManager localization, TimeSpan? timeout = null)
    {
        _runner = runner;
        _logger = logger;
        _localization = localization;
        _timeout = timeout ?? DefaultTimeout;
        _json.Converters.Add(new JsonBoolStringConverter());
    }

    public Task<MediaInfo> ProbeAsync(FfmpegCommand command, string primaryPath, MediaProtocol protocol, bool audio, VideoType? videoType, Func<Stream>? openInput, CancellationToken cancellationToken)
        => _operations.RunAsync(token => ReadAsync(command, primaryPath, protocol, audio, videoType, openInput, token), cancellationToken);

    private async Task<MediaInfo> ReadAsync(FfmpegCommand command, string primaryPath, MediaProtocol protocol, bool audio, VideoType? videoType, Func<Stream>? openInput, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Media probe: {Executable} {Arguments}", command.Executable, command.ArgumentLine);
        var execution = await _runner.RunMediaProbeAsync(command, _timeout, openInput, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (execution.TimedOut || execution.ExitCode != 0)
        {
            _logger.LogError("Media probe failed: timeout={TimedOut}, exit={ExitCode}, stderr={StandardError}", execution.TimedOut, execution.ExitCode, execution.StandardError);
            throw new FfmpegException(execution.TimedOut ? "Media probe timed out." : $"Media probe exited with code {execution.ExitCode}.");
        }

        var result = JsonSerializer.Deserialize<InternalMediaInfoResult>(execution.StandardOutput, _json);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is null || (result.Streams is null && result.Format is null))
        {
            throw new FfmpegException("ffprobe failed - streams and format are both null.");
        }

        if (result.Streams is not null)
        {
            foreach (var stream in result.Streams)
            {
                if (string.Equals(stream.DisplayAspectRatio, "0:1", StringComparison.OrdinalIgnoreCase))
                {
                    stream.DisplayAspectRatio = string.Empty;
                }

                if (string.Equals(stream.SampleAspectRatio, "0:1", StringComparison.OrdinalIgnoreCase))
                {
                    stream.SampleAspectRatio = string.Empty;
                }
            }
        }

        return new ProbeResultNormalizer(_logger, _localization).GetMediaInfo(result, videoType, audio, primaryPath, protocol);
    }

    public Task StopAsync() => _operations.StopAsync();

    public void Dispose() => _operations.Dispose();
}
