using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Encoder;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Encoder;

/// <summary>
/// Verifies <see cref="HardwareBackendProbe"/> against a real VAAPI render node, not a mock - the
/// whole point of this probe (transcoding-pipeline plan PR8/PR10) is that it catches backends that
/// look present but don't actually work, so a mocked process runner would prove nothing. Uses the
/// VAAPI candidate's real argument-building function from <see cref="HardwareBackendCatalog"/>
/// rather than a hand-copied string, so this test and production stay locked together.
/// </summary>
public class HardwareBackendProbeTests
{
    private const string RealVaapiRenderNode = "/dev/dri/renderD128";

    [Fact]
    public async Task ProbeAsync_RealVaapiHardware_Succeeds()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists(RealVaapiRenderNode), "requires a real VAAPI render node - not present in this environment");

        var probe = new HardwareBackendProbe(new FfmpegProcessRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<Tesserafin.MediaEncoding.Encoder.FfmpegProcessRunner>.Instance), NullLogger.Instance);
        var arguments = BuildVaapiArguments(RealVaapiRenderNode);

        var result = await probe.ProbeAsync("ffmpeg", arguments, TestContext.Current.CancellationToken);

        Assert.True(result);
    }

    [Fact]
    public async Task ProbeAsync_NonexistentDevice_Fails()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "VAAPI is Linux-only");

        var probe = new HardwareBackendProbe(new FfmpegProcessRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<Tesserafin.MediaEncoding.Encoder.FfmpegProcessRunner>.Instance), NullLogger.Instance);
        var arguments = BuildVaapiArguments("/dev/dri/renderD999");

        var result = await probe.ProbeAsync("ffmpeg", arguments, TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    private static string BuildVaapiArguments(string devicePath)
    {
        var candidate = HardwareBackendCatalog.CandidatesInPriorityOrder.Single(c => c.Type == HardwareAccelerationType.vaapi);
        var options = new EncodingOptions { VaapiDevice = devicePath };
        return candidate.BuildTrialEncodeArguments(options)!;
    }
}
