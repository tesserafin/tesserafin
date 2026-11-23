#pragma warning disable CS1591

using System;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;

namespace Tesserafin.Controller.Entities
{
    public interface IMetadataContainer
    {
        /// <summary>
        /// Refreshes all metadata.
        /// </summary>
        /// <param name="services">Deletion, write, image, catalogue and metadata services for this refresh.</param>
        /// <param name="refreshOptions">The refresh options.</param>
        /// <param name="progress">The progress.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        Task RefreshAllMetadata(IFolderValidationServices services, MetadataRefreshOptions refreshOptions, IProgress<double> progress, CancellationToken cancellationToken);
    }
}
