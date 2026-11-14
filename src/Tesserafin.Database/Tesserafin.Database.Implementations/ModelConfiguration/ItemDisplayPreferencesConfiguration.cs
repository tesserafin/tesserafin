using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tesserafin.Data.Entities;

namespace Tesserafin.Database.Implementations.ModelConfiguration
{
    /// <summary>
    /// FluentAPI configuration for the ItemDisplayPreferences entity.
    /// </summary>
    public class ItemDisplayPreferencesConfiguration : IEntityTypeConfiguration<ItemDisplayPreferences>
    {
        /// <inheritdoc/>
        public void Configure(EntityTypeBuilder<ItemDisplayPreferences> builder)
        {
            // One row per scope. The migration that adds the index archives and removes the duplicates
            // written before it (see docs/data-model-reliability.md).
            builder
                .HasIndex(entity => new { entity.UserId, entity.ItemId, entity.Client })
                .IsUnique();
        }
    }
}
