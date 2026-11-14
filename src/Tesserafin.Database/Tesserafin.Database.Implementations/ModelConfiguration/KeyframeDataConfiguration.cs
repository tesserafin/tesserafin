using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tesserafin.Data.Entities;

namespace Tesserafin.Database.Implementations.ModelConfiguration;

/// <summary>
/// KeyframeData Configuration.
/// </summary>
public class KeyframeDataConfiguration : IEntityTypeConfiguration<KeyframeData>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<KeyframeData> builder)
    {
        builder.HasKey(e => e.ItemId);
        builder.HasOne(e => e.Item).WithMany().HasForeignKey(e => e.ItemId);
    }
}
