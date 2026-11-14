using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Data.Enums;

namespace Tesserafin.Server.Core.Library
{
    /// <summary>
    /// The query of <see cref="LibraryManager.FindByPath"/>, shared with the path search of
    /// <see cref="TagVisibilityService"/>, which must not reference <see cref="LibraryManager"/>.
    /// </summary>
    internal static class ItemPathQuery
    {
        /// <summary>
        /// Creates the query for the newest item at a path.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="isFolder">Whether the item must be a folder; <c>null</c> for either.</param>
        /// <returns>The query.</returns>
        public static InternalItemsQuery Create(string path, bool? isFolder)
        {
            return new InternalItemsQuery
            {
                Path = path,
                IsFolder = isFolder,
                OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)],
                Limit = 1,
                DtoOptions = new DtoOptions(true)
            };
        }
    }
}
