using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tesserafin.Data.Entities;

namespace Tesserafin.Database.Implementations.ModelConfiguration;

/// <summary>
/// Chapter configuration.
/// </summary>
public class ChapterConfiguration : IEntityTypeConfiguration<Chapter>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<Chapter> builder)
    {
        builder.HasKey(e => new { e.ItemId, e.ChapterIndex });
        builder.HasOne(e => e.Item);
    }
}
