using System;
using Moq;
using Tesserafin.Controller.BaseItemManager;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Model.Configuration;
using Xunit;

namespace Tesserafin.Controller.Tests
{
    public class BaseItemManagerTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ChannelItemFetchers_FollowMediaSourceDisplay(bool enableMediaSourceDisplay)
        {
            var item = new Book { ChannelId = Guid.NewGuid() };
            var channels = new Mock<IChannelManager>(MockBehavior.Strict);
            channels.Setup(x => x.EnableMediaSourceDisplay(item)).Returns(enableMediaSourceDisplay);
            var baseItemManager = new Tesserafin.Controller.BaseItemManager.BaseItemManager(
                Mock.Of<IServerConfigurationManager>(MockBehavior.Strict),
                new Lazy<IChannelManager>(() => channels.Object));

            Assert.Equal(!enableMediaSourceDisplay, baseItemManager.IsMetadataFetcherEnabled(item, null, "Test"));
            Assert.Equal(!enableMediaSourceDisplay, baseItemManager.IsImageFetcherEnabled(item, null, "Test"));
        }

        [Theory]
        [InlineData(typeof(Book), "LibraryEnabled", true)]
        [InlineData(typeof(Book), "LibraryDisabled", false)]
        [InlineData(typeof(MusicArtist), "Enabled", true)]
        [InlineData(typeof(MusicArtist), "ServerDisabled", false)]
        public void IsMetadataFetcherEnabled_ChecksOptions_ReturnsExpected(Type itemType, string fetcherName, bool expected)
        {
            BaseItem item = (BaseItem)Activator.CreateInstance(itemType)!;

            var libraryTypeOptions = itemType == typeof(Book)
                ? new TypeOptions
                {
                    Type = "Book",
                    MetadataFetchers = new[] { "LibraryEnabled" }
                }
                : null;

            var serverConfiguration = new ServerConfiguration();
            foreach (var typeConfig in serverConfiguration.MetadataOptions)
            {
                typeConfig.DisabledMetadataFetchers = new[] { "ServerDisabled" };
            }

            var serverConfigurationManager = new Mock<IServerConfigurationManager>();
            serverConfigurationManager.Setup(scm => scm.Configuration)
                .Returns(serverConfiguration);

            var baseItemManager = new Tesserafin.Controller.BaseItemManager.BaseItemManager(
                serverConfigurationManager.Object,
                new Lazy<IChannelManager>(() => throw new InvalidOperationException("Unexpected channel manager access.")));
            var actual = baseItemManager.IsMetadataFetcherEnabled(item, libraryTypeOptions, fetcherName);

            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(typeof(Book), "LibraryEnabled", true)]
        [InlineData(typeof(Book), "LibraryDisabled", false)]
        [InlineData(typeof(MusicArtist), "Enabled", true)]
        [InlineData(typeof(MusicArtist), "ServerDisabled", false)]
        public void IsImageFetcherEnabled_ChecksOptions_ReturnsExpected(Type itemType, string fetcherName, bool expected)
        {
            BaseItem item = (BaseItem)Activator.CreateInstance(itemType)!;

            var libraryTypeOptions = itemType == typeof(Book)
                ? new TypeOptions
                {
                    Type = "Book",
                    ImageFetchers = new[] { "LibraryEnabled" }
                }
                : null;

            var serverConfiguration = new ServerConfiguration();
            foreach (var typeConfig in serverConfiguration.MetadataOptions)
            {
                typeConfig.DisabledImageFetchers = new[] { "ServerDisabled" };
            }

            var serverConfigurationManager = new Mock<IServerConfigurationManager>();
            serverConfigurationManager.Setup(scm => scm.Configuration)
                .Returns(serverConfiguration);

            var baseItemManager = new Tesserafin.Controller.BaseItemManager.BaseItemManager(
                serverConfigurationManager.Object,
                new Lazy<IChannelManager>(() => throw new InvalidOperationException("Unexpected channel manager access.")));
            var actual = baseItemManager.IsImageFetcherEnabled(item, libraryTypeOptions, fetcherName);

            Assert.Equal(expected, actual);
        }
    }
}
