using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Tesserafin.Model.Entities;
using Xunit;

namespace Tesserafin.Controller.Tests.MediaEncoding;

public sealed class FfmpegEnvironmentTests
{
    private static readonly string[] _driverVariables = ["LIBVA_DRIVER_NAME", "LIBVA_DRIVER_NAME_REEFIN", "AMD_DEBUG"];

    [Theory]
    [InlineData("i965", true, 2)]
    [InlineData("amd", true, 1)]
    [InlineData("generic", true, 0)]
    [InlineData("i965", false, 0)]
    [InlineData("amd", false, 0)]
    public void InputAndProgressiveCommand_CaptureDriverEnvironmentWithoutChangingParent(string driver, bool hardware, int expectedCount)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "VAAPI initialization requires Linux");
        var parent = ParentEnvironment();
        var (helper, state, options, encoder) = VaapiPipelineCharacterizationTests.Create(new("environment", Driver: driver, Hardware: hardware));
        encoder.Setup(x => x.EncoderPath).Returns("ffmpeg");
        state.OutputFilePath = "/output/movie.mp4";
        var input = helper.GetInputArgument(state, options, "mp4");
        var command = helper.GetProgressiveVideoCommand(state, options, EncoderPreset.ultrafast);

        Assert.Equal(expectedCount, input.EnvironmentVariables.Count);
        Assert.Equal(input.EnvironmentVariables, command.EnvironmentVariables);
        Assert.Contains(input.ArgumentLine, command.ArgumentLine, StringComparison.Ordinal);
        if (expectedCount == 2)
        {
            Assert.Equal("i965", command.EnvironmentVariables["LIBVA_DRIVER_NAME"]);
            Assert.Equal("i965", command.EnvironmentVariables["LIBVA_DRIVER_NAME_REEFIN"]);
        }
        else if (expectedCount == 1)
        {
            Assert.Equal("noefc", command.EnvironmentVariables["AMD_DEBUG"]);
        }

        Assert.Equal(parent, ParentEnvironment());
    }

    [Fact]
    public async Task ConcurrentPlans_DoNotExchangeDriverOverrides()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "VAAPI initialization requires Linux");
        var parent = ParentEnvironment();
        var plans = await Task.WhenAll(Enumerable.Range(0, 48).Select(index => Task.Run(
            () =>
            {
                var driver = index % 3 == 0 ? "i965" : index % 3 == 1 ? "amd" : "generic";
                var (helper, state, options, _) = VaapiPipelineCharacterizationTests.Create(new("concurrent", Driver: driver));
                return (Driver: driver, Input: helper.GetInputArgument(state, options, "mp4"));
            },
            TestContext.Current.CancellationToken)));

        foreach (var (driver, input) in plans)
        {
            Assert.Equal(driver == "i965" ? 2 : driver == "amd" ? 1 : 0, input.EnvironmentVariables.Count);
            Assert.Equal(driver == "i965", input.EnvironmentVariables.ContainsKey("LIBVA_DRIVER_NAME"));
            Assert.Equal(driver == "amd", input.EnvironmentVariables.ContainsKey("AMD_DEBUG"));
        }

        Assert.Equal(parent, ParentEnvironment());
    }

    private static Dictionary<string, string?> ParentEnvironment()
        => _driverVariables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
}
