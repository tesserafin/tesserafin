using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;

namespace Tesserafin.Server.Core.Library.Validators;

/// <summary>Materializes year metadata after scanning rather than from HTTP reads.</summary>
public sealed class YearsPostScanTask(IItemRepository repository, IItemWriteCommands writes) : ILibraryPostScanTask
{
    /// <inheritdoc />
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var years = await repository.GetProductionYearsAsync(cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < years.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writes.GetOrCreateNamedItem<Year>(years[i].ToString(CultureInfo.InvariantCulture), cancellationToken);
            progress.Report(100.0 * (i + 1) / years.Count);
        }

        progress.Report(100);
    }
}
