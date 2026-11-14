using System;
using Tesserafin.Controller.Entities;

namespace Tesserafin.Server.Core.Library
{
    /// <summary>
    /// The state transitions of the "mark played / unplayed" commands, without persistence.
    /// </summary>
    public static class PlayedStateTransitions
    {
        /// <summary>
        /// Applies "played": at least one play (one more when a date is reported), the resume
        /// position cleared, the reported date or else the existing one or else <paramref name="now"/>.
        /// </summary>
        /// <param name="data">The user's state for the item.</param>
        /// <param name="datePlayed">The date the user reports, if any.</param>
        /// <param name="now">The current UTC time.</param>
        public static void MarkPlayed(UserItemData data, DateTime? datePlayed, DateTime now)
        {
            ArgumentNullException.ThrowIfNull(data);

            if (datePlayed.HasValue)
            {
                data.PlayCount++;
            }

            data.PlayCount = Math.Max(data.PlayCount, 1);
            data.PlaybackPositionTicks = 0;
            data.LastPlayedDate = datePlayed ?? data.LastPlayedDate ?? now;
            data.Played = true;
        }

        /// <summary>
        /// Applies "unplayed": no plays, no resume position, no last played date.
        /// </summary>
        /// <param name="data">The user's state for the item.</param>
        public static void MarkUnplayed(UserItemData data)
        {
            ArgumentNullException.ThrowIfNull(data);

            data.PlayCount = 0;
            data.PlaybackPositionTicks = 0;
            data.LastPlayedDate = null;
            data.Played = false;
        }
    }
}
