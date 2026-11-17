using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

public sealed class ItemPeopleCommandsTests
{
    [Fact]
    public async Task NewPerson_IsExportedThenCreatedExactlyOnce_WithItsProviderIdsAndImage()
    {
        using var context = new Context();
        var credit = new PersonInfo { Name = "Alice", ImageUrl = "https://example.test/portrait.jpg", ProviderIds = new() { ["Imdb"] = "nm123" } };
        var exported = false;
        context.Metadata.Setup(m => m.SaveAsync(It.IsAny<BaseItem>(), ItemUpdateType.ImageUpdate, It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>>()))
            .Callback(() => exported = true).Returns(Task.CompletedTask);
        context.Writes.Setup(w => w.CreateItems(It.IsAny<IReadOnlyList<BaseItem>>(), null, It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<BaseItem> items, BaseItem? _, CancellationToken _) =>
            {
                Assert.True(exported);
                var person = Assert.IsType<Person>(Assert.Single(items));
                Assert.Equal("nm123", person.GetProviderId("Imdb"));
                Assert.Equal(credit.ImageUrl, Assert.Single(person.ImageInfos).Path);
                Assert.NotEqual(default, person.DateLastSaved);
            });

        await context.Commands.UpdateAsync(context.Item, [credit], TestContext.Current.CancellationToken);

        context.Repository.Verify(r => r.UpdatePeople(context.Item.Id, It.Is<IReadOnlyList<PersonInfo>>(p => p.Count == 1)), Times.Once);
        context.Writes.Verify(w => w.CreateItems(It.IsAny<IReadOnlyList<BaseItem>>(), null, TestContext.Current.CancellationToken), Times.Once);
        context.Writes.Verify(w => w.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExistingPerson_FailedWrite_DoesNotMutatePublishedProviderIdsOrImages()
    {
        using var context = new Context();
        var original = new Person { Id = Guid.NewGuid(), Name = "Alice", ProviderIds = new() { ["Imdb"] = "old" } };
        context.Lookup.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns(original);
        context.Writes.Setup(w => w.UpdateItemAsync(It.IsAny<BaseItem>(), null, ItemUpdateType.ImageUpdate, It.IsAny<CancellationToken>()))
            .Callback((BaseItem item, BaseItem? _, ItemUpdateType _, CancellationToken _) =>
            {
                Assert.NotSame(original, item);
                Assert.Equal("new", item.GetProviderId("Imdb"));
                Assert.Single(item.ImageInfos);
                throw new IOException("catalogue failure");
            });

        await Assert.ThrowsAsync<IOException>(() => context.Commands.UpdateAsync(context.Item,
            [new PersonInfo { Name = "Alice", ImageUrl = "https://example.test/portrait.jpg", ProviderIds = new() { ["Imdb"] = "new" } }], TestContext.Current.CancellationToken));

        Assert.Equal("old", original.GetProviderId("Imdb"));
        Assert.Empty(original.ImageInfos);
        context.Writes.Verify(w => w.CreateItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<BaseItem>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnchangedPerson_DoesNotWriteOrExportMetadata()
    {
        using var context = new Context();
        context.Lookup.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns(new Person { Id = Guid.NewGuid(), Name = "Alice" });

        await context.Commands.UpdateAsync(context.Item, [new PersonInfo { Name = "Alice" }], TestContext.Current.CancellationToken);

        context.Writes.VerifyNoOtherCalls();
        context.Metadata.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EmptyCredits_ClearTheMapping_WhileNullAndUnsupportedItemsDoNothing()
    {
        using var context = new Context();
        await context.Commands.UpdateAsync(context.Item, null, TestContext.Current.CancellationToken);
        await context.Commands.UpdateAsync(new Person(), [new PersonInfo { Name = "Alice" }], TestContext.Current.CancellationToken);
        context.Repository.VerifyNoOtherCalls();

        await context.Commands.UpdateAsync(context.Item, [], TestContext.Current.CancellationToken);

        context.Repository.Verify(r => r.UpdatePeople(context.Item.Id, It.Is<IReadOnlyList<PersonInfo>>(p => p.Count == 0)), Times.Once);
        context.Writes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CancelledAdmission_DoesNotWriteCreditsOrPeople()
    {
        using var context = new Context();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Commands.UpdateAsync(context.Item, [], cancellation.Token));

        context.Repository.VerifyNoOtherCalls();
        context.Writes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CancelledExport_DoesNotCreateAPerson()
    {
        using var context = new Context();
        context.Metadata.Setup(m => m.SaveAsync(It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Commands.UpdateAsync(context.Item, [new PersonInfo { Name = "Alice" }], TestContext.Current.CancellationToken));

        context.Writes.VerifyNoOtherCalls();
    }

    [Fact]
    public void Ownership_RemovesTheManagerWriteFacades_AndHasNoManagerOrProviderDependency()
    {
        var dependencies = typeof(ItemPeopleCommands).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.DoesNotContain(typeof(ILibraryManager), dependencies);
        Assert.DoesNotContain(typeof(IProviderManager), dependencies);
        Assert.DoesNotContain(typeof(IServiceProvider), dependencies);
        foreach (var type in new[] { typeof(ILibraryManager), typeof(Tesserafin.Server.Core.Library.LibraryManager) })
        {
            Assert.DoesNotContain(type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), m => m.Name is "UpdatePeople" or "UpdatePeopleAsync" or "SavePeopleMetadataAsync");
        }
    }

    private sealed class Context : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("people-command-").FullName;

        public Context()
        {
            var configuration = new Mock<IServerConfigurationManager>();
            configuration.SetupGet(c => c.Configuration).Returns(new ServerConfiguration());
            configuration.SetupGet(c => c.ApplicationPaths.PeoplePath).Returns(_root);
            configuration.SetupGet(c => c.ApplicationPaths.ProgramDataPath).Returns(Path.Combine(_root, "data"));
            var files = new Mock<IFileSystem>();
            files.Setup(f => f.GetValidFilename(It.IsAny<string>())).Returns((string name) => name);
            Metadata.Setup(m => m.SaveAsync(It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>>())).Returns(Task.CompletedTask);
            Commands = new ItemPeopleCommands(Repository.Object, Lookup.Object, configuration.Object, files.Object, Writes.Object, Metadata.Object, NullLogger<ItemPeopleCommands>.Instance);
        }

        public Movie Item { get; } = new() { Id = Guid.NewGuid(), Name = "Film" };

        public Mock<IPeopleRepository> Repository { get; } = new();

        public Mock<IItemLookupService> Lookup { get; } = new();

        public Mock<IItemWriteCommands> Writes { get; } = new();

        public Mock<IItemMetadataExporter> Metadata { get; } = new();

        public ItemPeopleCommands Commands { get; }

        public void Dispose() => Directory.Delete(_root, true);
    }
}
