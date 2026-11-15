namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// The outcome of a finite ffmpeg/ffprobe invocation run via <see cref="IFfmpegProcessRunner"/>.
/// </summary>
/// <param name="ExitCode">The process exit code, or null if the process was killed due to timeout.</param>
/// <param name="StandardOutput">The retained standard output (complete for probes, bounded tail for file commands).</param>
/// <param name="StandardError">The retained standard error (complete for probes, bounded tail for file commands).</param>
/// <param name="TimedOut">Whether the process was killed because it exceeded the requested timeout.</param>
public sealed record FfmpegProcessResult(int? ExitCode, string StandardOutput, string StandardError, bool TimedOut);
