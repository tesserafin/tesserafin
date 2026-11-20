#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaSegments;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;

namespace Tesserafin.Controller.Entities
{
    public interface IHasMediaSources
    {
        Guid Id { get; set; }

        long? RunTimeTicks { get; set; }

        string Path { get; }

        /// <summary>
        /// Gets the media sources.
        /// </summary>
        /// <param name="enablePathSubstitution"><c>true</c> to enable path substitution, <c>false</c> to not.</param>
        /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
        /// <param name="mediaSegmentManager">Instance of the <see cref="IMediaSegmentManager"/> interface.</param>
        /// <param name="context">The catalogue and live-source reads.</param>
        /// <returns>A list of media sources.</returns>
        IReadOnlyList<MediaSourceInfo> GetMediaSources(bool enablePathSubstitution, IMediaSourceManager mediaSourceManager, IMediaSegmentManager mediaSegmentManager, MediaSourceReadContext context);

        IReadOnlyList<MediaStream> GetMediaStreams(IMediaSourceManager mediaSourceManager);
    }
}
