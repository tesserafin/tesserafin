using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Encoder;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.MediaInfo;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Encoder;

/// <summary>
/// Characterization tests locking <see cref="MediaEncoder"/>'s current build-capability lookup
/// behavior (<c>SetAvailable*</c> + <c>Supports*</c>) before the transcoding-pipeline plan's PR6
/// replaces the backing <c>_encoders</c>/<c>_decoders</c>/<c>_hwaccels</c>/<c>_filters</c> lists
/// with a typed <c>HardwareCapabilitySnapshot</c>. <see cref="MediaEncoder"/> had zero test
/// coverage before this file - <see cref="EncoderValidatorTests"/> only covers the probing layer,
/// never the resulting <c>Supports*</c> lookups.
/// </summary>
public class MediaEncoderCapabilityTests
{
    [Fact]
    public void SupportsEncoder_IsCaseInsensitiveAndReflectsLastSetList()
    {
        var encoder = CreateEncoder();

        encoder.SetAvailableEncoders(new[] { "h264_vaapi", "libx264" });

        Assert.True(encoder.SupportsEncoder("h264_vaapi"));
        Assert.True(encoder.SupportsEncoder("H264_VAAPI"));
        Assert.False(encoder.SupportsEncoder("h264_nvenc"));
    }

    [Fact]
    public void SupportsDecoder_IsCaseInsensitiveAndReflectsLastSetList()
    {
        var encoder = CreateEncoder();

        encoder.SetAvailableDecoders(new[] { "h264" });

        Assert.True(encoder.SupportsDecoder("H264"));
        Assert.False(encoder.SupportsDecoder("hevc"));
    }

    [Fact]
    public void SupportsHwaccel_IsCaseInsensitiveAndReflectsLastSetList()
    {
        var encoder = CreateEncoder();

        encoder.SetAvailableHwaccels(new[] { "vaapi" });

        Assert.True(encoder.SupportsHwaccel("VAAPI"));
        Assert.False(encoder.SupportsHwaccel("qsv"));
    }

    [Fact]
    public void SupportsFilter_IsCaseInsensitiveAndReflectsLastSetList()
    {
        var encoder = CreateEncoder();

        encoder.SetAvailableFilters(new[] { "scale_vaapi" });

        Assert.True(encoder.SupportsFilter("SCALE_VAAPI"));
        Assert.False(encoder.SupportsFilter("scale_qsv"));
    }

    [Fact]
    public void SupportsFilterWithOption_UnknownKey_DefaultsFalse()
    {
        var encoder = CreateEncoder();

        encoder.SetAvailableFiltersWithOption(new Dictionary<FilterOptionType, bool>
        {
            [FilterOptionType.ScaleCudaFormat] = true,
        });

        Assert.True(encoder.SupportsFilterWithOption(FilterOptionType.ScaleCudaFormat));
        Assert.False(encoder.SupportsFilterWithOption(FilterOptionType.TonemapCudaName));
    }

    [Fact]
    public void SupportsBitStreamFilterWithOption_UnknownKey_DefaultsFalse()
    {
        var encoder = CreateEncoder();

        encoder.SetAvailableBitStreamFiltersWithOption(new Dictionary<BitStreamFilterOptionType, bool>
        {
            [BitStreamFilterOptionType.HevcMetadataRemoveDovi] = true,
        });

        Assert.True(encoder.SupportsBitStreamFilterWithOption(BitStreamFilterOptionType.HevcMetadataRemoveDovi));
        Assert.False(encoder.SupportsBitStreamFilterWithOption(BitStreamFilterOptionType.DoviRpuStrip));
    }

    [Fact]
    public void SettingEncodersDoesNotAffectDecodersOrHwaccelsOrFilters()
    {
        var encoder = CreateEncoder();

        encoder.SetAvailableEncoders(new[] { "libx264" });
        encoder.SetAvailableDecoders(new[] { "h264" });
        encoder.SetAvailableHwaccels(new[] { "vaapi" });
        encoder.SetAvailableFilters(new[] { "scale_vaapi" });

        Assert.True(encoder.SupportsEncoder("libx264"));
        Assert.False(encoder.SupportsDecoder("libx264"));
        Assert.False(encoder.SupportsHwaccel("libx264"));
        Assert.False(encoder.SupportsFilter("libx264"));
    }

    [Fact]
    public void FreshInstance_VaapiDeviceFlagsDefaultFalse()
    {
        var encoder = CreateEncoder();

        Assert.False(encoder.IsVaapiDeviceAmd);
        Assert.False(encoder.IsVaapiDeviceInteliHD);
        Assert.False(encoder.IsVaapiDeviceInteli965);
        Assert.False(encoder.IsVaapiDeviceSupportVulkanDrmModifier);
        Assert.False(encoder.IsVaapiDeviceSupportVulkanDrmInterop);
    }

    private static MediaEncoder CreateEncoder()
    {
        var serverConfig = new Mock<IServerConfigurationManager>();
        serverConfig.Setup(x => x.Configuration).Returns(new ServerConfiguration());
        IConfiguration configuration = new ConfigurationBuilder().Build();

        return new MediaEncoder(
            NullLogger<MediaEncoder>.Instance,
            serverConfig.Object,
            Mock.Of<Tesserafin.Model.IO.IFileSystem>(),
            Mock.Of<IBlurayExaminer>(),
            Mock.Of<Tesserafin.Model.Globalization.ILocalizationManager>(),
            configuration,
            serverConfig.Object,
            new FfmpegProcessRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<Tesserafin.MediaEncoding.Encoder.FfmpegProcessRunner>.Instance));
    }
}
