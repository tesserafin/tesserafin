using System;
using Tesserafin.Data.Enums;

namespace Tesserafin.Data.Entities;

/// <summary>
/// The relation putting one item in one content pack.
/// </summary>
/// <remarks>
/// Membership is a relation, never a capability and never a file operation. The row carries no
/// authorization meaning: item visibility is decided by the ordinary item query path.
/// </remarks>
public class ContentPackMembership
{
    /// <summary>
    /// Gets or sets the id of the pack.
    /// </summary>
    public Guid PackId { get; set; }

    /// <summary>
    /// Gets or sets the id of the member item.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets why the item is in the pack.
    /// </summary>
    public ContentPackMembershipProvenance Provenance { get; set; }

    /// <summary>
    /// Gets or sets the UTC instant the membership was created.
    /// </summary>
    public DateTime DateCreated { get; set; }

    /// <summary>
    /// Gets or sets the referenced <see cref="ContentPack"/>.
    /// </summary>
    public ContentPack? Pack { get; set; }

    /// <summary>
    /// Gets or sets the referenced <see cref="BaseItemEntity"/>.
    /// </summary>
    /// <remarks>
    /// One-way on purpose: <see cref="BaseItemEntity"/> gets no inverse collection, so no
    /// existing item query can ever see a pack, and a pack can never look like an item.
    /// </remarks>
    public BaseItemEntity? Item { get; set; }
}
