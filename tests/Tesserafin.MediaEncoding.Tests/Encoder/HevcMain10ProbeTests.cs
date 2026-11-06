using System;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Encoder;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Encoder;

public class HevcMain10ProbeTests
{
    [Theory]
    [InlineData(0, false, true)]
    [InlineData(1, false, false)]
    [InlineData(0, true, false)]
    [InlineData(null, true, false)]
    public void Probe_RequiresSuccessfulBoundedStrictEncode(int? exitCode, bool timedOut, bool expected)
    {
        var runner = new Mock<IFfmpegProcessRunner>();
        runner.Setup(x => x.RunProbeAsync(It.IsAny<FfmpegCommand>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new FfmpegProcessResult(exitCode, string.Empty, string.Empty, timedOut));
        var validator = new EncoderValidator(NullLogger.Instance, "chosen-ffmpeg", runner.Object);

        Assert.Equal(expected, validator.CheckSoftwareHevcMain10());
        runner.Verify(
            x => x.RunProbeAsync(
                It.Is<FfmpegCommand>(c => c.Executable == "chosen-ffmpeg" && c.ToArgumentLine().Contains("+yuv420p10le", StringComparison.Ordinal)),
                It.Is<TimeSpan>(t => t > TimeSpan.Zero && t <= TimeSpan.FromSeconds(60)),
                It.IsAny<CancellationToken>(),
                null),
            Times.Once);
    }

    [Fact]
    public void Probe_ProcessFailure_IsNotCapabilityEvidence()
    {
        var runner = new Mock<IFfmpegProcessRunner>();
        runner.Setup(x => x.RunProbeAsync(It.IsAny<FfmpegCommand>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), null))
            .ThrowsAsync(new InvalidOperationException("synthetic process failure"));
        Assert.False(new EncoderValidator(NullLogger.Instance, "missing-ffmpeg", runner.Object).CheckSoftwareHevcMain10());
    }
}
