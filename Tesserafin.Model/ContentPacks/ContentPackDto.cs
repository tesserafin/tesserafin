using System;
using Tesserafin.Data.Enums;

namespace Tesserafin.Model.ContentPacks;

/// <summary>
/// A content pack as one caller may see it.
/// </summary>
/// <remarks>
/// Everything derived from membership is computed for the calling user. The raw membership count
/// is never exposed, and the representative item is chosen only among items that user may see.
/// </remarks>
public class ContentPackDto
{
    /// <summary>
    /// Gets or sets the stable opaque identifier. Unchanged by a rename.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the name.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Gets or sets the optional description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the position in the server's global pack ordering.
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
    /// Gets or sets the number of items in the pack that the calling user may see.
    /// </summary>
    public int VisibleItemCount { get; set; }

    /// <summary>
    /// Gets or sets the item the client may use for representative artwork.
    /// </summary>
    /// <remarks>
    /// Null when the caller can see nothing in the pack. Never an item the caller cannot see.
    /// </remarks>
    public Guid? RepresentativeItemId { get; set; }
}
