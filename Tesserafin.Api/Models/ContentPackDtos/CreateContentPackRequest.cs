using System.ComponentModel.DataAnnotations;
using Tesserafin.Data.Entities;

namespace Tesserafin.Api.Models.ContentPackDtos;

/// <summary>
/// The body of a create content pack request.
/// </summary>
public class CreateContentPackRequest
{
    /// <summary>
    /// Gets or sets the name of the pack. Trimmed, non-empty and unique once normalized.
    /// </summary>
    [Required]
    [MinLength(1)]
    [MaxLength(ContentPack.NameMaxLength)]
    public required string Name { get; set; }

    /// <summary>
    /// Gets or sets the optional description.
    /// </summary>
    [MaxLength(ContentPack.DescriptionMaxLength)]
    public string? Description { get; set; }
}
