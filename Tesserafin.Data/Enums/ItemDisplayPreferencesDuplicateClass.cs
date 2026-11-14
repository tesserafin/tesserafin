namespace Tesserafin.Data.Enums;

/// <summary>
/// Why an <see cref="Entities.ItemDisplayPreferences"/> row was archived: the kind of duplicate group
/// it belonged to when its scope was reduced to one row.
/// </summary>
public enum ItemDisplayPreferencesDuplicateClass
{
    /// <summary>
    /// Every row of the scope held the same settings.
    /// </summary>
    Identical = 0,

    /// <summary>
    /// The rows of the scope held different settings; only the kept row's settings stay active.
    /// </summary>
    Contradictory = 1,

    /// <summary>
    /// The scope is the global one (<see cref="System.Guid.Empty"/> item), which rows of unknown original
    /// scope were written to.
    /// </summary>
    Global = 2,
}
