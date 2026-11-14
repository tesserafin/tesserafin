#nullable disable

using Tesserafin.Controller.Library;
using Tesserafin.Data.Entities;

namespace Tesserafin.Controller.Sorting
{
    /// <summary>
    /// Represents a BaseItem comparer that requires a User to perform its comparison.
    /// </summary>
    public interface IUserBaseItemComparer : IBaseItemComparer
    {
        /// <summary>
        /// Gets or sets the user.
        /// </summary>
        /// <value>The user.</value>
        User User { get; set; }

        /// <summary>
        /// Gets or sets the user manager.
        /// </summary>
        /// <value>The user manager.</value>
        IUserManager UserManager { get; set; }

        /// <summary>
        /// Gets or sets the user data repository.
        /// </summary>
        /// <value>The user data repository.</value>
        IUserDataManager UserDataManager { get; set; }
    }
}
