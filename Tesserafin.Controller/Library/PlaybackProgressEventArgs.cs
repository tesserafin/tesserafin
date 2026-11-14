#nullable disable

#pragma warning disable CA1002, CA2227, CS1591

using System;
using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Session;
using Tesserafin.Data.Entities;
using Tesserafin.Model.Dto;

namespace Tesserafin.Controller.Library
{
    /// <summary>
    /// Holds information about a playback progress event.
    /// </summary>
    public class PlaybackProgressEventArgs : EventArgs
    {
        public PlaybackProgressEventArgs()
        {
            Users = new List<User>();
        }

        public List<User> Users { get; set; }

        public long? PlaybackPositionTicks { get; set; }

        public BaseItem Item { get; set; }

        public BaseItemDto MediaInfo { get; set; }

        public string MediaSourceId { get; set; }

        public bool IsPaused { get; set; }

        public bool IsAutomated { get; set; }

        public string DeviceId { get; set; }

        public string DeviceName { get; set; }

        public string ClientName { get; set; }

        public string PlaySessionId { get; set; }

        public SessionInfo Session { get; set; }
    }
}
