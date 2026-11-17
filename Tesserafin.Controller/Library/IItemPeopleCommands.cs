using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Entities;

namespace Tesserafin.Controller.Library;

/// <summary>Owns credit replacement and associated person metadata writes.</summary>
public interface IItemPeopleCommands
{
    /// <summary>Replaces credits and materializes their person records.</summary>
    /// <param name="item">The credited item.</param>
    /// <param name="people">The replacement credits; null leaves them unchanged.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The completed credit update.</returns>
    Task UpdateAsync(BaseItem item, IReadOnlyList<PersonInfo>? people, CancellationToken cancellationToken);
}
