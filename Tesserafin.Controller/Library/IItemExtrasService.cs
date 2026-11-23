using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Data.Entities;
using Tesserafin.Model.Entities;

namespace Tesserafin.Controller.Library;

/// <summary>Reads extras of an item and its alternate versions.</summary>
public interface IItemExtrasService
{
    /// <summary>Gets the item's extras in sort-name order.</summary>
    /// <param name="item">The item whose extras are requested.</param>
    /// <param name="user">The user whose restrictions apply, or <c>null</c> for an unfiltered read.</param>
    /// <param name="extraTypes">Optional types of extras to return.</param>
    /// <returns>The matching extras.</returns>
    IReadOnlyList<BaseItem> GetExtras(BaseItem item, User? user = null, IReadOnlyCollection<ExtraType>? extraTypes = null);
}
