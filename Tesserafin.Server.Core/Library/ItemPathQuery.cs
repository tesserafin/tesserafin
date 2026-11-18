using System.Linq;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Persistence;
using Tesserafin.Data.Enums;

namespace Tesserafin.Server.Core.Library
{
    /// <summary>
    /// The shared catalogue search for a stored path.
    /// </summary>
    internal static class ItemPathQuery
    {
        /// <summary>
        /// Finds the newest item at a path.
        /// </summary>
        /// <param name="repository">The catalogue repository.</param>
        /// <param name="path">The path.</param>
        /// <param name="isFolder">Whether the item must be a folder; <c>null</c> for either.</param>
        /// <returns>The matching item, or null.</returns>
        public static BaseItem? Find(IItemRepository repository, string path, bool? isFolder)
        {
            return repository.GetItemList(new InternalItemsQuery
            {
                Path = path,
                IsFolder = isFolder,
                OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)],
                Limit = 1,
                DtoOptions = new DtoOptions(true)
            }).FirstOrDefault();
        }
    }
}
