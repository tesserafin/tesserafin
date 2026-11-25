using System;
using Microsoft.Extensions.Logging.Abstractions;
using Tesserafin.MediaEncoding.Encoder;
using Tesserafin.Model.Entities;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests
{
    public class EncoderValidatorTests
    {
        private readonly EncoderValidator _encoderValidator = new EncoderValidator(new NullLogger<EncoderValidatorTests>(), "ffmpeg", new FfmpegProcessRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<Tesserafin.MediaEncoding.Encoder.FfmpegProcessRunner>.Instance));

        [Theory]
        [ClassData(typeof(GetFFmpegVersionTestData))]
        public void GetFFmpegVersionTest(string versionOutput, Version? version)
        {
            Assert.Equal(version, _encoderValidator.GetFFmpegVersionInternal(versionOutput));
        }

        [Theory]
        [InlineData(EncoderValidatorTestsData.FFmpegV701Output, true)]
        [InlineData(EncoderValidatorTestsData.FFmpegV611Output, true)]
        [InlineData(EncoderValidatorTestsData.FFmpegV60Output, true)]
        [InlineData(EncoderValidatorTestsData.FFmpegV512Output, true)]
        [InlineData(EncoderValidatorTestsData.FFmpegV44Output, true)]
        [InlineData(EncoderValidatorTestsData.FFmpegV432Output, false)]
        [InlineData(EncoderValidatorTestsData.FFmpegGitUnknownOutput2, true)]
        [InlineData(EncoderValidatorTestsData.FFmpegGitUnknownOutput, false)]
        public void ValidateVersionInternalTest(string versionOutput, bool valid)
        {
            Assert.Equal(valid, _encoderValidator.ValidateVersionInternal(versionOutput));
        }

        [Fact]
        public void GetDiscImageReaders_ReadsTheBlurayInputProtocolAndTheDvdvideoDemuxer()
        {
            const string Protocols = "Supported file protocols:\nInput:\n  async\n  bluray\n  file\nOutput:\n  file\n";
            const string Demuxers = "Formats:\n D.. = Demuxing supported\n ---\n D   dvdvideo        DVD-Video\n D   mpegts          MPEG-TS\n";

            Assert.Equal([IsoType.BluRay, IsoType.Dvd], EncoderValidator.GetDiscImageReadersInternal(Protocols, Demuxers));
        }

        [Fact]
        public void GetDiscImageReaders_IgnoresOutputProtocolsAndSimilarNames()
        {
            // A distribution FFmpeg 6.1: libbluray, but no dvdvideo demuxer (it arrived in 7.0).
            const string Protocols = "Supported file protocols:\nInput:\n  file\n  bluray\nOutput:\n  file\n";
            const string Demuxers = "Formats:\n ---\n D   dv              DV (Digital Video)\n D   dvbsub          raw dvbsub\n";
            Assert.Equal([IsoType.BluRay], EncoderValidator.GetDiscImageReadersInternal(Protocols, Demuxers));

            // A protocol that exists for output only does not read an image.
            Assert.Empty(EncoderValidator.GetDiscImageReadersInternal("Input:\n  file\nOutput:\n  bluray\n", string.Empty));
            Assert.Empty(EncoderValidator.GetDiscImageReadersInternal(null, null));
        }

        private sealed class GetFFmpegVersionTestData : TheoryData<string, Version?>
        {
            public GetFFmpegVersionTestData()
            {
                Add(EncoderValidatorTestsData.FFmpegV701Output, new Version(7, 0, 1));
                Add(EncoderValidatorTestsData.FFmpegV611Output, new Version(6, 1, 1));
                Add(EncoderValidatorTestsData.FFmpegV60Output, new Version(6, 0));
                Add(EncoderValidatorTestsData.FFmpegV512Output, new Version(5, 1, 2));
                Add(EncoderValidatorTestsData.FFmpegV44Output, new Version(4, 4));
                Add(EncoderValidatorTestsData.FFmpegV432Output, new Version(4, 3, 2));
                Add(EncoderValidatorTestsData.FFmpegGitUnknownOutput2, new Version(4, 4));
                Add(EncoderValidatorTestsData.FFmpegGitUnknownOutput, null);
            }
        }
    }
}
