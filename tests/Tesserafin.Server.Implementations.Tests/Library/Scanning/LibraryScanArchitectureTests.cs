using System;
using System.IO;
using System.Linq;
using Tesserafin.Controller.Library;
using Tesserafin.Model.Tasks;
using Tesserafin.Server.Core.Library;
using Tesserafin.Server.Core.ScheduledTasks.Tasks;
using Tesserafin.Server.Implementations.FullSystemBackup;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library.Scanning;

public sealed class LibraryScanArchitectureTests
{
    [Fact]
    public void ItemManager_NoLongerOwnsScanMethodsStateOrMonitorSchedulingDependencies()
    {
        var manager = typeof(Tesserafin.Server.Core.Library.LibraryManager);
        foreach (var removed in new[] { "ValidateMediaLibrary", "ValidateMediaLibraryInternal", "ValidateTopLibraryFolders", "ClearIgnoreRuleCache", "QueueLibraryScan", "CreateRootFolder" })
        {
            Assert.Null(manager.GetMethod(removed));
            Assert.Null(typeof(ILibraryManager).GetMethod(removed));
        }

        Assert.Null(manager.GetProperty("IsScanRunning"));
        Assert.Null(typeof(ILibraryManager).GetProperty("IsScanRunning"));
        Assert.Null(manager.GetMethod("RegisterItem"));
        Assert.Null(typeof(ILibraryManager).GetMethod("RegisterItem"));
        var dependencies = manager.GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.DoesNotContain(typeof(IItemCacheStore), dependencies);
        Assert.DoesNotContain(typeof(ITaskManager), dependencies);
        Assert.DoesNotContain(typeof(Lazy<ILibraryMonitor>), dependencies);
    }

    [Theory]
    [InlineData(typeof(RefreshMediaLibraryTask))]
    [InlineData(typeof(OptimizeDatabaseTask))]
    [InlineData(typeof(BackupService))]
    public void ScanOnlyConsumers_UseTheNarrowPort(Type type)
    {
        var dependencies = type.GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.Contains(typeof(ILibraryScanCoordinator), dependencies);
        Assert.DoesNotContain(typeof(ILibraryManager), dependencies);
    }

    [Fact]
    public void ScanImplementation_HasNoManagerDependencyOrCast()
    {
        foreach (var type in new[] { typeof(LibraryScanCoordinator), typeof(LibraryScanTraversal), typeof(LibraryRootFolderProvider) })
        {
            var dependencies = type.GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();
            Assert.DoesNotContain(typeof(ILibraryManager), dependencies);
            Assert.DoesNotContain(typeof(Lazy<ILibraryManager>), dependencies);
            var source = Source("Tesserafin.Server.Core", "Library", type.Name + ".cs");
            Assert.DoesNotContain("LibraryManager", source, StringComparison.Ordinal);
        }

        var root = Source("Tesserafin.Server.Core", "Library", "LibraryRootFolderProvider.cs");
        Assert.DoesNotContain("UpdateToRepositoryAsync", root, StringComparison.Ordinal);
        var task = Source("Tesserafin.Server.Core", "ScheduledTasks", "Tasks", "RefreshMediaLibraryTask.cs");
        Assert.DoesNotContain("LibraryManager", task, StringComparison.Ordinal);
        var controller = Source("Tesserafin.Api", "Controllers", "LibraryStructureController.cs");
        Assert.DoesNotContain("Task.Run", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("ILibraryMonitor", controller, StringComparison.Ordinal);
    }

    private static string Source(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tesserafin.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, Path.Combine(parts)));
    }
}
