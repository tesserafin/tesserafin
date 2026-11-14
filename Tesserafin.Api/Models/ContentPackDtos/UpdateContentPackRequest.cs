using System.ComponentModel.DataAnnotations;
using Tesserafin.Data.Entities;

namespace Tesserafin.Api.Models.ContentPackDtos;

/// <summary>
/// The body of an update content pack request.
/// </summary>
/// <remarks>
/// Metadata only. The pack identifier never changes, so posting the same body twice produces the
/// same pack.
/// </remarks>
public class UpdateContentPackRequest
{
    /// <summary>
    /// Gets or sets the new name. Trimmed, non-empty and unique once normalized.
    /// </summary>
    [Required]
    [MinLength(1)]
    [MaxLength(ContentPack.NameMaxLength)]
    public required string Name { get; set; }

    /// <summary>
    /// Gets or sets the new optional description.
    /// </summary>
    [MaxLength(ContentPack.DescriptionMaxLength)]
    public string? Description { get; set; }
}
