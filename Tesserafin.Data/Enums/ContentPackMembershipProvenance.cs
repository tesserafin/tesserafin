namespace Tesserafin.Data.Enums;

/// <summary>
/// Records why an item is a member of a content pack.
/// </summary>
/// <remarks>
/// Stored as an integer so that the values that no producer exists for yet can be
/// written later without a schema migration. Only <see cref="Manual"/> and
/// <see cref="SystemSeed"/> are produced today.
/// </remarks>
public enum ContentPackMembershipProvenance
{
    /// <summary>
    /// A person put the item in the pack.
    /// </summary>
    Manual = 0,

    /// <summary>
    /// The membership was created by first-run seeding or a built-in default.
    /// </summary>
    SystemSeed = 1,

    /// <summary>
    /// A deterministic, inspectable rule matched the item. Not produced yet.
    /// </summary>
    Rule = 2,

    /// <summary>
    /// A metadata provider proposed the membership. Not produced yet.
    /// </summary>
    ProviderSuggestion = 3,

    /// <summary>
    /// A plugin proposed the membership. Not produced yet.
    /// </summary>
    PluginSuggestion = 4
}
