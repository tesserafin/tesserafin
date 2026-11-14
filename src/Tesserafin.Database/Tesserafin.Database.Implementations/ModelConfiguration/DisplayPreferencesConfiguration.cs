using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tesserafin.Data.Entities;

namespace Tesserafin.Database.Implementations.ModelConfiguration
{
    /// <summary>
    /// FluentAPI configuration for the DisplayPreferencesConfiguration entity.
    /// </summary>
    public class DisplayPreferencesConfiguration : IEntityTypeConfiguration<DisplayPreferences>
    {
        /// <inheritdoc/>
        public void Configure(EntityTypeBuilder<DisplayPreferences> builder)
        {
            builder
                .HasMany(d => d.HomeSections)
                .WithOne()
                .OnDelete(DeleteBehavior.Cascade);

            builder
                .HasIndex(entity => new { entity.UserId, entity.ItemId, entity.Client })
                .IsUnique();
        }
    }
}
