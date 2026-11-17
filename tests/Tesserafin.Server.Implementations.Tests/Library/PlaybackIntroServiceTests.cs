using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Data.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

public sealed class PlaybackIntroServiceTests
{
    [Fact]
    public async Task NoProviders_ReturnsEmptyWithoutResolution()
    {
        var context = new Context();

        Assert.Empty(await context.GetAsync());

        context.Access.VerifyNoOtherCalls();
        context.Resolution.VerifyNoOtherCalls();
        context.Files.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ConcurrentProviders_RetainRegistrationAndCandidateOrder_AndTheRequestSnapshot()
    {
        var context = new Context();
        var first = new TaskCompletionSource<IEnumerable<IntroInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<IEnumerable<IntroInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var one = context.Visible();
        var two = context.Visible();
        var three = context.Visible();
        var providerOne = context.Provider();
        var providerTwo = context.Provider();
        providerOne.Setup(p => p.GetIntrosAsync(context.Item, context.User, It.IsAny<CancellationToken>())).Returns(first.Task);
        providerTwo.Setup(p => p.GetIntrosAsync(context.Item, context.User, It.IsAny<CancellationToken>())).Returns(second.Task);
        context.Service.AddParts([providerOne.Object, providerTwo.Object]);

        var request = context.GetAsync();
        providerOne.Verify(p => p.GetIntrosAsync(context.Item, context.User, TestContext.Current.CancellationToken), Times.Once);
        providerTwo.Verify(p => p.GetIntrosAsync(context.Item, context.User, TestContext.Current.CancellationToken), Times.Once);
        context.Service.AddParts([]);
        second.SetResult([new IntroInfo { ItemId = three.Id }]);
        first.SetResult([new IntroInfo { ItemId = one.Id }, new IntroInfo { ItemId = two.Id }]);

        Assert.Equal(new[] { one, two, three }, await request);
        Assert.Empty(await context.GetAsync());
    }

    [Fact]
    public async Task ProviderTaskAndIteratorFailures_AreIsolated_WithoutLeakingPartialCandidates()
    {
        var context = new Context();
        var intro = context.Visible();
        var throwing = context.Provider();
        throwing.Setup(p => p.GetIntrosAsync(context.Item, context.User, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("provider failed"));
        var deferred = context.Provider();
        deferred.Setup(p => p.GetIntrosAsync(context.Item, context.User, It.IsAny<CancellationToken>())).ReturnsAsync(BrokenIterator());
        var good = context.Provider(new IntroInfo { ItemId = intro.Id });
        context.Service.AddParts([throwing.Object, deferred.Object, good.Object]);

        Assert.Same(intro, Assert.Single(await context.GetAsync()));
        context.Access.Verify(a => a.GetVisibleItemById<Video>(intro.Id, context.User), Times.Once);

        IEnumerable<IntroInfo> BrokenIterator()
        {
            yield return new IntroInfo { ItemId = intro.Id };
            throw new IOException("deferred failure");
        }
    }

    [Fact]
    public async Task PathResolution_ReturnsSavedVisibleMetadata_AndIsFinishedBeforeEnumeration()
    {
        var context = new Context();
        var saved = context.Visible();
        var candidate = new Video { Id = saved.Id, Name = "unpersisted candidate" };
        var file = new FileSystemMetadata { FullName = "/intro.mp4" };
        context.Files.Setup(f => f.GetFileSystemInfo(file.FullName)).Returns(file);
        context.Resolution.Setup(r => r.ResolvePath(file, null, null, null)).Returns(candidate);
        context.Service.AddParts([context.Provider(new IntroInfo { Path = file.FullName }).Object]);

        var result = await context.GetAsync();

        context.Resolution.Verify(r => r.ResolvePath(file, null, null, null), Times.Once);
        Assert.Same(saved, Assert.Single(result));
        Assert.Same(saved, Assert.Single(result));
        context.Resolution.Verify(r => r.ResolvePath(file, null, null, null), Times.Once);
    }

    [Fact]
    public async Task MissingHiddenMalformedAndUnpersistedCandidates_AreExcluded()
    {
        var context = new Context();
        var unresolved = new Video { Id = Guid.NewGuid() };
        var file = new FileSystemMetadata { FullName = "/unsaved.mp4" };
        context.Files.Setup(f => f.GetFileSystemInfo(file.FullName)).Returns(file);
        context.Resolution.Setup(r => r.ResolvePath(file, null, null, null)).Returns(unresolved);
        var wrongType = new FileSystemMetadata { FullName = "/folder" };
        context.Files.Setup(f => f.GetFileSystemInfo(wrongType.FullName)).Returns(wrongType);
        context.Resolution.Setup(r => r.ResolvePath(wrongType, null, null, null)).Returns(new Folder());
        context.Service.AddParts([context.Provider(
            new IntroInfo { ItemId = Guid.NewGuid() },
            new IntroInfo { ItemId = Guid.Empty },
            new IntroInfo(),
            new IntroInfo { Path = file.FullName },
            new IntroInfo { Path = wrongType.FullName }).Object]);

        Assert.Empty(await context.GetAsync());
        context.Access.Verify(a => a.GetVisibleItemById<Video>(unresolved.Id, context.User), Times.Once);
        context.Access.Verify(a => a.GetVisibleItemById<Video>(Guid.Empty, context.User), Times.Never);
    }

    [Fact]
    public async Task PathFailure_DoesNotPreventLaterIntros_AndItemIdTakesPrecedence()
    {
        var context = new Context();
        var saved = context.Visible();
        context.Files.Setup(f => f.GetFileSystemInfo("/bad.mp4")).Throws(new IOException("file unavailable"));
        context.Service.AddParts([context.Provider(
            new IntroInfo { Path = "/bad.mp4" },
            new IntroInfo { ItemId = saved.Id, Path = "/must-not-resolve.mp4" }).Object]);

        Assert.Same(saved, Assert.Single(await context.GetAsync()));
        context.Files.Verify(f => f.GetFileSystemInfo("/must-not-resolve.mp4"), Times.Never);
    }

    [Fact]
    public async Task CancelledAdmission_DoesNotInvokeProviders()
    {
        var context = new Context();
        var provider = context.Provider();
        context.Service.AddParts([provider.Object]);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Service.GetIntrosAsync(context.Item, context.User, cancellation.Token));

        provider.Verify(p => p.GetIntrosAsync(It.IsAny<BaseItem>(), It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RequestCancellation_IsPassedToProviders_AndIsNotConvertedIntoAnEmptySuccess()
    {
        var context = new Context();
        using var cancellation = new CancellationTokenSource();
        var provider = context.Provider();
        provider.Setup(p => p.GetIntrosAsync(context.Item, context.User, cancellation.Token)).Returns(async () =>
        {
            await cancellation.CancelAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            return Array.Empty<IntroInfo>();
        });
        context.Service.AddParts([provider.Object]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Service.GetIntrosAsync(context.Item, context.User, cancellation.Token));

        context.Access.VerifyNoOtherCalls();
    }

    [Fact]
    public void Ownership_RemovesManagerFacades_AndUsesOnlyExplicitReadDependencies()
    {
        var dependencies = typeof(PlaybackIntroService).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.DoesNotContain(typeof(ILibraryManager), dependencies);
        Assert.DoesNotContain(typeof(IProviderManager), dependencies);
        Assert.DoesNotContain(typeof(IItemWriteCommands), dependencies);
        Assert.DoesNotContain(typeof(IServiceProvider), dependencies);
        foreach (var type in new[] { typeof(ILibraryManager), typeof(Tesserafin.Server.Core.Library.LibraryManager) })
        {
            Assert.DoesNotContain(type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), m => m.Name is "GetIntros" or "ResolveIntro");
            Assert.DoesNotContain(type.GetMethod("AddParts")!.GetParameters(), p => p.ParameterType == typeof(IEnumerable<IIntroProvider>));
        }
    }

    private sealed class Context
    {
        public Context()
        {
            Service = new PlaybackIntroService(Access.Object, Resolution.Object, Files.Object, NullLogger<PlaybackIntroService>.Instance);
        }

        public Movie Item { get; } = new() { Id = Guid.NewGuid() };

        public User User { get; } = new("viewer", "authentication", "password-reset");

        public Mock<IItemAccessService> Access { get; } = new();

        public Mock<IItemResolutionService> Resolution { get; } = new(MockBehavior.Strict);

        public Mock<IFileSystem> Files { get; } = new(MockBehavior.Strict);

        public PlaybackIntroService Service { get; }

        public Task<IReadOnlyList<Video>> GetAsync() => Service.GetIntrosAsync(Item, User, TestContext.Current.CancellationToken);

        public Video Visible()
        {
            var video = new Video { Id = Guid.NewGuid(), Name = "saved intro" };
            Access.Setup(a => a.GetVisibleItemById<Video>(video.Id, User)).Returns(video);
            return video;
        }

        public Mock<IIntroProvider> Provider(params IntroInfo[] candidates)
        {
            var provider = new Mock<IIntroProvider>();
            provider.SetupGet(p => p.Name).Returns("test provider");
            provider.Setup(p => p.GetIntrosAsync(Item, User, It.IsAny<CancellationToken>())).ReturnsAsync(candidates);
            return provider;
        }
    }
}
