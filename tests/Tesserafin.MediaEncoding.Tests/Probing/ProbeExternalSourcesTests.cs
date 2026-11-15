using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.MediaEncoding.Encoder;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.IO;
using Tesserafin.Model.MediaInfo;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Probing
{
    public class ProbeExternalSourcesTests
    {
        [Fact]
        public void GetExtraArguments_Forwards_UserAgent()
        {
            var encoder = new MediaEncoder(
                Mock.Of<ILogger<MediaEncoder>>(),
                Mock.Of<IServerConfigurationManager>(),
                Mock.Of<IFileSystem>(),
                Mock.Of<IBlurayExaminer>(),
                Mock.Of<ILocalizationManager>(),
                new ConfigurationBuilder().Build(),
                Mock.Of<IServerConfigurationManager>(),
                new FfmpegProcessRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<Tesserafin.MediaEncoding.Encoder.FfmpegProcessRunner>.Instance));

            var userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
            var req = new Tesserafin.Controller.MediaEncoding.MediaInfoRequest()
            {
                MediaSource = new Tesserafin.Model.Dto.MediaSourceInfo
                {
                    Path = "/path/to/stream",
                    Protocol = MediaProtocol.Http,
                    RequiredHttpHeaders = new Dictionary<string, string>()
                    {
                        { "User-Agent", userAgent },
                    }
                },
                ExtractChapters = false,
                MediaType = Tesserafin.Model.Dlna.DlnaProfileType.Video,
            };

            var extraArg = encoder.GetExtraArguments(req);

            Assert.Contains($"-user_agent \"{userAgent}\"", extraArg, StringComparison.InvariantCulture);
        }
    }
}
