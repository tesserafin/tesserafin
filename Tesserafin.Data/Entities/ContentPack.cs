using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Tesserafin.Data.Enums;

namespace Tesserafin.Data.Entities;

/// <summary>
/// A high-level organisational and navigation lens over items the server already knows about.
/// </summary>
/// <remarks>
/// A content pack is not a library, a collection, a tag or a user view. It owns no files and
/// grants no access. There is one set of content packs per server; the entity deliberately has
/// no owner column so that per-user packs stay possible later without rewriting the model.
/// </remarks>
public class ContentPack
{
    /// <summary>
    /// The maximum length of a pack name.
    /// </summary>
    public const int NameMaxLength = 255;

    /// <summary>
    /// The maximum length of a pack description.
    /// </summary>
    public const int DescriptionMaxLength = 1024;

    /// <summary>
    /// Gets or sets the stable opaque identifier of the pack. Never changes on rename.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the user-facing name.
    /// </summary>
    /// <remarks>
    /// Required, trimmed and non-empty. Max length = 255.
    /// </remarks>
    [MaxLength(NameMaxLength)]
    [StringLength(NameMaxLength)]
    public required string Name { get; set; }

    /// <summary>
    /// Gets or sets the culture-independent normalized form of <see cref="Name"/>.
    /// </summary>
    /// <remarks>
    /// Required, unique across all packs. Produced by <see cref="Normalize"/>. Max length = 255.
    /// </remarks>
    [MaxLength(NameMaxLength)]
    [StringLength(NameMaxLength)]
    public required string NormalizedName { get; set; }

    /// <summary>
    /// Gets or sets the optional description.
    /// </summary>
    /// <remarks>
    /// Max length = 1024.
    /// </remarks>
    [MaxLength(DescriptionMaxLength)]
    [StringLength(DescriptionMaxLength)]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the position of the pack in the server's global pack ordering.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Gets or sets how the pack itself was created.
    /// </summary>
    public ContentPackOrigin Origin { get; set; }

    /// <summary>
    /// Gets or sets the UTC instant the pack was created.
    /// </summary>
    public DateTime DateCreated { get; set; }

    /// <summary>
    /// Gets or sets the memberships that belong to this pack.
    /// </summary>
#pragma warning disable CA2227 // Collection properties should be read only
    public ICollection<ContentPackMembership>? Memberships { get; set; }
#pragma warning restore CA2227

    /// <summary>
    /// Produces the culture-independent normalized form of a pack name.
    /// </summary>
    /// <param name="name">The name to normalize.</param>
    /// <returns>The trimmed, invariant upper-case form.</returns>
    /// <remarks>
    /// Follows the <c>User.NormalizedUsername</c> precedent: trim, then <c>ToUpperInvariant</c>.
    /// Upper-case invariant rather than lower-case, so that the result is stable for every
    /// server culture and CA1308 stays satisfied.
    /// </remarks>
    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.Trim().ToUpperInvariant();
    }
}
