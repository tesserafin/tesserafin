#nullable disable

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Entities;
using Tesserafin.Data.Entities;

namespace Tesserafin.Controller.Library
{
    /// <summary>
    /// Class BaseIntroProvider.
    /// </summary>
    public interface IIntroProvider
    {
        /// <summary>
        /// Gets the name.
        /// </summary>
        /// <value>The name.</value>
        string Name { get; }

        /// <summary>
        /// Gets the intros.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="user">The user.</param>
        /// <param name="cancellationToken">The request cancellation token.</param>
        /// <returns>The ordered intro candidates.</returns>
        Task<IEnumerable<IntroInfo>> GetIntrosAsync(BaseItem item, User user, CancellationToken cancellationToken);
    }
}
