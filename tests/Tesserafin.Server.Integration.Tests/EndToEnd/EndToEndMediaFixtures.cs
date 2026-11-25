using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    /// Synthesizes a real Matroska (or AVI) file whose codecs the caller chooses among FFmpeg's built-in
    /// encoders, for the transcode journeys: a video or an audio stream a browser-like client does
    /// not decode (MPEG-4 Part 2, AC-3) beside one it does, and two audio tracks that differ
    /// (1000 Hz and 440 Hz) so a track change is distinguishable in what is produced.
    /// </summary>
    /// <param name="directory">The directory to write the fixture into.</param>
    /// <param name="fileName">The file name (including extension) to write.</param>
    /// <param name="durationSeconds">The duration of the fixture, in seconds.</param>
    /// <param name="videoEncoder">The FFmpeg video encoder: <c>libx264</c> or <c>mpeg4</c>.</param>
    /// <param name="audioEncoder">The FFmpeg audio encoder: <c>aac</c> or <c>ac3</c>.</param>
    /// <param name="secondAudioTrack">Whether to add a second audio track, so the audio indices are 1 and 2.</param>
    /// <param name="format">The FFmpeg muxer: <c>matroska</c> or <c>avi</c>.</param>
    /// <returns>The full path to the synthesized file.</returns>
    public static async Task<string> CreateFilmAsync(
        string directory,
        string fileName,
        int durationSeconds,
        string videoEncoder,
        string audioEncoder,
        bool secondAudioTrack = false,
        string format = "matroska")
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
            arguments.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:duration={duration}", "-map", "0:v", "-map", "1:a", "-map", "2:a"]);
        }

        arguments.AddRange(["-c:v", videoEncoder, "-pix_fmt", "yuv420p", "-g", "15"]);
        if (string.Equals(videoEncoder, "libx264", StringComparison.Ordinal))
        {
            arguments.AddRange(["-preset", "ultrafast", "-keyint_min", "15", "-sc_threshold", "0"]);
        }

        arguments.AddRange(["-c:a", audioEncoder, "-ac", "2", "-f", format, path]);

        await RunFfmpegAsync([.. arguments]).ConfigureAwait(false);

        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new InvalidOperationException($"ffmpeg did not produce a non-empty fixture at {path}.");
        }

        return path;
    }

    /// <summary>
    /// Synthesizes a real MP3 file (a 1000 Hz tone, stereo) for the audio journeys.
    /// </summary>
    /// <param name="directory">The directory to write the fixture into.</param>
    /// <param name="fileName">The file name (including extension) to write.</param>
    /// <param name="durationSeconds">The duration of the fixture, in seconds.</param>
    /// <returns>The full path to the synthesized file.</returns>
    public static async Task<string> CreateMp3Async(string directory, string fileName = "fixture.mp3", int durationSeconds = DurationSeconds)
    {
        var path = Path.Combine(directory, fileName);
        var duration = durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await RunFfmpegAsync(
        [
            "-hide_banner",
            "-y",
            "-f", "lavfi", "-i", $"sine=frequency=1000:duration={duration}",
            "-c:a", "libmp3lame", "-b:a", "128k", "-ac", "2",
            "-f", "mp3",
            path,
        ]).ConfigureAwait(false);

        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new InvalidOperationException($"ffmpeg did not produce a non-empty fixture at {path}.");
        }

        return path;
    }

    /// <summary>
    /// Synthesizes a real audio file in a format a browser does not play: a 440 Hz stereo tone
    /// encoded as WMA in ASF, or as FLAC carrying an embedded cover picture (a PNG the prober reports
    /// as a video stream).
    /// </summary>
    /// <param name="directory">The directory to write the fixture into.</param>
    /// <param name="withCoverArt"><see langword="true"/> for FLAC with an embedded picture, otherwise WMA.</param>
    /// <param name="durationSeconds">The duration of the fixture, in seconds.</param>
    /// <returns>The full path to the synthesized file.</returns>
    public static async Task<string> CreateUndecodedAudioAsync(string directory, bool withCoverArt, int durationSeconds = DurationSeconds)
    {
        var path = Path.Combine(directory, withCoverArt ? "fixture.flac" : "fixture.wma");
        var duration = durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string[] arguments = withCoverArt
            ?
            [
                "-hide_banner", "-y",
                "-f", "lavfi", "-i", $"sine=frequency=440:duration={duration}",
                "-f", "lavfi", "-i", "color=c=red:s=64x64:d=1",
                "-map", "0:a", "-map", "1:v", "-frames:v", "1",
                "-c:a", "flac", "-ac", "2", "-c:v", "png", "-disposition:v", "attached_pic",
                "-f", "flac", path,
            ]
            :
            [
                "-hide_banner", "-y",
                "-f", "lavfi", "-i", $"sine=frequency=440:duration={duration}",
                "-c:a", "wmav2", "-b:a", "128k", "-ac", "2",
                "-f", "asf", path,
            ];
        await RunFfmpegAsync(arguments).ConfigureAwait(false);

        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new InvalidOperationException($"ffmpeg did not produce a non-empty fixture at {path}.");
        }

        return path;
    }

    /// <summary>
    /// Synthesizes a DVD folder rip: <c>VIDEO_TS</c> holding one title split over two VOB files
    /// (MPEG-2 video and AC-3 audio in MPEG program streams), the layout the executor concatenates.
    /// </summary>
    /// <param name="directory">The directory to create the rip in.</param>
    /// <param name="durationSeconds">The duration of each VOB, in seconds.</param>
    /// <returns>The rip's folder and its first VOB.</returns>
    public static async Task<(string Folder, string FirstVob)> CreateDvdFolderAsync(string directory, int durationSeconds)
    {
        var folder = Path.Combine(directory, "DVD Probe");
        var videoTs = Directory.CreateDirectory(Path.Combine(folder, "VIDEO_TS")).FullName;
        var duration = durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var part in new[] { 1, 2 })
        {
            await RunFfmpegAsync(
            [
                "-hide_banner", "-y",
                "-f", "lavfi", "-i", $"testsrc=size=320x240:rate=25:duration={duration}",
                "-f", "lavfi", "-i", $"sine=frequency=440:duration={duration}",
                "-c:v", "mpeg2video", "-b:v", "2M", "-c:a", "ac3", "-ac", "2", "-shortest",
                "-f", "vob", Path.Combine(videoTs, FormattableString.Invariant($"VTS_01_{part}.VOB")),
            ]).ConfigureAwait(false);
        }

        return (folder, Path.Combine(videoTs, "VTS_01_1.VOB"));
    }

    /// <summary>
    /// Synthesizes an HDR10 film: HEVC Main10 tagged BT.2020 / SMPTE ST 2084 (PQ), with AAC, in
    /// Matroska - the source a client that only shows SDR must be served tone-mapped.
    /// </summary>
    /// <param name="directory">The directory to write the fixture into.</param>
    /// <param name="durationSeconds">The duration of the fixture, in seconds.</param>
    /// <returns>The full path to the synthesized file.</returns>
    public static async Task<string> CreateHdr10FilmAsync(string directory, int durationSeconds)
    {
        var path = Path.Combine(directory, "hdr10.mkv");
        var duration = durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await RunFfmpegAsync(
        [
            "-hide_banner", "-y",
            "-f", "lavfi", "-i", $"testsrc2=size=320x180:rate=25:duration={duration}",
            "-f", "lavfi", "-i", $"sine=frequency=440:duration={duration}",
            "-c:v", "libx265", "-preset", "ultrafast", "-pix_fmt", "yuv420p10le",
            "-x265-params", "colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:log-level=error",
            "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc",
            "-c:a", "aac", "-shortest",
            "-f", "matroska", path,
        ]).ConfigureAwait(false);

        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new InvalidOperationException($"ffmpeg did not produce a non-empty fixture at {path}.");
        }

        return path;
    }

    /// <summary>
    /// Reads, with the REAL ffprobe, the pixel format and transfer characteristic of a file's first
    /// video stream: what a tone mapping actually produced, not what a command line asked for.
    /// </summary>
    /// <param name="path">The file to probe.</param>
    /// <returns>The pixel format and the colour transfer (empty when untagged).</returns>
    public static async Task<(string PixelFormat, string ColorTransfer)> ProbeVideoColourAsync(string path)
    {
        var (exitCode, stdout, stderr) = await RunAsync(
            FfprobePath,
            ["-hide_banner", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=pix_fmt,color_transfer", "-of", "default=noprint_wrappers=1", path]).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe exited {exitCode} probing '{path}'.\nstderr tail: {Tail(stderr)}");
        }

        string Read(string key) => stdout.Split('\n', StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..] ?? string.Empty;
        return (Read("pix_fmt"), Read("color_transfer"));
    }

    /// <summary>
    /// Synthesizes an H.264 + AAC Matroska film carrying an embedded ASS subtitle stream (index 2),
    /// and writes an SRT sidecar next to it: two text subtitles a client that renders none must
    /// have burnt in.
    /// </summary>
    /// <param name="directory">The directory to write the fixture into.</param>
    /// <param name="durationSeconds">The duration of the fixture, in seconds.</param>
    /// <returns>The film path and the sidecar path.</returns>
    public static async Task<(string Film, string Sidecar)> CreateFilmWithTextSubtitlesAsync(string directory, int durationSeconds)
    {
        var film = Path.Combine(directory, "subtitled.mkv");
        var ass = Path.Combine(directory, "embedded.ass");
        var sidecar = Path.Combine(directory, "subtitled.en.srt");
        var end = TimeSpan.FromSeconds(durationSeconds).ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
        await File.WriteAllTextAsync(ass, $"[Script Info]\nScriptType: v4.00+\nPlayResX: 320\nPlayResY: 180\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, BackColour, Bold, Italic, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\nStyle: Default,DejaVu Sans,24,&H00FFFFFF,&H00000000,0,0,1,2,0,2,10,10,10,1\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:00.00,{end}.00,Default,,0,0,0,,Embedded subtitle\n").ConfigureAwait(false);
        await File.WriteAllTextAsync(sidecar, $"1\n00:00:00,000 --> 0{end},000\nSidecar subtitle\n").ConfigureAwait(false);
        var duration = durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await RunFfmpegAsync(
        [
            "-hide_banner", "-y",
            "-f", "lavfi", "-i", $"testsrc=size=320x180:rate=25:duration={duration}",
            "-f", "lavfi", "-i", $"sine=frequency=440:duration={duration}",
            "-i", ass,
            "-map", "0:v", "-map", "1:a", "-map", "2:s",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-c:s", "ass", "-shortest",
            "-f", "matroska", film,
        ]).ConfigureAwait(false);

        if (!File.Exists(film) || new FileInfo(film).Length == 0)
        {
            throw new InvalidOperationException($"ffmpeg did not produce a non-empty fixture at {film}.");
        }

        return (film, sidecar);
    }

    /// <summary>
    /// Synthesizes the body a software tuner serves for one channel: H.264 and AAC in MPEG-TS, the
    /// shape an M3U or HDHomeRun tuner delivers.
    /// </summary>
    /// <param name="directory">The directory to write the fixture into.</param>
    /// <param name="durationSeconds">The duration of the fixture, in seconds.</param>
    /// <returns>The full path to the synthesized file.</returns>
    public static async Task<string> CreateTunerTransportStreamAsync(string directory, int durationSeconds)
    {
        var path = Path.Combine(directory, "channel1.ts");
        var duration = durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await RunFfmpegAsync(
        [
            "-hide_banner", "-y",
            "-f", "lavfi", "-i", $"testsrc=size=320x180:rate=25:duration={duration}",
            "-f", "lavfi", "-i", $"sine=frequency=440:duration={duration}",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "25",
            "-c:a", "aac", "-shortest",
            "-f", "mpegts", path,
        ]).ConfigureAwait(false);

        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new InvalidOperationException($"ffmpeg did not produce a non-empty fixture at {path}.");
        }

        return path;
    }

    /// <summary>
    /// Synthesizes a real AAC track in a Matroska audio container: a codec a browser decodes, in a
    /// container it does not play as it is.
    /// </summary>
    /// <param name="directory">The directory to write the fixture into.</param>
    /// <param name="durationSeconds">The duration of the fixture, in seconds.</param>
    /// <returns>The full path to the synthesized file.</returns>
    public static async Task<string> CreateAacInMatroskaAsync(string directory, int durationSeconds = DurationSeconds)
    {
        var path = Path.Combine(directory, "fixture.mka");
        var duration = durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await RunFfmpegAsync(
        [
            "-hide_banner", "-y",
            "-f", "lavfi", "-i", $"sine=frequency=440:duration={duration}",
            "-c:a", "aac", "-b:a", "128k", "-ac", "2",
            "-f", "matroska", path,
        ]).ConfigureAwait(false);

        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new InvalidOperationException($"ffmpeg did not produce a non-empty fixture at {path}.");
        }

        return path;
    }

    /// <summary>
    /// Runs the REAL ffprobe binary against a file on disk and returns, per stream, its type, codec
    /// and (for video) width: what was actually produced, not what a command line or a label says.
    /// </summary>
    /// <param name="path">The file to probe.</param>
    /// <returns>One <c>codec_type,codec_name[,width]</c> line per stream, in stream order.</returns>
    public static async Task<IReadOnlyList<string>> ProbeStreamsAsync(string path)
    {
        var (exitCode, stdout, stderr) = await RunAsync(
            FfprobePath,
            ["-hide_banner", "-v", "error", "-show_entries", "stream=codec_type,codec_name,width", "-of", "csv=p=0", path]).ConfigureAwait(false);

        if (exitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe exited {exitCode} probing '{path}'.\nstderr tail: {Tail(stderr)}");
        }

        // ffprobe prints the entries in its own order (codec_name, codec_type, width), whatever
        // order they were asked in.
        var streams = new List<string>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            streams.Add(fields.Length > 2 ? $"{fields[1]},{fields[0]},{fields[2]}" : $"{fields[1]},{fields[0]}");
        }

        return streams;
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

    /// <summary>
    /// Reads, with the REAL ffmpeg, the average colour of the first frame a file decodes to: the
    /// frame scaled to one pixel, in RGB.
    /// </summary>
    /// <param name="path">The media file.</param>
    /// <returns>The red, green and blue components.</returns>
    public static async Task<(int R, int G, int B)> ReadFirstFrameColourAsync(string path)
    {
        var pixel = path + ".rgb";
        await RunFfmpegAsync(["-hide_banner", "-loglevel", "error", "-y", "-i", path, "-map", "0:v:0", "-frames:v", "1", "-vf", "scale=1:1", "-pix_fmt", "rgb24", "-f", "rawvideo", pixel]).ConfigureAwait(false);
        var bytes = await File.ReadAllBytesAsync(pixel).ConfigureAwait(false);
        File.Delete(pixel);
        if (bytes.Length != 3)
        {
            throw new InvalidOperationException($"expected one RGB pixel from '{path}', got {bytes.Length} bytes");
        }

        return (bytes[0], bytes[1], bytes[2]);
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
