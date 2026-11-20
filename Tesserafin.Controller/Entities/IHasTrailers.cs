#nullable disable

#pragma warning disable CS1591

using System.Collections.Generic;
using Tesserafin.Model.Entities;

namespace Tesserafin.Controller.Entities
{
    public interface IHasTrailers : IHasProviderIds
    {
        /// <summary>
        /// Gets or sets the remote trailers.
        /// </summary>
        /// <value>The remote trailers.</value>
        IReadOnlyList<MediaUrl> RemoteTrailers { get; set; }
    }
}
