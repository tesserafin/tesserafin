using System;
using System.Collections.Generic;
using Tesserafin.Controller.Entities;

namespace Tesserafin.Server.Core.Library
{
    /// <summary>
    /// Internal lifecycle port for the item lookup cache owned by <see cref="ItemLookupService"/>.
    /// Deliberately excluded from the public, read-only <see cref="Tesserafin.Controller.Library.IItemLookupService"/>
    /// contract - item writers register entries and deletion/scan owners invalidate them.
    /// </summary>
    /// <remarks>
    /// Kept <c>internal</c> rather than public: <see cref="LibraryManager"/> lives in the same
    /// assembly (Tesserafin.Server.Core) as <see cref="ItemLookupService"/>, and
    /// Tesserafin.Server.Core.Properties.AssemblyInfo already grants
    /// <c>InternalsVisibleTo("Tesserafin.Server.Implementations.Tests")</c>, so the characterization
    /// test suite can see this type too.
    /// </remarks>
    internal interface IItemCacheStore
    {
        /// <summary>
        /// Adds or replaces <paramref name="item"/> in the cache if it is cacheable; otherwise a no-op.
        /// </summary>
        /// <param name="item">The item to register.</param>
        void Register(BaseItem item);

        /// <summary>
        /// Removes a single item from the cache, if present.
        /// </summary>
        /// <param name="id">The item id.</param>
        void Remove(Guid id);

        /// <summary>
        /// Removes multiple items from the cache, if present.
        /// </summary>
        /// <param name="ids">The item ids.</param>
        void RemoveRange(IEnumerable<Guid> ids);
    }
}
