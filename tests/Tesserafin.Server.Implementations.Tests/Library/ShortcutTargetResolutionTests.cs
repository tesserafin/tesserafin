using System;
using System.IO;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.IO;
using Tesserafin.Naming.Common;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

public sealed class ShortcutTargetResolutionTests
{
    [Fact]
    public void Resolution_ExpandsRelativeAndVirtualTargets_InStableOrder_AndIgnoresOtherEntries()
    {
        var expandedRoot = Path.Combine(Path.GetTempPath(), "shortcut-expanded");
        var context = new Context(expandedRoot);
        context.Files.Setup(f => f.ResolveShortcut("a.mblink")).Returns("../relative");
        context.Files.Setup(f => f.ResolveShortcut("b.mblink")).Returns("%ROOT%/virtual");

        var result = context.Service.ResolveShortcutTargets(context.Owner, [new() { FullName = "b.mblink" }, new() { FullName = "note.txt" }, new() { FullName = "a.mblink" }, new() { FullName = "directory.mblink", IsDirectory = true }], TestContext.Current.CancellationToken);

        Assert.Equal("a.mblink", result[0].SourcePath);
        Assert.Equal(Path.GetFullPath(Path.Combine(context.Owner.Path, "../relative")), result[0].TargetPath);
        Assert.Equal(Path.GetFullPath(Path.Combine(expandedRoot, "virtual")), result[1].TargetPath);
        Assert.Equal(2, result.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialReadFailure_DoesNotReturnAPartialSnapshot_OrChangeTheFolder(bool denied)
    {
        var context = new Context();
        var before = context.Owner.LinkedChildren;
        context.Files.Setup(f => f.ResolveShortcut("a.mblink")).Returns("first");
        context.Files.Setup(f => f.ResolveShortcut("b.mblink")).Throws(denied ? new SecurityException("denied") : new IOException("unreadable"));

        var error = Assert.ThrowsAny<Exception>(() => context.Service.ResolveShortcutTargets(context.Owner, [new() { FullName = "a.mblink" }, new() { FullName = "b.mblink" }], TestContext.Current.CancellationToken));
        if (denied)
        {
            Assert.IsType<SecurityException>(error);
        }
        else
        {
            Assert.IsType<IOException>(error);
        }

        Assert.Same(before, context.Owner.LinkedChildren);
        context.Files.Verify(f => f.ResolveShortcut("a.mblink"), Times.Once);
        context.Files.Verify(f => f.ResolveShortcut("b.mblink"), Times.Once);
        context.Files.Verify(f => f.DeleteFile(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyShortcutTarget_IsAFailedRead(string? target)
    {
        var context = new Context();
        context.Files.Setup(f => f.ResolveShortcut("empty.mblink")).Returns(target);

        Assert.Throws<IOException>(() => context.Service.ResolveShortcutTargets(context.Owner, [new() { FullName = "empty.mblink" }], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancelledAdmission_DoesNotInspectFiles()
    {
        var context = new Context();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Assert.ThrowsAny<OperationCanceledException>(() => context.Service.ResolveShortcutTargets(context.Owner, [new() { FullName = "a.mblink" }], cancellation.Token));

        context.Files.VerifyNoOtherCalls();
    }

    private sealed class Context
    {
        public Context(string dataPath = "/data")
        {
            Files.Setup(f => f.IsShortcut(It.IsAny<string>())).Returns((string path) => path.EndsWith(".mblink", StringComparison.OrdinalIgnoreCase));
            Service = new ItemResolutionService(Mock.Of<IServerConfigurationManager>(c => c.ApplicationPaths == Mock.Of<IServerApplicationPaths>(p => p.VirtualDataPath == "%ROOT%" && p.DataPath == dataPath && p.VirtualInternalMetadataPath == "%MetadataPath%" && p.InternalMetadataPath == "/metadata")), Files.Object, Mock.Of<ILibraryItemOptions>(), Mock.Of<IItemLookupService>(), new NamingOptions(), NullLogger<ItemResolutionService>.Instance, NullLoggerFactory.Instance, Mock.Of<IDirectoryService>(), new DotIgnoreIgnoreRule(), Mock.Of<IItemReadServices>());
        }

        public Folder Owner { get; } = new() { Id = Guid.NewGuid(), Path = Path.Combine(Path.GetTempPath(), "shortcut-owner") };

        public Mock<IFileSystem> Files { get; } = new();

        public ItemResolutionService Service { get; }
    }
}
