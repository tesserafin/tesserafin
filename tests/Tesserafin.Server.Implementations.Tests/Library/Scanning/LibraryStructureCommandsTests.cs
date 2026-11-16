using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Library;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.IO;
using Tesserafin.Model.Tasks;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library.Scanning;

public sealed class LibraryStructureCommandsTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ConcurrentCommands_SerializeValidation_AndRestoreMonitoringAfterFailure()
    {
        using var context = new Context();
        using var release = new ManualResetEventSlim();
        var entered = Signal();
        var secondEntered = Signal();
        var count = 0;
        context.Files.Setup(f => f.GetValidFilename(It.IsAny<string>())).Returns((string _) =>
        {
            if (Interlocked.Increment(ref count) == 1)
            {
                entered.SetResult();
                release.Wait(Deadline, TestContext.Current.CancellationToken);
            }
            else
            {
                secondEntered.SetResult();
            }

            throw new IOException("definition write failed");
        });
        var first = Task.Run(() => context.Add(), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        var started = Signal();
        var second = Task.Run(
            () =>
            {
                started.SetResult();
                return context.Add();
            },
            TestContext.Current.CancellationToken);
        try
        {
            await started.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<TimeoutException>(() => secondEntered.Task.WaitAsync(TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken));
        }
        finally
        {
            release.Set();
            await Record.ExceptionAsync(() => Task.WhenAll(first, second).WaitAsync(Deadline, TestContext.Current.CancellationToken));
        }

        await Assert.ThrowsAsync<IOException>(() => first.WaitAsync(Deadline, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IOException>(() => second.WaitAsync(Deadline, TestContext.Current.CancellationToken));
        context.Traversal.Verify(t => t.RefreshTopFoldersAsync(false, It.IsAny<CancellationToken>()), Times.Exactly(2));
        context.Monitor.Verify(m => m.Start(), Times.Exactly(2));
    }

    [Fact]
    public async Task CommandQueuedBehindScan_CanBeCancelledWithoutTouchingFilesystem()
    {
        using var context = new Context();
        var entered = Signal();
        var release = Signal();
        context.Traversal.Setup(t => t.RefreshAllAsync(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>())).Returns(async () =>
        {
            entered.SetResult();
            await release.Task;
        });
        var scan = context.Coordinator.ScanAsync(new Progress<double>(), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var command = context.Commands.AddVirtualFolderAsync("queued", null, new LibraryOptions(), false, cancellation.Token);
        try
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.WaitAsync(Deadline, TestContext.Current.CancellationToken));
            context.Files.Verify(f => f.GetValidFilename(It.IsAny<string>()), Times.Never);
        }
        finally
        {
            release.SetResult();
            await scan.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterEdit_ReconcilesBeforeRestoringMonitor_AndPreservesPrimaryFailure(bool editFails)
    {
        using var context = new Context();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var reconciled = false;
        var failure = new IOException("primary edit error");
        context.Traversal.Setup(t => t.RefreshTopFoldersAsync(false, It.IsAny<CancellationToken>())).Callback((bool _, CancellationToken token) =>
        {
            Assert.False(token.IsCancellationRequested);
            reconciled = true;
        }).Returns(Task.CompletedTask);
        var edit = context.Coordinator.ApplyLibraryChangeAsync(
            () =>
            {
                cancellation.Cancel();
                if (editFails)
                {
                    throw failure;
                }
            },
            false,
            false,
            cancellation.Token);
        if (editFails)
        {
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => edit));
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => edit);
        }

        Assert.True(reconciled);
        context.Monitor.Verify(m => m.Start(), Times.Once);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("/etc")]
    [InlineData("..")]
    public async Task HostileNames_AreRefusedInsideTheMutationBoundary(string name)
    {
        using var context = new Context();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => context.Commands.RemoveVirtualFolderAsync(name, false, TestContext.Current.CancellationToken));
        context.Monitor.Verify(m => m.Stop(), Times.Once);
        context.Monitor.Verify(m => m.Start(), Times.Once);
    }

    [Fact]
    public void CommandsHaveNoFacadeDependency_AndManagerNoLongerOwnsMutations()
    {
        var manager = typeof(Tesserafin.Server.Core.Library.LibraryManager);
        foreach (var method in new[] { "AddVirtualFolder", "RemoveVirtualFolder", "AddMediaPath", "UpdateMediaPath", "RemoveMediaPath", "CreateShortcut" })
        {
            Assert.Null(manager.GetMethod(method));
            Assert.Null(typeof(ILibraryManager).GetMethod(method));
        }

        var parameters = manager.GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.DoesNotContain(typeof(ILibraryScanCoordinator), parameters);
        Assert.DoesNotContain(typeof(ILibraryStructureCommands), parameters);
        Assert.DoesNotContain(typeof(ILibraryManager), typeof(LibraryStructureCommands).GetConstructors().Single().GetParameters().Select(p => p.ParameterType));
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tesserafin.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var source = File.ReadAllText(Path.Combine(directory.FullName, "Tesserafin.Api/Controllers/LibraryStructureController.cs"));
        foreach (var banned in new[] { "Directory.", "File.", "CollectionFolder.", "ApplyLibraryChangeAsync", "CancellationToken.None" })
        {
            Assert.DoesNotContain(banned, source, StringComparison.Ordinal);
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Context : IDisposable
    {
        public Context()
        {
            Configuration.Setup(c => c.Configuration).Returns(new ServerConfiguration());
            Configuration.Setup(c => c.ApplicationPaths.DefaultUserViewsPath).Returns(Root);
            Traversal.Setup(t => t.RefreshTopFoldersAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            Traversal.Setup(t => t.RefreshAllAsync(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            Coordinator = new LibraryScanCoordinator(Traversal.Object, new Lazy<ILibraryMonitor>(() => Monitor.Object), Mock.Of<ITaskManager>(), Configuration.Object, NullLogger<LibraryScanCoordinator>.Instance);
            Commands = new LibraryStructureCommands(Configuration.Object, Files.Object, Mock.Of<IServerApplicationHost>(), Coordinator, Mock.Of<IItemLookupService>(), Mock.Of<ILibraryOptionsStore>(s => s.Get(It.IsAny<string>()) == new LibraryOptions()), NullLogger<LibraryStructureCommands>.Instance);
        }

        public string Root { get; } = Directory.CreateTempSubdirectory("structure-commands-").FullName;

        public Mock<IServerConfigurationManager> Configuration { get; } = new();

        public Mock<IFileSystem> Files { get; } = new();

        public Mock<TraversalDouble> Traversal { get; } = new();

        public Mock<ILibraryMonitor> Monitor { get; } = new();

        public LibraryScanCoordinator Coordinator { get; }

        public LibraryStructureCommands Commands { get; }

        public Task Add() => Commands.AddVirtualFolderAsync("same", null, new LibraryOptions(), true, TestContext.Current.CancellationToken);

        public void Dispose()
        {
            Coordinator.Dispose();
            Directory.Delete(Root, true);
        }
    }
}
