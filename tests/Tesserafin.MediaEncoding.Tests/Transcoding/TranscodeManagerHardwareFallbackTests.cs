using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Session;
using Tesserafin.MediaEncoding.Transcoding;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Transcoding;

/// <summary>
/// What a granted software fallback does to the options new commands are built with
/// (tesserafin#119): how far it reaches, and that the administrator's stored choice is not what
/// carries it.
/// </summary>
public sealed class TranscodeManagerHardwareFallbackTests
{
    private static readonly TranscodeFallbackDecision _backendWide =
        new(true, HardwareAccelerationType.none) { Scope = TranscodeFallbackScope.Backend };

    private static readonly TranscodeFallbackDecision _sourceOnly =
        new(true, HardwareAccelerationType.none) { Scope = TranscodeFallbackScope.MediaSource };

    [Fact]
    public void NothingFailed_ReturnsTheConfiguredOptionsThemselves()
    {
        var (manager, configured, _) = Create(HardwareAccelerationType.vaapi);

        Assert.Same(configured, manager.GetEffectiveEncodingOptions("source-a"));
    }

    [Fact]
    public void BackendWideFallback_WithholdsHardwareFromEverySource()
    {
        var (manager, _, _) = Create(HardwareAccelerationType.vaapi);

        manager.WithholdHardware(_backendWide, HardwareAccelerationType.vaapi, "source-a");

        Assert.Equal(HardwareAccelerationType.none, manager.GetEffectiveEncodingOptions("source-a").HardwareAccelerationType);
        Assert.Equal(HardwareAccelerationType.none, manager.GetEffectiveEncodingOptions("source-b").HardwareAccelerationType);
        Assert.Equal(HardwareAccelerationType.none, manager.GetEffectiveEncodingOptions(null).HardwareAccelerationType);
    }

    [Fact]
    public void SourceOnlyFallback_LeavesTheBackendToEveryOtherSource()
    {
        var (manager, configured, _) = Create(HardwareAccelerationType.vaapi);

        manager.WithholdHardware(_sourceOnly, HardwareAccelerationType.vaapi, "source-a");

        Assert.Equal(HardwareAccelerationType.none, manager.GetEffectiveEncodingOptions("source-a").HardwareAccelerationType);
        Assert.Same(configured, manager.GetEffectiveEncodingOptions("source-b"));
        Assert.Same(configured, manager.GetEffectiveEncodingOptions(null));
    }

    [Fact]
    public void Fallback_NeverTouchesOrSavesTheStoredConfiguration()
    {
        var (manager, configured, configurationManager) = Create(HardwareAccelerationType.vaapi);

        manager.WithholdHardware(_backendWide, HardwareAccelerationType.vaapi, "source-a");
        var effective = manager.GetEffectiveEncodingOptions("source-a");

        Assert.NotSame(configured, effective);
        Assert.Equal(HardwareAccelerationType.vaapi, configured.HardwareAccelerationType);
        configurationManager.Verify(x => x.SaveConfiguration(It.IsAny<string>(), It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public void Fallback_KeepsEveryOtherOption()
    {
        var (manager, configured, _) = Create(HardwareAccelerationType.vaapi);
        configured.VaapiDevice = "/dev/dri/renderD129";
        configured.EncodingThreadCount = 3;
        configured.H264Crf = 27;
        configured.HardwareDecodingCodecs = ["h264", "hevc", "av1"];
        configured.AllowOnDemandMetadataBasedKeyframeExtractionForExtensions = ["mkv", "ts"];

        manager.WithholdHardware(_backendWide, HardwareAccelerationType.vaapi, null);
        var effective = manager.GetEffectiveEncodingOptions(null);

        Assert.Equal("/dev/dri/renderD129", effective.VaapiDevice);
        Assert.Equal(3, effective.EncodingThreadCount);
        Assert.Equal(27, effective.H264Crf);

        // Arrays with constructor defaults are the ones a careless copy appends to instead of replacing.
        Assert.Equal(["h264", "hevc", "av1"], effective.HardwareDecodingCodecs);
        Assert.Equal(["mkv", "ts"], effective.AllowOnDemandMetadataBasedKeyframeExtractionForExtensions);
    }

    [Fact]
    public void AnotherBackendFailing_DoesNotWithholdTheConfiguredOne()
    {
        var (manager, configured, _) = Create(HardwareAccelerationType.vaapi);

        manager.WithholdHardware(_backendWide, HardwareAccelerationType.qsv, null);

        Assert.Same(configured, manager.GetEffectiveEncodingOptions(null));
    }

    [Fact]
    public void RefusedDecision_ChangesNothing()
    {
        var (manager, configured, _) = Create(HardwareAccelerationType.vaapi);

        manager.WithholdHardware(new TranscodeFallbackDecision(false, HardwareAccelerationType.none), HardwareAccelerationType.vaapi, "source-a");

        Assert.Same(configured, manager.GetEffectiveEncodingOptions("source-a"));
    }

    [Fact]
    public void SoftwareConfigured_IsReturnedAsIs()
    {
        var (manager, configured, _) = Create(HardwareAccelerationType.none);

        manager.WithholdHardware(_backendWide, HardwareAccelerationType.vaapi, null);

        Assert.Same(configured, manager.GetEffectiveEncodingOptions(null));
    }

    [Fact]
    public void NoJob_HasNoFailure()
    {
        var (manager, _, _) = Create(HardwareAccelerationType.vaapi);

        Assert.Null(manager.GetTranscodeFailure("/nowhere/x.m3u8", TranscodingJobType.Hls));
    }

    private static (TranscodeManager Manager, EncodingOptions Configured, Mock<IServerConfigurationManager> ConfigurationManager) Create(HardwareAccelerationType configuredBackend)
    {
        var configured = new EncodingOptions
        {
            HardwareAccelerationType = configuredBackend,

            // Does not exist, so the constructor's cache purge finds nothing and touches nothing.
            TranscodingTempPath = Path.Combine(Path.GetTempPath(), "tesserafin-fallback-" + Guid.NewGuid().ToString("N"))
        };

        var appPaths = Mock.Of<IServerApplicationPaths>();
        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.Setup(x => x.GetConfiguration("encoding")).Returns(configured);
        configurationManager.Setup(x => x.CommonApplicationPaths).Returns(appPaths);

        var manager = new TranscodeManager(
            NullLoggerFactory.Instance,
            Mock.Of<IFileSystem>(),
            appPaths,
            configurationManager.Object,
            Mock.Of<IUserManager>(),
            Mock.Of<ISessionManager>(),
            new EncodingHelper(
                appPaths,
                Mock.Of<IMediaEncoder>(),
                Mock.Of<ISubtitleEncoder>(),
                Mock.Of<IConfiguration>(),
                configurationManager.Object,
                Mock.Of<IPathManager>()),
            Mock.Of<IMediaEncoder>(),
            Mock.Of<IMediaSourceManager>(),
            Mock.Of<IAttachmentExtractor>(),
            new Tesserafin.MediaEncoding.Encoder.FfmpegProcessRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<Tesserafin.MediaEncoding.Encoder.FfmpegProcessRunner>.Instance));

        return (manager, configured, configurationManager);
    }
}
