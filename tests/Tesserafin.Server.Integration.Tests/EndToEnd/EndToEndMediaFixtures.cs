using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// PR119: synthesizes real, on-disk media fixtures with a real ffmpeg process - the same
/// no-test-asset-dependency approach <c>HlsSmokeTests</c> already uses (a hand-built
/// <c>testsrc</c>/<c>sine</c> lavfi source), extended here to produce files a real, booted server can
/// register as a library item and actually serve. Every fixture is a real, decodable file: what
/// distinguishes DirectPlay from remux from transcode in the end-to-end tests is the request
/// (<c>PlaybackConstraints.AllowDirectPlay</c>/<c>AllowDirectStream</c>/<c>AllowTranscoding</c>), not
/// the file - see <c>PlaybackUrlContractEndToEndTests</c>' remarks for why that is a faithful,
/// deliberately-chosen simplification.
/// </summary>
public static class EndToEndMediaFixtures
{
    /// <summary>The exact duration, in seconds, every synthesized fixture below encodes.</summary>
    public const int DurationSeconds = 2;

    /// <summary>The exact duration above, expressed in the ticks <c>BaseItem.RunTimeTicks</c> expects.</summary>
    public const long DurationTicks = DurationSeconds * TimeSpan.TicksPerSecond;

    public const int Width = 320;

    public const int Height = 240;

    /// <summary>
    /// Gets the real ffprobe binary this class shells out to. Resolved as ffmpeg's own sibling when
    /// <c>FFMPEG_PATH</c> names an explicit binary (the same override
    /// <see cref="E2eApplicationFactory.FfmpegPath"/> honours), otherwise plain <c>"ffprobe"</c> off
    /// <c>$PATH</c> - which <c>Dockerfile.ci</c> guarantees, exactly as it does for ffmpeg.
    /// </summary>
    internal static string FfprobePath { get; } = ResolveFfprobePath();

    /// <summary>
    /// Synthesizes a real H.264/AAC MP4 - a container/codec pair any reasonable client declares as a
    /// DirectPlay target, so this single fixture backs the DirectPlay, remux, transcode, and subtitle
    /// scenarios alike (the request's own constraints decide which method actually gets used).
    /// </summary>
    /// <param name="directory">The directory to write the fixture into.</param>
    /// <param name="fileName">The file name (including extension) to write.</param>
    /// <param name="durationSeconds">The duration to encode: more than one HLS segment's worth when a test needs several.</param>
    /// <returns>The full path to the synthesized file.</returns>
    public static async Task<string> CreateH264AacMp4Async(string directory, string fileName = "fixture.mp4", int durationSeconds = DurationSeconds)
    {
        var path = Path.Combine(directory, fileName);
        string[] arguments =
        [
            "-hide_banner",
            "-y",
            "-f", "lavfi", "-i", $"testsrc=size={Width}x{Height}:rate=15:duration={durationSeconds}",
            "-f", "lavfi", "-i", $"sine=frequency=1000:duration={durationSeconds}",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-movflags", "+faststart",
            path,
        ];

        await RunFfmpegAsync(arguments).ConfigureAwait(false);

        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new InvalidOperationException($"ffmpeg did not produce a non-empty fixture at {path}.");
        }

