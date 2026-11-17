using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Data.Enums;
using Tesserafin.Model.IO;
using Tesserafin.Model.Querying;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

public class ItemPeopleServiceTests
{
    private static ItemPeopleService CreateService(out Mock<IPeopleRepository> peopleRepository)
    {
        peopleRepository = new Mock<IPeopleRepository>();
        return new ItemPeopleService(peopleRepository.Object, Mock.Of<IItemLookupService>(), Mock.Of<IServerConfigurationManager>(), Mock.Of<IFileSystem>(), Mock.Of<IStandaloneVisibilityService>(), NullLogger<ItemPeopleService>.Instance);
    }

    [Fact]
    public void GetPeople_WithUnsupportedItem_ReturnsEmptyWithoutRepositoryCall()
    {
        var service = CreateService(out var peopleRepository);

        var result = service.GetPeople(new Person { Id = Guid.NewGuid(), Name = "Actor" });

        Assert.Empty(result);
        peopleRepository.VerifyNoOtherCalls();
    }

    [Fact]
    public void GetPeople_WithSupportedItem_QueriesByItemId()
    {
        var service = CreateService(out var peopleRepository);
        var item = new Movie { Id = Guid.NewGuid(), Name = "Movie" };
        var person = new PersonInfo { Name = "Actor", Type = PersonKind.Actor };

        peopleRepository
            .Setup(x => x.GetPeople(It.Is<InternalPeopleQuery>(q => q.ItemId.Equals(item.Id))))
            .Returns(new QueryResult<PersonInfo>(0, 1, new[] { person }));

        var result = service.GetPeople(item);

        Assert.Same(person, Assert.Single(result));
    }

    [Fact]
    public void GetPeopleNamesByItems_ForwardsParameters()
    {
        var service = CreateService(out var peopleRepository);
        var itemIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var personTypes = new[] { PersonKind.Actor.ToString(), PersonKind.Director.ToString() };
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> expected = new Dictionary<Guid, IReadOnlyList<string>>
        {
            [itemIds[0]] = ["Actor"]
        };

        peopleRepository
            .Setup(x => x.GetPeopleNamesByItems(itemIds, personTypes))
            .Returns(expected);

        var result = service.GetPeopleNamesByItems(itemIds, personTypes);

        Assert.Same(expected, result);
    }
}
