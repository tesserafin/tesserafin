#nullable disable

using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Sorting;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Querying;

namespace Tesserafin.Server.Core.Sorting
{
    /// <summary>
    /// Class PlayCountComparer.
    /// </summary>
    public class PlayCountComparer : IUserBaseItemComparer
    {
        /// <summary>
        /// Gets or sets the user.
        /// </summary>
        /// <value>The user.</value>
        public User User { get; set; }

        /// <summary>
        /// Gets the name.
        /// </summary>
        /// <value>The name.</value>
        public ItemSortBy Type => ItemSortBy.PlayCount;

        /// <summary>
        /// Gets or sets the user data manager.
        /// </summary>
        /// <value>The user data manager.</value>
        public IUserDataManager UserDataManager { get; set; }

        /// <summary>
        /// Compares the specified x.
        /// </summary>
        /// <param name="x">The x.</param>
        /// <param name="y">The y.</param>
        /// <returns>System.Int32.</returns>
        public int Compare(BaseItem x, BaseItem y)
        {
            return GetValue(x).CompareTo(GetValue(y));
        }

        /// <summary>
        /// Gets the date.
        /// </summary>
        /// <param name="x">The x.</param>
        /// <returns>DateTime.</returns>
        private int GetValue(BaseItem x)
        {
            var userdata = UserDataManager.GetUserData(User, x);

            return userdata is null ? 0 : userdata.PlayCount;
        }
    }
}
