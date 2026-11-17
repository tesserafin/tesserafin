using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Entities;
using Xunit;

namespace Tesserafin.Server.Integration.Tests;

public sealed class ItemPeopleCommandsEndToEndTests(TesserafinApplicationFactory factory) : IClassFixture<TesserafinApplicationFactory>
{
    [Fact]
    public async Task Credits_CreateOnePerson_UpdateMetadata_AndClearWithoutDeletingThePerson()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var commands = factory.Services.GetRequiredService<IItemPeopleCommands>();
        var writes = factory.Services.GetRequiredService<IItemWriteCommands>();
        var people = factory.Services.GetRequiredService<IItemPeopleService>();
        var lookup = factory.Services.GetRequiredService<IItemLookupService>();
        var repository = factory.Services.GetRequiredService<IItemRepository>();
        var manager = factory.Services.GetRequiredService<ILibraryManager>();
        var changes = factory.Services.GetRequiredService<IItemChangeEvents>();
        var name = "Credits person " + Guid.NewGuid().ToString("N");
        var added = new List<Guid>();
        var updated = new List<Guid>();
        var item = new Movie { Id = Guid.NewGuid(), Name = "Credits film", Path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mkv") };
        writes.CreateItems([item], null, TestContext.Current.CancellationToken);
        changes.ItemAdded += Added;
        changes.ItemUpdated += Updated;
        try
        {
            await commands.UpdateAsync(item,
                [new PersonInfo { Name = name, Type = PersonKind.Actor, Role = "Lead", ProviderIds = new() { ["Imdb"] = "nm111" } },
                 new PersonInfo { Name = name, Type = PersonKind.Director }], TestContext.Current.CancellationToken);

            var person = Assert.IsType<Person>(manager.GetPerson(name));
            Assert.Equal(new[] { person.Id }, added);
            Assert.Equal(2, people.GetPeople(item).Count);
            Assert.Equal("nm111", Assert.IsType<Person>(repository.RetrieveItem(person.Id)).GetProviderId("Imdb"));
            Assert.NotEqual(default, person.DateLastSaved);
            Assert.Empty(updated);

            await commands.UpdateAsync(item,
                [new PersonInfo { Name = name, Type = PersonKind.Actor, ProviderIds = new() { ["Imdb"] = "nm222" } }], TestContext.Current.CancellationToken);

            Assert.Single(people.GetPeople(item));
            Assert.Equal(new[] { person.Id }, updated);
            Assert.Equal("nm222", Assert.IsType<Person>(repository.RetrieveItem(person.Id)).GetProviderId("Imdb"));
            Assert.Equal("nm222", Assert.IsType<Person>(lookup.GetItemById(person.Id)).GetProviderId("Imdb"));
            Assert.Single(added);

            await commands.UpdateAsync(item, [], TestContext.Current.CancellationToken);

            Assert.Empty(people.GetPeople(item));
            Assert.IsType<Person>(repository.RetrieveItem(person.Id));
            Assert.Single(added);
            Assert.Single(updated);
        }
        finally
        {
            changes.ItemAdded -= Added;
            changes.ItemUpdated -= Updated;
        }

        void Added(object? sender, ItemChangeEventArgs args)
        {
            if (args.Item is Person && args.Item.Name == name)
            {
                added.Add(args.Item.Id);
            }
        }

        void Updated(object? sender, ItemChangeEventArgs args)
        {
            if (args.Item is Person && args.Item.Name == name)
            {
                updated.Add(args.Item.Id);
            }
        }
    }
}
