using System.Collections.Generic;
using Tesserafin.Data.Entities.Libraries;

namespace Tesserafin.Data.Interfaces
{
    /// <summary>
    /// An abstraction representing an entity that has companies.
    /// </summary>
    public interface IHasCompanies
    {
        /// <summary>
        /// Gets a collection containing this entity's companies.
        /// </summary>
        ICollection<Company> Companies { get; }
    }
}
