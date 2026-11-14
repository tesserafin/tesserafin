using System;
using Tesserafin.Controller.Entities;
using Tesserafin.Data.Entities;

namespace Tesserafin.Controller.Library
{
    /// <summary>
    /// Owns the "mark played / mark unplayed" commands for a user: which items an operation
    /// writes (the item itself, the leaves of a folder, the members of a collection, the other
    /// versions of a video), the transition each one goes through, and its persistence.
    /// </summary>
    /// <remarks>
    /// The entities no longer carry these commands (<c>BaseItem.MarkPlayed/MarkUnplayed</c> and
    /// their <c>Folder</c>, <c>BoxSet</c> and <c>Video</c> overrides were removed, #129 step 5).
    /// Callers are expected to have resolved the item through the user's visibility
    /// rules already: the service only writes descendants that the same user can see.
    /// </remarks>
    public interface IItemPlayedStateService
    {
        /// <summary>
        /// Marks <paramref name="item"/> played for <paramref name="user"/>, resetting the resume
        /// position. A folder marks its playable descendants instead of itself.
        /// </summary>
        /// <param name="user">The user whose state changes.</param>
        /// <param name="item">The item the user marks.</param>
        /// <param name="datePlayed">The date the user reports; when given, the play count is
        /// incremented and the date is recorded. When absent the existing date is kept, or now.</param>
        void MarkPlayed(User user, BaseItem item, DateTime? datePlayed);

        /// <summary>
        /// Marks <paramref name="item"/> unplayed for <paramref name="user"/>: play count, resume
        /// position and last played date are cleared. A folder marks its descendants instead.
        /// </summary>
        /// <param name="user">The user whose state changes.</param>
        /// <param name="item">The item the user marks.</param>
        void MarkUnplayed(User user, BaseItem item);

        /// <summary>
        /// Marks every other version of <paramref name="video"/> played with a cleared resume
        /// position, keeping their play counts. Used when one version is played to completion.
        /// </summary>
        /// <param name="user">The user whose state changes.</param>
        /// <param name="video">The version that was played.</param>
        void MarkOtherVersionsPlayed(User user, Video video);
    }
}