        return path;
    }

    /// <summary>
    /// Synthesizes a real H.264/AAC MATROSKA file - same codecs as
    /// <see cref="CreateH264AacMp4Async"/>, deliberately a different CONTAINER. Issue #57 needs a
    /// source whose container differs from the announced output container, so "the bytes actually
    /// served" and "the container the descriptor announces" are distinguishable at all: with an mp4
    /// source remuxed to mp4 the defect is invisible, because serving the source verbatim still
    /// yields mp4 bytes.
    /// </summary>
    /// <param name="directory">The directory to write the fixture into.</param>
    /// <param name="fileName">The file name (including extension) to write.</param>
    /// <param name="durationSeconds">The duration of the fixture, in seconds.</param>
    /// <param name="keyframeEverySecond">Whether to force one keyframe per second, so a stream copy can be cut into several segments.</param>
    /// <param name="secondAudioTrack">Whether to add a second audio track, so the audio indices are 1 and 2.</param>
    /// <returns>The full path to the synthesized file.</returns>
    public static async Task<string> CreateH264AacMkvAsync(
        string directory,
        string fileName = "fixture.mkv",
        int durationSeconds = DurationSeconds,
        bool keyframeEverySecond = false,
        bool secondAudioTrack = false)
    {
        var path = Path.Combine(directory, fileName);
        var duration = durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        List<string> arguments =
        [
            "-hide_banner",
            "-y",
            "-f", "lavfi", "-i", $"testsrc=size={Width}x{Height}:rate=15:duration={duration}",
            "-f", "lavfi", "-i", $"sine=frequency=1000:duration={duration}",
        ];

        if (secondAudioTrack)
        {
            // Stream 1 and stream 2 are both audio: a version whose track indices differ from a
            // single-audio version of the same film.
            arguments.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:duration={duration}", "-map", "0:v", "-map", "1:a", "-map", "2:a"]);
        }

        arguments.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"]);
        if (keyframeEverySecond)
        {
            // One keyframe per second, so a stream copy can be cut into several HLS segments.
            arguments.AddRange(["-g", "15", "-keyint_min", "15", "-sc_threshold", "0"]);
        }

        arguments.AddRange(["-c:a", "aac", "-f", "matroska", path]);

        await RunFfmpegAsync([.. arguments]).ConfigureAwait(false);

        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new InvalidOperationException($"ffmpeg did not produce a non-empty fixture at {path}.");
        }

        return path;
    }

    /// <summary>
    /// Runs the REAL ffprobe binary against a file on disk and returns its <c>format_name</c> - the
    /// same identification a client-side player performs, and the strongest available statement about
    /// what a byte buffer actually IS (as opposed to what a header claims). Used by the issue #57
    /// scenario to prove the served bytes are genuinely ISOBMFF and not Matroska.
    /// </summary>
    /// <param name="path">The file to probe.</param>
    /// <returns>ffprobe's own <c>format_name</c> for that file (for example <c>"mov,mp4,m4a,3gp,3g2,mj2"</c>).</returns>
    public static async Task<string> ProbeFormatNameAsync(string path)
    {
        var (exitCode, stdout, stderr) = await RunAsync(
            FfprobePath,
            ["-hide_banner", "-v", "error", "-show_entries", "format=format_name", "-of", "default=noprint_wrappers=1:nokey=1", path]).ConfigureAwait(false);

        if (exitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe exited {exitCode} probing '{path}'.\nstderr tail: {Tail(stderr)}");
        }

        return stdout.Trim();
    }

    /// <summary>Probes the actual encoded streams of a produced file.</summary>
    /// <param name="path">The produced file.</param>
    /// <returns>FFprobe stream metadata as JSON.</returns>
    public static async Task<string> ProbeStreamsJsonAsync(string path)
    {
        var (exitCode, stdout, stderr) = await RunAsync(
            FfprobePath,
            ["-v", "error", "-show_streams", "-of", "json", path]).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe exited {exitCode}: {Tail(stderr)}");
        }

        return stdout;
    }

    /// <summary>
    /// Writes a real, valid external SubRip (.srt) subtitle sidecar - no ffmpeg needed, an external
    /// subtitle is a plain sidecar file by definition (PR117 design doc §2.2's <c>SubtitleUrl</c>).
    /// </summary>
    /// <param name="directory">The directory to write the sidecar into.</param>
    /// <param name="fileName">The file name (including extension) to write.</param>
    /// <returns>The full path to the written subtitle file.</returns>
    public static string CreateExternalSrt(string directory, string fileName = "fixture.srt")
    {
        var path = Path.Combine(directory, fileName);
        var content = new StringBuilder()
            .AppendLine("1")
            .AppendLine("00:00:00,000 --> 00:00:01,000")
            .AppendLine("PR119 end-to-end external subtitle fixture.")
            .AppendLine()
            .AppendLine("2")
            .AppendLine("00:00:01,000 --> 00:00:02,000")
            .AppendLine("Second cue, still real bytes on disk.")
            .ToString();

        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    private static string ResolveFfprobePath()
    {
        var ffmpeg = E2eApplicationFactory.FfmpegPath;
        var directory = Path.GetDirectoryName(ffmpeg);
        if (string.IsNullOrEmpty(directory))
        {
            return "ffprobe";
        }

        var sibling = Path.Combine(directory, Path.GetFileName(ffmpeg).Replace("ffmpeg", "ffprobe", StringComparison.Ordinal));
        return File.Exists(sibling) ? sibling : "ffprobe";
    }

    private static async Task RunFfmpegAsync(string[] arguments)
    {
        var (exitCode, _, stderr) = await RunAsync(E2eApplicationFactory.FfmpegPath, arguments).ConfigureAwait(false);

        if (exitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg exited {exitCode}. Arguments: {string.Join(' ', arguments)}\nstderr tail: {Tail(stderr)}");
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string fileName, string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            },
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();

        using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"'{fileName}' timed out. Arguments: {string.Join(' ', arguments)}");
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);

        return (process.ExitCode, stdout, stderr);
    }

    private static string Tail(string text, int maxChars = 2000) => text.Length <= maxChars ? text : text[^maxChars..];
}
