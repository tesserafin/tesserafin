#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Model.IO;
using Tesserafin.Model.Querying;

namespace Tesserafin.Server.Core.Library;

public class ItemPeopleService : IItemPeopleService
{
    private readonly IPeopleRepository _peopleRepository;
    private readonly IItemLookupService _lookup;
    private readonly IServerConfigurationManager _configuration;
    private readonly IFileSystem _files;
    private readonly IStandaloneVisibilityService _visibility;
    private readonly ILogger<ItemPeopleService> _logger;

    public ItemPeopleService(IPeopleRepository peopleRepository, IItemLookupService lookup, IServerConfigurationManager configuration, IFileSystem files, IStandaloneVisibilityService visibility, ILogger<ItemPeopleService> logger)
    {
        _peopleRepository = peopleRepository;
        _lookup = lookup;
        _configuration = configuration;
        _files = files;
        _visibility = visibility;
        _logger = logger;
    }

    public Person? GetPerson(string name)
    {
        var path = Person.GetPath(name, _files, _configuration.ApplicationPaths.PeoplePath);
        var id = ItemIdentity.Create(_configuration, path, typeof(Person), _configuration.Configuration.EnableNormalizedItemByNameIds);
        return _lookup.GetItemById<Person>(id);
    }

    public QueryResult<BaseItem> GetPeopleItems(InternalPeopleQuery query)
    {
        var result = _peopleRepository.GetPeople(query);
        var items = new List<BaseItem>();
        foreach (var credit in result.Items)
        {
            Person? person;
            try
            {
                person = GetPerson(credit.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving person {Name}", credit.Name);
                continue;
            }

            if (person is not null && (query.User is null || person.IsVisible(query.User, false, _visibility)))
            {
                items.Add(person);
            }
        }

        return new QueryResult<BaseItem>(result.StartIndex, result.TotalRecordCount, items.AsReadOnly());
    }

    public IReadOnlyList<PersonInfo> GetPeople(InternalPeopleQuery query)
    {
        return _peopleRepository.GetPeople(query).Items;
    }

    public IReadOnlyList<PersonInfo> GetPeople(BaseItem item)
    {
        if (item.SupportsPeople)
        {
            var people = GetPeople(new InternalPeopleQuery
            {
                ItemId = item.Id
            });

            if (people.Count > 0)
            {
                return people;
            }
        }

        return [];
    }

    public IReadOnlyList<string> GetPeopleNames(InternalPeopleQuery query)
    {
        return _peopleRepository.GetPeopleNames(query);
    }

    public IReadOnlyDictionary<Guid, IReadOnlyList<string>> GetPeopleNamesByItems(IReadOnlyList<Guid> itemIds, IReadOnlyList<string> personTypes)
    {
        return _peopleRepository.GetPeopleNamesByItems(itemIds, personTypes);
    }
}
