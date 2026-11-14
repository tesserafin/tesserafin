namespace Tesserafin.Data.Enums;

/// <summary>
/// The way a user prefers the top level of the library to be arranged.
/// </summary>
/// <remarks>
/// This is a cross-client product preference, stored per user through
/// <c>UserConfiguration</c>. It changes no navigation in M1; clients consume it later.
/// </remarks>
public enum ContentPackBrowsingPreference
{
    /// <summary>
    /// Browse by media family first — Movies, Shows, Music, Photos. The default, and the
    /// behaviour every existing user keeps.
    /// </summary>
    MediaFamilyFirst = 0,

    /// <summary>
    /// Browse by content pack first — the household's own categories.
    /// </summary>
    ContentPackFirst = 1
}
