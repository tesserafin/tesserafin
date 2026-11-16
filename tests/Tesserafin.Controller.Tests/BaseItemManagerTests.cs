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

            var baseItemManager = new Tesserafin.Controller.BaseItemManager.BaseItemManager(serverConfigurationManager.Object, new Lazy<IChannelManager>(Mock.Of<IChannelManager>));
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

            var baseItemManager = new Tesserafin.Controller.BaseItemManager.BaseItemManager(serverConfigurationManager.Object, new Lazy<IChannelManager>(Mock.Of<IChannelManager>));
            var actual = baseItemManager.IsImageFetcherEnabled(item, libraryTypeOptions, fetcherName);

            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void ChannelItem_FetchersFollowTheInjectedChannelManager(bool displaysMediaSources, bool expected)
        {
            var item = new Book { ChannelId = Guid.NewGuid() };
            var channelManager = new Mock<IChannelManager>(MockBehavior.Strict);
            channelManager.Setup(c => c.EnableMediaSourceDisplay(item)).Returns(displaysMediaSources);

            var baseItemManager = new Tesserafin.Controller.BaseItemManager.BaseItemManager(
                Mock.Of<IServerConfigurationManager>(),
                new Lazy<IChannelManager>(() => channelManager.Object));

            Assert.Equal(expected, baseItemManager.IsMetadataFetcherEnabled(item, null, "Any"));
            Assert.Equal(expected, baseItemManager.IsImageFetcherEnabled(item, null, "Any"));
            channelManager.Verify(c => c.EnableMediaSourceDisplay(item), Times.Exactly(2));
        }

        [Fact]
        public void LibraryItem_NeverResolvesTheChannelManager()
        {
            var serverConfigurationManager = new Mock<IServerConfigurationManager>();
            serverConfigurationManager.Setup(scm => scm.Configuration).Returns(new ServerConfiguration());
            var channelManager = new Lazy<IChannelManager>(() => throw new InvalidOperationException("resolved"));

            var baseItemManager = new Tesserafin.Controller.BaseItemManager.BaseItemManager(serverConfigurationManager.Object, channelManager);

            Assert.True(baseItemManager.IsMetadataFetcherEnabled(new Book(), null, "Any"));
            Assert.False(channelManager.IsValueCreated);
        }
    }
}
