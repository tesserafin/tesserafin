#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Model.Querying;

namespace Tesserafin.Controller.Library;

public interface IItemPeopleService
{
    /// <summary>Looks up persisted person metadata without creating directories or items.</summary>
    /// <param name="name">The credited name.</param>
    /// <returns>The existing person, or null.</returns>
    Person? GetPerson(string name);

    /// <summary>Expands a credit page into existing visible person records, preserving repository paging metadata.</summary>
    /// <param name="query">The credit query and optional requesting user.</param>
    /// <returns>Persisted visible people and the credit query's start and total.</returns>
    QueryResult<BaseItem> GetPeopleItems(InternalPeopleQuery query);

    IReadOnlyList<PersonInfo> GetPeople(InternalPeopleQuery query);

    IReadOnlyList<PersonInfo> GetPeople(BaseItem item);

    IReadOnlyList<string> GetPeopleNames(InternalPeopleQuery query);

    IReadOnlyDictionary<Guid, IReadOnlyList<string>> GetPeopleNamesByItems(IReadOnlyList<Guid> itemIds, IReadOnlyList<string> personTypes);
}
