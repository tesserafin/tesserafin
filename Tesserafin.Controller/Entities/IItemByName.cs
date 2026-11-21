#pragma warning disable CS1591

namespace Tesserafin.Controller.Entities
{
    /// <summary>
    /// Marker interface.
    /// </summary>
    public interface IItemByName
    {
        /// <summary>Applies the filters for items associated with this entry.</summary>
        /// <param name="query">The query to filter.</param>
        /// <returns>Whether this entry represents a valid filter.</returns>
        bool TryApplyTaggedItemsFilter(InternalItemsQuery query);
    }

    public interface IHasDualAccess : IItemByName
    {
        bool IsAccessedByName { get; }
    }
}
