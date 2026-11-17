using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Data.Enums;
using Tesserafin.Database.Implementations;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Querying;
using Tesserafin.Server.Core.Library.Validators;
using Xunit;

namespace Tesserafin.Server.Integration.Tests;

public sealed class ItemPeopleCommandsEndToEndTests(TesserafinApplicationFactory factory) : IClassFixture<TesserafinApplicationFactory>
{
    [Fact]
    public async Task PersonReads_DoNotCreate_MetadataIsPaged_AndValidationRemovesOnlyUnlockedOrphans()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var commands = factory.Services.GetRequiredService<IItemPeopleCommands>();
        var writes = factory.Services.GetRequiredService<IItemWriteCommands>();
        var people = factory.Services.GetRequiredService<IItemPeopleService>();
        var lookup = factory.Services.GetRequiredService<IItemLookupService>();
        var repository = factory.Services.GetRequiredService<IItemRepository>();
        var validator = factory.Services.GetRequiredService<PeopleValidator>();
        var suffix = Guid.NewGuid().ToString("N");
        var name = "readperson" + suffix;
        var lockedName = "lockedperson" + suffix;
        var source = Directory.CreateTempSubdirectory("people-source-").FullName;
        var file = Path.Combine(source, "film.mkv");
        var bytes = new byte[] { 1, 4, 9, 16 };
        await File.WriteAllBytesAsync(file, bytes, TestContext.Current.CancellationToken);
        try
        {
            Assert.Null(people.GetPerson(name));
            using (var missing = await client.GetAsync("Persons/" + name, TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }

            Assert.Null(people.GetPerson(name));
            var film = new Movie { Id = Guid.NewGuid(), Name = "person reads " + suffix, Path = file };
            writes.CreateItems([film], null, TestContext.Current.CancellationToken);
            await commands.UpdateAsync(
                film,
                [new PersonInfo { Name = name, Type = PersonKind.Actor }, new PersonInfo { Name = lockedName, Type = PersonKind.Actor }],
                TestContext.Current.CancellationToken);
            var person = Assert.IsType<Person>(people.GetPerson(name));
            var locked = Assert.IsType<Person>(people.GetPerson(lockedName));
            var before = Directory.GetFiles(Path.GetDirectoryName(person.Path)!, "*", SearchOption.AllDirectories).Order().ToArray();
            for (var i = 0; i < 2; i++)
            {
                var detail = await client.GetFromJsonAsync<BaseItemDto>("Persons/" + name, JsonDefaults.Options, TestContext.Current.CancellationToken);
                Assert.Equal(person.Id, Assert.IsType<BaseItemDto>(detail).Id);
                var page = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>($"Persons?appearsInItemId={film.Id}&limit=1", JsonDefaults.Options, TestContext.Current.CancellationToken);
                Assert.NotNull(page);
                Assert.Equal(2, page.TotalRecordCount);
                Assert.Single(page.Items);
            }

            Assert.Equal(before, Directory.GetFiles(Path.GetDirectoryName(person.Path)!, "*", SearchOption.AllDirectories).Order());
            locked.IsLocked = true;
            await writes.UpdateItemAsync(locked, null, ItemUpdateType.MetadataEdit, TestContext.Current.CancellationToken);
            await commands.UpdateAsync(film, [], TestContext.Current.CancellationToken);
            // The scheduled task removes unreferenced credit rows before invoking the validator.
            var databases = factory.Services.GetRequiredService<IDbContextFactory<TesserafinDbContext>>();
            await using (var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken))
            {
                await database.Peoples
                    .Where(p => (p.Name == name || p.Name == lockedName) && !database.PeopleBaseItemMap.Any(m => m.PeopleId.Equals(p.Id)))
                    .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            }

            var progress = new List<double>();
            await validator.ValidatePeople(TestContext.Current.CancellationToken, new InlineProgress(progress.Add));

            Assert.Null(repository.RetrieveItem(person.Id));
            Assert.Null(lookup.GetItemById(person.Id));
            Assert.Null(people.GetPerson(name));
            Assert.IsType<Person>(repository.RetrieveItem(locked.Id));
            Assert.Equal(locked.Id, Assert.IsType<Person>(people.GetPerson(lockedName)).Id);
            Assert.Equal(progress.Order(), progress);
            Assert.Equal(100, progress[^1]);
            Assert.All(progress, value => Assert.InRange(value, 0, 100));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(source, true);
        }
    }

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
            await commands.UpdateAsync(
                item,
                [new PersonInfo { Name = name, Type = PersonKind.Actor, Role = "Lead", ProviderIds = new() { ["Imdb"] = "nm111" } },
                 new PersonInfo { Name = name, Type = PersonKind.Director }],
                TestContext.Current.CancellationToken);

            var person = Assert.IsType<Person>(people.GetPerson(name));
            Assert.Equal(new[] { person.Id }, added);
            Assert.Equal(2, people.GetPeople(item).Count);
            Assert.Equal("nm111", Assert.IsType<Person>(repository.RetrieveItem(person.Id)).GetProviderId("Imdb"));
            Assert.NotEqual(default, person.DateLastSaved);
            Assert.Empty(updated);

            await commands.UpdateAsync(
                item,
                [new PersonInfo { Name = name, Type = PersonKind.Actor, ProviderIds = new() { ["Imdb"] = "nm222" } }],
                TestContext.Current.CancellationToken);

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

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
