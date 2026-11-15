using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Encoder;

public sealed partial class FfmpegProcessArchitectureTests
{
    [Fact]
    public void ManagedTranscodes_DelegateNativeProcessCreationAndLaunch()
    {
        var source = Source("Tesserafin.MediaEncoding", "Transcoding", "TranscodeManager.cs");
        Assert.False(NativeProcessCreationOrLaunch().IsMatch(source), "Managed transcodes must use IFfmpegProcessRunner for native process creation and launch.");
    }

    [Fact]
    public void CommandConstruction_CannotChangeTheServerEnvironment()
    {
        var source = Source("Tesserafin.Controller", "MediaEncoding", "EncodingHelper.cs");
        Assert.DoesNotContain("SetEnvironmentVariable", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Attachments", "AttachmentExtractor.cs")]
    [InlineData("Subtitles", "SubtitleEncoder.cs")]
    public void FileExtractors_DelegateNativeProcessLifecycle(string component, string file)
    {
        var source = Source("Tesserafin.MediaEncoding", component, file);
        Assert.False(NativeProcessCreationOrLaunch().IsMatch(source), $"{file} must run finite commands through IFfmpegProcessRunner.");
    }

    [Fact]
    public void ImageScheduler_HasNoNativeProcessLaunch()
        => Assert.DoesNotMatch(NativeProcessCreationOrLaunch(), Source("Tesserafin.MediaEncoding", "Encoder", "FfmpegImageScheduler.cs"));

    [Fact]
    public void MediaEncoderImagePaths_HaveNoNativeProcessLaunch()
    {
        var source = Source("Tesserafin.MediaEncoding", "Encoder", "MediaEncoder.cs");
        var start = source.IndexOf("private async Task<string> ExtractImageInternal(", StringComparison.Ordinal);
        var end = source.IndexOf("public string GetTimeParameter(long ticks)", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        Assert.DoesNotMatch(NativeProcessCreationOrLaunch(), source[start..end]);
    }

    [Fact]
    public void CachePublisher_HasNoCopyOrManagedMoveFallback()
        => Assert.DoesNotMatch(@"\bFile\.(?:Copy|Move)\s*\(", Source("Tesserafin.MediaEncoding", "Encoder", "AtomicCachePublication.cs"));

    private static string Source(params string[] components)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tesserafin.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return string.Join('\n', File.ReadLines(Path.Combine(directory.FullName, Path.Combine(components)))
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    [GeneratedRegex(@"\bnew\s+(?:System\.Diagnostics\.)?Process(?:StartInfo)?\s*(?:\(|\{)|\b(?:process|Process)\.Start\s*\(")]
    private static partial Regex NativeProcessCreationOrLaunch();
}
