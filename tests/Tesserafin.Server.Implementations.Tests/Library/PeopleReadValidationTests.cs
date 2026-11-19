using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.IO;
using Tesserafin.Model.Querying;
using Tesserafin.Server.Core.Library;
using Tesserafin.Server.Core.Library.Validators;
using Tesserafin.Server.Core.ScheduledTasks.Tasks;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

public sealed class PeopleReadValidationTests
{
    [Fact]
    public void Lookup_UsesConfiguredIdentity_WithoutCreatingMissingMetadata()
    {
        using var context = new Context();
        Assert.Null(context.Service.GetPerson("missing"));
        Assert.False(Directory.Exists(context.PeoplePath));
        var person = context.Store("Actor");
        Assert.Same(person, context.Service.GetPerson("Actor"));
        Assert.False(Directory.Exists(context.PeoplePath));
        context.Files.Verify(f => f.GetValidFilename("Actor"), Times.Exactly(2));
    }

    [Fact]
    public void CreditPage_IsolatesBadAndMissingNames_FiltersThroughTheScope_AndPreservesOrderAndPaging()
    {
        using var context = new Context();
        var first = context.Store("first");
        var blocked = context.Store("blocked");
        var last = context.Store("last");
        var user = new User("user", "auth", "reset");
        var query = new InternalPeopleQuery { User = user, StartIndex = 9, Limit = 5 };
        var credits = new[] { "first", "missing", "bad/name", "blocked", "last" }.Select(n => new PersonInfo { Name = n }).ToArray();
        context.People.Setup(p => p.GetPeople(query)).Returns(new QueryResult<PersonInfo>(9, 27, credits));
        context.Visibility.Setup(v => v.IsVisibleViaTags(It.IsAny<BaseItem>(), user, false)).Returns((BaseItem item, User _, bool _) => !item.Id.Equals(blocked.Id));

        var result = context.Service.GetPeopleItems(query);

        Assert.Equal(new[] { first.Id, last.Id }, result.Items.Select(i => i.Id));
        Assert.Equal(9, result.StartIndex);
        Assert.Equal(27, result.TotalRecordCount);
        Assert.False(Directory.Exists(context.PeoplePath));
        context.Visibility.Verify(v => v.IsVisibleViaTags(It.IsAny<BaseItem>(), user, false), Times.Exactly(3));
    }

    [Fact]
    public void CreditPage_WithoutAUser_DoesNotApplyVisibility()
    {
        using var context = new Context();
        var person = context.Store("actor");
        context.People.Setup(p => p.GetPeople(It.IsAny<InternalPeopleQuery>())).Returns(new QueryResult<PersonInfo>(0, 1, [new PersonInfo { Name = "actor" }]));

        Assert.Same(person, Assert.Single(context.Service.GetPeopleItems(new InternalPeopleQuery()).Items));
        context.Visibility.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Validation_AccountsForMissingAndInvalidNames_DeletesInBatches_AndReportsMonotonicProgress()
    {
        using var context = new Context();
        var reads = new Mock<IItemPeopleService>();
        reads.Setup(p => p.GetPeopleNames(It.IsAny<InternalPeopleQuery>())).Returns(["missing", "invalid"]);
        reads.Setup(p => p.GetPerson("invalid")).Throws(new ArgumentException("invalid name"));
        var dead = Enumerable.Range(0, 601).Select(_ => (BaseItem)new Person { Id = Guid.NewGuid() }).ToArray();
        var repository = new Mock<IItemRepository>();
        repository.Setup(r => r.GetItemList(It.Is<InternalItemsQuery>(q => q.IsDeadPerson == true && q.IsLocked == false && q.IncludeItemTypes.SequenceEqual(new[] { BaseItemKind.Person })))).Returns(dead);
        var batchSizes = new List<int>();
        var deletions = new Mock<IItemDeletionCommands>();
        deletions.Setup(d => d.DeleteUnreferencedAsync(It.IsAny<IReadOnlyCollection<BaseItem>>(), true, It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyCollection<BaseItem> items, bool _, CancellationToken _) => batchSizes.Add(items.Count)).Returns(Task.CompletedTask);
        var validator = new PeopleValidator(Mock.Of<IItemWriteCommands>(), deletions.Object, Mock.Of<IItemMetadataDispatcher>(MockBehavior.Strict), reads.Object, repository.Object, context.Configuration.Object, NullLogger<PeopleValidator>.Instance, context.Files.Object);
        var progress = new List<double>();

        await validator.ValidatePeople(TestContext.Current.CancellationToken, new InlineProgress(progress.Add));

        Assert.Equal(new[] { 500, 101 }, batchSizes);
        Assert.Equal(new[] { 25.0, 50, 50, 50 + (50.0 * 500 / 601), 100, 100 }, progress);
        Assert.All(progress, value => Assert.InRange(value, 0, 100));
        Assert.Equal(progress.Order(), progress);
        Assert.True(Directory.Exists(context.PeoplePath));
        repository.Verify(r => r.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Once);
    }

    [Fact]
    public async Task Validation_CancelledAdmission_DoesNotCreateDirectoriesOrReadOrDelete()
    {
        using var context = new Context();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var reads = new Mock<IItemPeopleService>(MockBehavior.Strict);
        var deletions = new Mock<IItemDeletionCommands>(MockBehavior.Strict);
        var repository = new Mock<IItemRepository>(MockBehavior.Strict);
        var validator = new PeopleValidator(Mock.Of<IItemWriteCommands>(), deletions.Object, Mock.Of<IItemMetadataDispatcher>(MockBehavior.Strict), reads.Object, repository.Object, context.Configuration.Object, NullLogger<PeopleValidator>.Instance, context.Files.Object);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validator.ValidatePeople(cancellation.Token, new InlineProgress(_ => { })));

        Assert.False(Directory.Exists(context.PeoplePath));
        reads.VerifyNoOtherCalls();
        repository.VerifyNoOtherCalls();
        deletions.VerifyNoOtherCalls();
    }

