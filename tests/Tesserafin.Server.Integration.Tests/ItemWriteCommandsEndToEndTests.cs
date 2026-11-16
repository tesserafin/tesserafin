using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Database.Implementations;
using Tesserafin.Model.Entities;
using Xunit;

namespace Tesserafin.Server.Integration.Tests;

public sealed class ItemWriteCommandsEndToEndTests(TesserafinApplicationFactory factory) : IClassFixture<TesserafinApplicationFactory>
{
    [Fact]
    public async Task VersionGraph_RollsBackFailedCreation_Retries_UpdatesAndPromotesMultipartAlternate()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        using var sources = new Sources();
        var writes = factory.Services.GetRequiredService<IItemWriteCommands>();
        var resolution = factory.Services.GetRequiredService<IItemResolutionService>();
        var lookup = factory.Services.GetRequiredService<IItemLookupService>();
        var repository = factory.Services.GetRequiredService<IItemRepository>();
        var linked = factory.Services.GetRequiredService<ILinkedChildrenService>();
        var changes = factory.Services.GetRequiredService<IItemChangeEvents>();
        var databases = factory.Services.GetRequiredService<IDbContextFactory<TesserafinDbContext>>();
        var primary = new Movie
        {
            Id = resolution.GetNewItemId(sources.Primary, typeof(Movie)),
            Name = "Write graph primary",
            Path = sources.Primary,
            Container = "mkv",
            VideoType = VideoType.VideoFile,
            IsInMixedFolder = true,
            LocalAlternateVersions = [sources.Alternate, sources.Alternate],
            DateLastSaved = DateTime.UnixEpoch
        };
        var alternateId = resolution.GetNewItemId(sources.Alternate, typeof(Movie));
        var added = new List<Guid>();
        var updated = new List<Guid>();
        changes.ItemAdded += Added;
        changes.ItemUpdated += Updated;
        try
        {
            int before;
            await using (var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken))
            {
                before = await database.BaseItems.CountAsync(TestContext.Current.CancellationToken);
                await database.Database.ExecuteSqlRawAsync($"CREATE TRIGGER fail_item_graph AFTER INSERT ON BaseItems WHEN (SELECT COUNT(*) FROM BaseItems) > {before + 1} BEGIN SELECT RAISE(ABORT, 'second graph row rejected'); END;", TestContext.Current.CancellationToken);
            }

            try
            {
                Assert.ThrowsAny<Exception>(() => writes.CreateItems([primary], null, TestContext.Current.CancellationToken));
                Assert.Null(lookup.GetItemById(primary.Id));
                Assert.Null(lookup.GetItemById(alternateId));
                Assert.Empty(added);
                await using var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken);
                Assert.Equal(before, await database.BaseItems.CountAsync(TestContext.Current.CancellationToken));
                Assert.False(await database.LinkedChildren.AnyAsync(row => row.ParentId.Equals(primary.Id), TestContext.Current.CancellationToken));
            }
            finally
            {
                await using var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken);
                await database.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_item_graph;", TestContext.Current.CancellationToken);
            }

            writes.CreateItems([primary], null, TestContext.Current.CancellationToken);
            Assert.Equal(new[] { primary.Id }, added);
            var alternate = Assert.IsType<Movie>(repository.RetrieveItem(alternateId));
            Assert.Equal(primary.Id, alternate.OwnerId);
            Assert.Equal(primary.Id, alternate.PrimaryVersionId);
            Assert.Equal(sources.SecondPart, Assert.Single(alternate.AdditionalParts));
            Assert.Equal(new[] { alternateId }, linked.GetLinkedChildrenIds(primary.Id, (int)Tesserafin.Controller.Entities.LinkedChildType.LocalAlternateVersion));

            primary.Overview = "One committed description for both versions";
            await writes.UpdateItemAsync(primary, null, ItemUpdateType.MetadataEdit, TestContext.Current.CancellationToken);
            Assert.Equal(primary.Overview, repository.RetrieveItem(alternateId).Overview);
            Assert.Equal(primary.Overview, repository.RetrieveItem(primary.Id).Overview);
            Assert.Equal(new[] { primary.Id, alternateId }, updated);
            Assert.NotEqual(DateTime.UnixEpoch, primary.DateLastSaved);

            await factory.Services.GetRequiredService<IItemDeletionCommands>().DeleteAsync(primary, new DeleteOptions { DeleteFileLocation = false }, TestContext.Current.CancellationToken);
            Assert.Null(lookup.GetItemById(primary.Id));
            var promoted = Assert.IsType<Movie>(repository.RetrieveItem(alternateId));
            Assert.Null(promoted.PrimaryVersionId);
            Assert.Equal(Guid.Empty, promoted.OwnerId);
            Assert.Equal(sources.SecondPart, Assert.Single(promoted.AdditionalParts));
            Assert.True(File.Exists(sources.Primary));
            Assert.True(File.Exists(sources.Alternate));
            Assert.True(File.Exists(sources.SecondPart));
        }
        finally
        {
            changes.ItemAdded -= Added;
            changes.ItemUpdated -= Updated;
        }

        void Added(object? sender, ItemChangeEventArgs args)
        {
            if (args.Item.Id.Equals(primary.Id) && lookup.GetItemById(alternateId) is Movie)
            {
                added.Add(args.Item.Id);
            }
        }

        void Updated(object? sender, ItemChangeEventArgs args)
        {
            if (args.Item.Id.Equals(primary.Id) || args.Item.Id.Equals(alternateId))
            {
                updated.Add(args.Item.Id);
            }
        }
    }

    private sealed class Sources : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("write-graph-host-").FullName;

        public Sources()
        {
            Primary = Path.Combine(_root, "Feature - 2160p.mkv");
            Alternate = Path.Combine(_root, "Feature - 1080p cd1.mkv");
            SecondPart = Path.Combine(_root, "Feature - 1080p cd2.mkv");
            foreach (var path in new[] { Primary, Alternate, SecondPart })
            {
                File.WriteAllBytes(path, [1, 3, 5, 7]);
            }
        }

        public string Primary { get; }

        public string Alternate { get; }

        public string SecondPart { get; }

        public void Dispose() => Directory.Delete(_root, true);
    }
}
