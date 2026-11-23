using Moq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Globalization;
using Xunit;

namespace Tesserafin.Controller.Tests.Entities;

/// <summary>
/// Rating scores come from the localization the caller passes, not from a static on the entity.
/// </summary>
public sealed class BaseItemRatingTests
{
    public BaseItemRatingTests()
    {
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.GetLibraryOptions(It.IsAny<BaseItem>())).Returns(new LibraryOptions());
        libraryManager.Setup(l => l.GetCollectionFolders(It.IsAny<BaseItem>())).Returns([]);
    }

    [Fact]
    public void OnMetadataChanged_ScoresTheRatingWithTheGivenLocalization()
    {
        var movie = new Movie { OfficialRating = "R" };
        var localization = Mock.Of<ILocalizationManager>(l => l.GetRatingScore("R", "US") == new ParentalRatingScore(17, 2));

        movie.OnMetadataChanged(localization);

        Assert.Equal(17, movie.InheritedParentalRatingValue);
        Assert.Equal(2, movie.InheritedParentalRatingSubValue);
    }

    [Fact]
    public void UpdateRatingToItems_TakesTheLowestScoredChildRating()
    {
        var boxSet = new BoxSet();
        var children = new BaseItem[] { new Movie { OfficialRating = "R" }, new Movie { OfficialRating = "PG" } };
        var localization = new Mock<ILocalizationManager>();
        localization.Setup(l => l.GetRatingScore("R", "US")).Returns(new ParentalRatingScore(17, null));
        localization.Setup(l => l.GetRatingScore("PG", "US")).Returns(new ParentalRatingScore(10, null));

        Assert.True(boxSet.UpdateRatingToItems(children, localization.Object));
        Assert.Equal("PG", boxSet.OfficialRating);
    }
}
