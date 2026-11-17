using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Common.Extensions;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;

namespace Tesserafin.Server.Core.Library;

/// <summary>Replaces credits and persists their person metadata through the item writer.</summary>
internal sealed class ItemPeopleCommands(
    IPeopleRepository repository,
    IItemLookupService lookup,
    IServerConfigurationManager configuration,
    IFileSystem files,
    IItemWriteCommands writes,
    IItemMetadataExporter metadata,
    ILogger<ItemPeopleCommands> logger) : IItemPeopleCommands
{
    public async Task UpdateAsync(BaseItem item, IReadOnlyList<PersonInfo>? people, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();
        if (!item.SupportsPeople || people is null)
        {
            return;
        }

        var credits = people.Where(p => p is not null).ToArray();
        repository.UpdatePeople(item.Id, credits);
        await SavePeopleMetadataAsync(credits, cancellationToken).ConfigureAwait(false);
    }

    private Guid GetPersonId(string path)
        => ItemIdentity.Create(configuration, path, typeof(Person), configuration.Configuration.EnableNormalizedItemByNameIds);

    private Person? FindPerson(string name)
        => lookup.GetItemById(GetPersonId(Person.GetPath(name, files, configuration.ApplicationPaths.PeoplePath))) as Person;

    private async Task SavePeopleMetadataAsync(IEnumerable<PersonInfo> people, CancellationToken cancellationToken)
    {
        foreach (var person in people)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var itemUpdateType = ItemUpdateType.MetadataDownload;
            var saveEntity = false;
            var createEntity = false;
            var personEntity = FindPerson(person.Name);

            if (personEntity is null)
            {
                try
                {
                    var path = Person.GetPath(person.Name, files, configuration.ApplicationPaths.PeoplePath);
                    var info = Directory.CreateDirectory(path);
                    personEntity = new Person()
                    {
                        Name = person.Name,
                        Id = GetPersonId(path),
                        DateCreated = info.CreationTimeUtc,
                        DateModified = info.LastWriteTimeUtc,
                        Path = path
                    };

                    personEntity.PresentationUniqueKey = personEntity.CreatePresentationUniqueKey();
                    saveEntity = true;
                    createEntity = true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to create person {Name}", person.Name.ToSingleLogLine());
                    continue;
                }
            }

            if (!createEntity)
            {
                personEntity = (Person)personEntity.CopyForWrite();
                personEntity.ProviderIds = new Dictionary<string, string>(personEntity.ProviderIds, personEntity.ProviderIds.Comparer);
                personEntity.ImageInfos = personEntity.ImageInfos.ToArray();
            }

            foreach (var id in person.ProviderIds)
            {
                if (!string.Equals(personEntity.GetProviderId(id.Key), id.Value, StringComparison.OrdinalIgnoreCase))
                {
                    personEntity.SetProviderId(id.Key, id.Value);
                    saveEntity = true;
                }
            }

            if (!string.IsNullOrWhiteSpace(person.ImageUrl) && !personEntity.HasImage(ImageType.Primary))
            {
                personEntity.SetImage(
                    new ItemImageInfo
                    {
                        Path = person.ImageUrl,
                        Type = ImageType.Primary
                    },
                    0);

                saveEntity = true;
                itemUpdateType = ItemUpdateType.ImageUpdate;
            }

            if (saveEntity)
            {
                if (createEntity)
                {
                    await metadata.SaveAsync(personEntity, itemUpdateType, cancellationToken).ConfigureAwait(false);
                    personEntity.DateLastSaved = DateTime.UtcNow;
                    writes.CreateItems([personEntity], null, cancellationToken);
                }
                else
                {
                    await writes.UpdateItemAsync(personEntity, null, itemUpdateType, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}
