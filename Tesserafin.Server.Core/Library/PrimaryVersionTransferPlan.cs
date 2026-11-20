using System.Collections.Generic;
using Tesserafin.Controller.Entities;

namespace Tesserafin.Server.Core.Library;

/// <summary>The items to move from a replaced primary version to its replacement.</summary>
/// <param name="Previous">The primary version being replaced.</param>
/// <param name="Replacement">The new primary version.</param>
/// <param name="Items">The alternate versions and parts to move.</param>
internal sealed record PrimaryVersionTransferPlan(Video Previous, Video Replacement, IReadOnlyList<BaseItem> Items);
