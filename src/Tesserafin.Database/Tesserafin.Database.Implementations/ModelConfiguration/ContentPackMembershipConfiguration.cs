using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tesserafin.Data.Entities;

namespace Tesserafin.Database.Implementations.ModelConfiguration;

/// <summary>
/// FluentAPI configuration for the ContentPackMembership entity.
/// </summary>
public class ContentPackMembershipConfiguration : IEntityTypeConfiguration<ContentPackMembership>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<ContentPackMembership> builder)
    {
        // The composite key is the uniqueness guarantee that makes "add the same item twice"
        // idempotent at the storage layer, and it doubles as the pack-to-items index.
        builder.HasKey(e => new { e.PackId, e.ItemId });

        // Item-to-packs lookup, and the index the item-deletion cleanup uses.
        builder.HasIndex(e => e.ItemId);

        builder
            .HasOne(e => e.Pack)
            .WithMany(e => e.Memberships)
            .HasForeignKey(e => e.PackId)
            .OnDelete(DeleteBehavior.Cascade);

        // No inverse navigation on BaseItemEntity: a pack must never be reachable from an item
        // in a way that could make an existing item query return one. Cascade here is a
        // database-level backstop; ItemPersistenceService also removes the rows explicitly.
        builder
            .HasOne(e => e.Item)
            .WithMany()
            .HasForeignKey(e => e.ItemId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
