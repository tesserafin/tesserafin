using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Library;
using Tesserafin.Database.Implementations;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.Tasks;

namespace Tesserafin.Server.Core.ScheduledTasks.Tasks;

/// <summary>
/// Optimizes Tesserafin's database by issuing a VACUUM command.
/// </summary>
public class OptimizeDatabaseTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly ILogger<OptimizeDatabaseTask> _logger;
    private readonly ILocalizationManager _localization;
    private readonly ITesserafinDatabaseProvider _reefinDatabaseProvider;
    private readonly ILibraryScanCoordinator _scans;

    /// <summary>
    /// Initializes a new instance of the <see cref="OptimizeDatabaseTask" /> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    /// <param name="localization">Instance of the <see cref="ILocalizationManager"/> interface.</param>
    /// <param name="reefinDatabaseProvider">Instance of the TesserafinDatabaseProvider that can be used for provider specific operations.</param>
    /// <param name="scans">Instance of the <see cref="ILibraryScanCoordinator"/> interface.</param>
    public OptimizeDatabaseTask(
        ILogger<OptimizeDatabaseTask> logger,
        ILocalizationManager localization,
        ITesserafinDatabaseProvider reefinDatabaseProvider,
        ILibraryScanCoordinator scans)
    {
        _logger = logger;
        _localization = localization;
        _reefinDatabaseProvider = reefinDatabaseProvider;
        _scans = scans;
    }

    /// <inheritdoc />
    public string Name => _localization.GetLocalizedString("TaskOptimizeDatabase");

    /// <inheritdoc />
    public string Description => _localization.GetLocalizedString("TaskOptimizeDatabaseDescription");

    /// <inheritdoc />
    public string Category => _localization.GetLocalizedString("TasksMaintenanceCategory");

    /// <inheritdoc />
    public string Key => "OptimizeDatabaseTask";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(6).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // Vacuuming/checkpointing requires an exclusive lock on the database. Running it while a library scan is in
        // progress causes both operations to contend for the database and can stall the scan, so defer optimization
        // until no scan is running. The task will run again on its next trigger.
        if (_scans.IsScanRunning)
        {
            _logger.LogInformation("Skipping database optimization because a library scan is currently running.");
            return;
        }

        _logger.LogInformation("Optimizing and vacuuming tesserafin.db...");

        try
        {
            await _reefinDatabaseProvider.RunScheduledOptimisation(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while optimizing tesserafin.db");
        }
    }
}
