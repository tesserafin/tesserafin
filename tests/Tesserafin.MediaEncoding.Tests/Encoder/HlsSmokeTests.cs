using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Encoder;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Encoder;

/// <summary>
/// PR115d, scope item 4 ("smoke tests ffmpeg/HLS in Docker"): a real, end-to-end proof that ffmpeg
/// can actually produce a playable HLS manifest and segments in whatever environment runs this test -
/// exactly the mechanic the v2 canary's live streaming path (<c>DynamicHlsController</c>, driven by
/// whichever <c>StreamInfo</c> <c>MediaInfoHelper.ResolveServedStreamInfo</c> served, legacy or v2)
/// ultimately depends on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Be precise about what this proves and what it does not.</b> This test runs a real ffmpeg
/// process, through <see cref="FfmpegProcessRunner"/> - the same production process-running component
/// <c>TranscodeManager.StartFfMpeg</c> uses - with a hand-built <c>testsrc</c>/<c>sine</c> lavfi
/// source (no real media file needed, so this has zero test-asset dependency) and asserts a real
/// `.m3u8` manifest plus at least one real, non-empty `.ts` segment land on disk. What it does NOT
/// prove: that this is the exact command line <c>EncodingHelper</c> would build for a real client
/// request, or that the v2/legacy serve-or-fallback DECISION (kill switch, canary cohort, the PR115d
/// stop-threshold guard) actually routes to this command line over a real HTTP connection - see
/// <c>ci/smoke.sh</c>'s own header comment for the full honesty statement, including why a genuine
/// attempt at booting the full server in Docker for this proof was not carried through (no existing
/// test harness in this repo provisions a library/media item/auth token; the only full-server harness,
/// <c>tests/Tesserafin.Server.Integration.Tests/TesserafinApplicationFactory.cs</c>, explicitly disables
/// ffmpeg validation for its own tests). The v2/legacy DECISION and the admin diagnostics endpoint are
/// separately, thoroughly covered at the unit level by <c>MediaInfoHelperLiveWiringTests</c> and
/// <c>PlaybackDiagnosticsMetricsControllerTests</c> - <c>ci/smoke.sh</c> runs both alongside this test.
/// </para>
/// <para>
/// Tagged <c>Category=Smoke</c> so <c>ci/run.sh</c> (the mandatory merge gate) can exclude it by
/// default - PR115d's scope explicitly asks for this to be optional/non-blocking, unlike every other
/// ffmpeg-touching test in this suite (for example <c>EncoderValidatorTests</c>), which already run
/// unconditionally as part of the normal gate. This one is kept separate because synthesizing and
/// segmenting two real seconds of video is meaningfully heavier than a version-string probe.
/// </para>
/// </remarks>
[Trait("Category", "Smoke")]
public class HlsSmokeTests
{
    [Fact]
    public async Task RealFfmpeg_SynthesizedTestSource_ProducesPlayableHlsManifestAndSegments()
    {
        // Deliberately a single Fact, not a [Theory] over "legacy" vs. "canary/V2" - the ffmpeg
        // invocation below is identical either way (this test proves ffmpeg/HLS MECHANICS, not the
        // v2/legacy DECISION), and a two-identical-runs Theory would misleadingly imply coverage of
        // the decision that this test does not actually provide. That decision - which mode serves a
        // given request, and that the diagnostics endpoint reports it correctly - is genuinely
        // covered, separately, by MediaInfoHelperLiveWiringTests/PlaybackDiagnosticsMetricsControllerTests,
        // which ci/smoke.sh runs alongside this test. See this class's remarks for the full statement
        // of what this test proves and does not prove.
        using var workDir = new TempDirectory();
        var manifestPath = Path.Combine(workDir.Path, "master.m3u8");
        var segmentPattern = Path.Combine(workDir.Path, "segment%03d.ts");

        string[] arguments =
        [
            "-hide_banner",
            "-y",
            "-f", "lavfi", "-i", "\"testsrc=size=320x240:rate=15:duration=2\"",
            "-f", "lavfi", "-i", "\"sine=frequency=1000:duration=2\"",
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
            "-c:a", "aac",
            "-f", "hls",
            "-hls_time", "1",
            "-hls_list_size", "0",
            "-hls_segment_filename", $"\"{segmentPattern}\"",
            $"\"{manifestPath}\"",
        ];
        var argumentLine = string.Join(' ', arguments);

        var command = FfmpegCommand.FromArgumentLine("ffmpeg", argumentLine, workingDirectory: workDir.Path);
        var runner = new FfmpegProcessRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<Tesserafin.MediaEncoding.Encoder.FfmpegProcessRunner>.Instance);

        var result = await runner.RunProbeAsync(command, TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.False(result.TimedOut, $"ffmpeg timed out. stderr tail: {Tail(result.StandardError)}");
        Assert.True(result.ExitCode is 0, $"ffmpeg exited {result.ExitCode}. stderr tail: {Tail(result.StandardError)}");

        Assert.True(File.Exists(manifestPath), "HLS manifest (.m3u8) was not produced.");
        var manifest = await File.ReadAllTextAsync(manifestPath, CancellationToken.None);
        Assert.Contains("#EXTM3U", manifest, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-ENDLIST", manifest, StringComparison.Ordinal); // VOD playlist fully closed out.

        var segments = Directory.GetFiles(workDir.Path, "segment*.ts");
        Assert.True(segments.Length >= 1, "No HLS segment (.ts) files were produced.");
        Assert.All(segments, path => Assert.True(new FileInfo(path).Length > 0, $"Segment {path} is empty."));
    }

    private static string Tail(string text, int maxChars = 2000) => text.Length <= maxChars ? text : text[^maxChars..];

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = Directory.CreateTempSubdirectory("reefin-hls-smoke-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup - a locked file on some platforms must never fail the test.
            }
        }
    }
}
