using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Library;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.Tasks;

namespace Tesserafin.Server.Core.ScheduledTasks.Tasks;

/// <summary>
/// Class RefreshMediaLibraryTask.
/// </summary>
public class RefreshMediaLibraryTask : IScheduledTask
{
    /// <summary>
    /// The scan coordinator.
    /// </summary>
    private readonly ILibraryScanCoordinator _scans;
    private readonly ILocalizationManager _localization;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshMediaLibraryTask" /> class.
    /// </summary>
    /// <param name="scans">Instance of the <see cref="ILibraryScanCoordinator"/> interface.</param>
    /// <param name="localization">Instance of the <see cref="ILocalizationManager"/> interface.</param>
    public RefreshMediaLibraryTask(ILibraryScanCoordinator scans, ILocalizationManager localization)
    {
        _scans = scans;
        _localization = localization;
    }

    /// <inheritdoc />
    public string Name => _localization.GetLocalizedString("TaskRefreshLibrary");

    /// <inheritdoc />
    public string Description => _localization.GetLocalizedString("TaskRefreshLibraryDescription");

    /// <inheritdoc />
    public string Category => _localization.GetLocalizedString("TasksLibraryCategory");

    /// <inheritdoc />
    public string Key => "RefreshLibrary";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(12).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _scans.ScanAsync(progress, cancellationToken).ConfigureAwait(false);
    }
}
