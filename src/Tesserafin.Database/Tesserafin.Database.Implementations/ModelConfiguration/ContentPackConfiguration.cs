using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tesserafin.Data.Entities;

namespace Tesserafin.Database.Implementations.ModelConfiguration;

/// <summary>
/// FluentAPI configuration for the ContentPack entity.
/// </summary>
public class ContentPackConfiguration : IEntityTypeConfiguration<ContentPack>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<ContentPack> builder)
    {
        builder.HasKey(e => e.Id);

        // The unique constraint, not a read-before-write, is what decides a same-name race.
        builder.HasIndex(e => e.NormalizedName).IsUnique();

        // The pack list is ordered globally; this index keeps that read cheap.
        builder.HasIndex(e => e.SortOrder);
    }
}
