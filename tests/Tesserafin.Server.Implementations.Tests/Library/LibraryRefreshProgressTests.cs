using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using Tesserafin.Controller.Entities;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

public sealed class LibraryRefreshProgressTests
{
    [Fact]
    public void Progress_AdvancesOnlyForActiveItemsAndClearsOnCompletion()
    {
        var tracking = new LibraryRefreshProgress(NullLogger<LibraryRefreshProgress>.Instance);
        var folder = new Folder { Id = Guid.NewGuid() };
        var inactive = new Folder { Id = Guid.NewGuid() };
        var reports = new List<double?>();
        tracking.RefreshStarted += (_, args) => reports.Add(tracking.GetRefreshProgress(args.Argument.Id));
        tracking.RefreshProgress += (_, args) => reports.Add(tracking.GetRefreshProgress(args.Argument.Item1.Id));
        tracking.RefreshCompleted += (_, args) => reports.Add(tracking.GetRefreshProgress(args.Argument.Id));

        tracking.OnRefreshProgress(inactive, 10);
        tracking.OnRefreshStart(folder);
        tracking.OnRefreshProgress(folder, 25);
        tracking.OnRefreshProgress(folder, 25);
        tracking.OnRefreshProgress(folder, 5);
        tracking.OnRefreshProgress(folder, 80);
        tracking.OnRefreshComplete(folder);
        tracking.OnRefreshProgress(folder, 100);

        Assert.Equal([0, 25, 80, null], reports);
        Assert.Null(tracking.GetRefreshProgress(folder.Id));
        Assert.Null(tracking.GetRefreshProgress(inactive.Id));
    }
}
