using System.Collections.Generic;
using Tesserafin.Model.Configuration;

namespace Tesserafin.Controller.Library;

/// <summary>The configured locale and name sorting defaults for a catalogue.</summary>
/// <param name="Language">The default metadata language.</param>
/// <param name="CountryCode">The default metadata country code.</param>
/// <param name="SortRemoveWords">Words removed from sort names.</param>
/// <param name="SortRemoveCharacters">Characters removed from sort names.</param>
/// <param name="SortReplaceCharacters">Characters replaced with spaces in sort names.</param>
public sealed record ItemMetadataDefaults(
    string Language,
    string CountryCode,
    IReadOnlyList<string> SortRemoveWords,
    IReadOnlyList<string> SortRemoveCharacters,
    IReadOnlyList<string> SortReplaceCharacters)
{
    /// <summary>Gets the standard defaults for an item outside a catalogue.</summary>
    public static ItemMetadataDefaults BuiltIn { get; } = FromConfiguration(new ServerConfiguration());

    /// <summary>Reads defaults from the current server configuration.</summary>
    /// <param name="configuration">The server settings.</param>
    /// <returns>The catalogue defaults.</returns>
    public static ItemMetadataDefaults FromConfiguration(ServerConfiguration configuration)
        => new(
            configuration.PreferredMetadataLanguage,
            configuration.MetadataCountryCode,
            configuration.SortRemoveWords,
            configuration.SortRemoveCharacters,
            configuration.SortReplaceCharacters);
}