    [Fact]
    public void Ownership_RemovesManagerPersonEntryPointsAndRepository_WithoutMovingThemBehindALocator()
    {
        var manager = typeof(Tesserafin.Server.Core.Library.LibraryManager);
        foreach (var owner in new[] { manager, typeof(ILibraryManager) })
        {
            Assert.DoesNotContain(owner.GetMethods(), m => new[] { "GetPerson", "GetPeopleItems", "ValidatePeopleAsync" }.Contains(m.Name));
        }

        Assert.DoesNotContain(manager.GetConstructors().Single().GetParameters(), p => p.ParameterType == typeof(IPeopleRepository));
        foreach (var owner in new[] { typeof(ItemPeopleService), typeof(PeopleValidator), typeof(PeopleValidationTask) })
        {
            var dependencies = owner.GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();
            foreach (var forbidden in new[] { typeof(ILibraryManager), typeof(IProviderManager), typeof(IServiceProvider) })
            {
                Assert.DoesNotContain(forbidden, dependencies);
            }
        }
    }

    private sealed class Context : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("people-read-").FullName;
        private readonly Dictionary<Guid, Person> _items = new();

        public Context()
        {
            Configuration.SetupGet(c => c.Configuration).Returns(new ServerConfiguration());
            Configuration.SetupGet(c => c.ApplicationPaths.ProgramDataPath).Returns(_root);
            Configuration.SetupGet(c => c.ApplicationPaths.PeoplePath).Returns(PeoplePath);
            Files.Setup(f => f.GetValidFilename(It.IsAny<string>())).Returns((string name) => name);
            var lookup = new Mock<IItemLookupService>();
            lookup.Setup(l => l.GetItemById<Person>(It.IsAny<Guid>())).Returns((Guid id) => _items.GetValueOrDefault(id));
            Visibility.Setup(v => v.GetParents(It.IsAny<BaseItem>())).Returns(Array.Empty<BaseItem>());
            Service = new ItemPeopleService(People.Object, lookup.Object, Configuration.Object, Files.Object, Visibility.Object, NullLogger<ItemPeopleService>.Instance);
        }

        public string PeoplePath => Path.Combine(_root, "people");

        public Mock<IServerConfigurationManager> Configuration { get; } = new();

        public Mock<IFileSystem> Files { get; } = new();

        public Mock<IStandaloneVisibilityService> Visibility { get; } = new();

        public Mock<IPeopleRepository> People { get; } = new();

        public ItemPeopleService Service { get; }

        public Person Store(string name)
        {
            var path = Person.GetPath(name, Files.Object, PeoplePath);
            var id = ItemIdentity.Create(Configuration.Object, path, typeof(Person), Configuration.Object.Configuration.EnableNormalizedItemByNameIds);
            var item = new Person { Id = id, Name = name, Path = path };
            _items.Add(id, item);
            return item;
        }

        public void Dispose() => Directory.Delete(_root, true);
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
