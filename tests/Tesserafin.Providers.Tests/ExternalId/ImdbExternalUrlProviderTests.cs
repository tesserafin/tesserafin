using System;
using Moq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Library;
using Tesserafin.Model.Entities;
using Tesserafin.Providers.Movies;
using Xunit;

namespace Tesserafin.Providers.Tests.ExternalId
{
    public sealed class ImdbExternalUrlProviderTests
    {
        private readonly ImdbExternalUrlProvider _provider = new();
        private readonly Mock<IItemReadServices> _reads = new();

        [Fact]
        public void GetExternalUrls_MovieWithImdbId_ReturnsCorrectUrl()
        {
            var movie = new Movie();
            movie.SetProviderId(MetadataProvider.Imdb, "tt1234567");

            var urls = _provider.GetExternalUrls(movie);

            Assert.Contains("https://www.imdb.com/title/tt1234567", urls);
        }

        [Fact]
        public void GetExternalUrls_SeriesWithImdbId_ReturnsCorrectUrl()
        {
            var series = new Series();
            series.SetProviderId(MetadataProvider.Imdb, "tt7654321");

            var urls = _provider.GetExternalUrls(series);

            Assert.Contains("https://www.imdb.com/title/tt7654321", urls);
        }

        [Fact]
        public void GetExternalUrls_EpisodeWithImdbId_ReturnsCorrectUrl()
        {
            var episode = new Episode();
            episode.SetProviderId(MetadataProvider.Imdb, "tt9999999");

            var urls = _provider.GetExternalUrls(episode);

            Assert.Contains("https://www.imdb.com/title/tt9999999", urls);
        }

        [Fact]
        public void GetExternalUrls_SeasonWithSeriesImdbId_ReturnsSeasonEpisodesUrl()
        {
            var series = new Series { Id = Guid.NewGuid() };
            series.SetProviderId(MetadataProvider.Imdb, "tt1234567");

            var season = new Season { IndexNumber = 2, SeriesId = series.Id, ReadServices = _reads.Object };
            _reads.Setup(m => m.GetItemById(series.Id)).Returns(series);

            var urls = _provider.GetExternalUrls(season);

            Assert.Contains("https://www.imdb.com/title/tt1234567/episodes/?season=2", urls);
        }

        [Fact]
        public void GetExternalUrls_SeasonWithNoSeriesImdbId_ReturnsNoUrl()
        {
            var series = new Series { Id = Guid.NewGuid() };
            var season = new Season { IndexNumber = 1, SeriesId = series.Id, ReadServices = _reads.Object };
            _reads.Setup(m => m.GetItemById(series.Id)).Returns(series);

            var urls = _provider.GetExternalUrls(season);

            Assert.Empty(urls);
        }

        [Fact]
        public void GetExternalUrls_SeasonWithNoIndexNumber_ReturnsNoUrl()
        {
            var series = new Series { Id = Guid.NewGuid() };
            series.SetProviderId(MetadataProvider.Imdb, "tt1234567");
            var season = new Season { IndexNumber = null, SeriesId = series.Id, ReadServices = _reads.Object };
            _reads.Setup(m => m.GetItemById(series.Id)).Returns(series);

            var urls = _provider.GetExternalUrls(season);

            Assert.Empty(urls);
        }

        [Fact]
        public void GetExternalUrls_SeasonWithUnknownSeriesId_ReturnsNoUrl()
        {
            var season = new Season { IndexNumber = 1, SeriesId = Guid.NewGuid(), ReadServices = _reads.Object };
            _reads.Setup(m => m.GetItemById(It.IsAny<Guid>())).Returns((BaseItem?)null);

            var urls = _provider.GetExternalUrls(season);

            Assert.Empty(urls);
        }

        [Fact]
        public void GetExternalUrls_ItemWithNoImdbId_ReturnsNoUrl()
        {
            var movie = new Movie();

            var urls = _provider.GetExternalUrls(movie);

            Assert.Empty(urls);
        }
    }
}
