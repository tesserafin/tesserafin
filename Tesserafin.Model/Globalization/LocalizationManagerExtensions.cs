namespace Tesserafin.Model.Globalization;

/// <summary>
/// Language helpers on <see cref="ILocalizationManager"/>.
/// </summary>
public static class LocalizationManagerExtensions
{
    /// <summary>
    /// Normalises a language name or code to its two-letter ISO 639-1 code when it is recognised.
    /// </summary>
    /// <param name="localization">The localization manager.</param>
    /// <param name="language">The language, as a provider or a user wrote it.</param>
    /// <returns>The two-letter code; the input unchanged when it is empty or not recognised.</returns>
    public static string? NormalizeLanguage(this ILocalizationManager localization, string? language)
        => string.IsNullOrEmpty(language)
            ? language
            : localization.FindLanguageInfo(language)?.TwoLetterISOLanguageName ?? language;
}
