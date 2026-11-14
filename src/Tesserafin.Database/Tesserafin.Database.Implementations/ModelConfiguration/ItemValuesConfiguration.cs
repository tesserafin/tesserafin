using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tesserafin.Data.Entities;

namespace Tesserafin.Database.Implementations.ModelConfiguration;

/// <summary>
/// itemvalues Configuration.
/// </summary>
public class ItemValuesConfiguration : IEntityTypeConfiguration<ItemValue>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<ItemValue> builder)
    {
        builder.HasKey(e => e.ItemValueId);
        builder.HasIndex(e => new { e.Type, e.CleanValue });
        builder.HasIndex(e => new { e.Type, e.Value }).IsUnique();
    }
}
