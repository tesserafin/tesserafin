using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tesserafin.Data.Entities;

namespace Tesserafin.Database.Implementations.ModelConfiguration
{
    /// <summary>
    /// FluentAPI configuration for the TrickplayInfo entity.
    /// </summary>
    public class TrickplayInfoConfiguration : IEntityTypeConfiguration<TrickplayInfo>
    {
        /// <inheritdoc/>
        public void Configure(EntityTypeBuilder<TrickplayInfo> builder)
        {
            builder.HasKey(info => new { info.ItemId, info.Width });
        }
    }
}
