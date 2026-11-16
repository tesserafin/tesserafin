using Moq;
using Tesserafin.Model.Globalization;
using Xunit;

namespace Tesserafin.Model.Tests.Globalization;

public sealed class LocalizationManagerExtensionsTests
{
    [Theory]
    [InlineData("English", "en")]
    [InlineData("eng", "en")]
    [InlineData("Klingon", "Klingon")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void NormalizeLanguage_MapsRecognisedLanguagesToTheirTwoLetterCode(string? language, string? expected)
    {
        var localization = new Mock<ILocalizationManager>();
        localization.Setup(l => l.FindLanguageInfo("English")).Returns(new CultureDto("English", "English", "en", ["eng"]));
        localization.Setup(l => l.FindLanguageInfo("eng")).Returns(new CultureDto("English", "English", "en", ["eng"]));

        Assert.Equal(expected, localization.Object.NormalizeLanguage(language));
    }
}
