using Microsoft.Extensions.Logging;
using Tesserafin.Controller;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.IO;
using Tesserafin.Naming.Common;

namespace Tesserafin.Providers.MediaInfo
{
    /// <summary>
    /// Resolves external audio files for <see cref="Video"/>.
    /// </summary>
    public class AudioResolver : MediaInfoResolver
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AudioResolver"/> class for external audio file processing.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="localizationManager">The localization manager.</param>
        /// <param name="mediaEncoder">The media encoder.</param>
        /// <param name="fileSystem">The file system.</param>
        /// <param name="applicationPaths">The server application paths.</param>
        /// <param name="namingOptions">The <see cref="NamingOptions"/> object containing FileExtensions, MediaDefaultFlags, MediaForcedFlags and MediaFlagDelimiters.</param>
        public AudioResolver(
            ILogger<AudioResolver> logger,
            ILocalizationManager localizationManager,
            IMediaEncoder mediaEncoder,
            IFileSystem fileSystem,
            IServerApplicationPaths applicationPaths,
            NamingOptions namingOptions)
            : base(
                logger,
                localizationManager,
                mediaEncoder,
                fileSystem,
                applicationPaths,
                namingOptions,
                DlnaProfileType.Audio)
        {
        }
    }
}
