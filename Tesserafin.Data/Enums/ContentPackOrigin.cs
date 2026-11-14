namespace Tesserafin.Data.Enums;

/// <summary>
/// Records how a content pack itself came into existence.
/// </summary>
/// <remarks>
/// Deliberately distinct from <see cref="ContentPackMembershipProvenance"/>. Membership
/// provenance answers "why is this item in this pack" and has to carry rule and suggestion
/// values later; a pack's origin answers "who created this pack", and only a person or the
/// server's own seeding can ever create one. Sharing one enumeration between the two would
/// make <c>ProviderSuggestion</c> a legal pack origin, which the contract forbids.
/// </remarks>
public enum ContentPackOrigin
{
    /// <summary>
    /// A person created the pack.
    /// </summary>
    Manual = 0,

    /// <summary>
    /// The pack was created by first-run seeding or a built-in default.
    /// </summary>
    SystemSeed = 1
}
