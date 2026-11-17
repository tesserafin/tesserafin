using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Playlists;
using Tesserafin.Database.Implementations;
using Xunit;

namespace Tesserafin.Server.Integration.Tests;

public sealed class ItemLinkWriteCommandsEndToEndTests(TesserafinApplicationFactory factory) : IClassFixture<TesserafinApplicationFactory>
{
    [Fact]
    public async Task Reroute_RollsBackBothSqlMutations_ThenReconcilesDeduplicatedCacheAndPreservesSources()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        using var sources = new Sources();
        var writes = factory.Services.GetRequiredService<IItemWriteCommands>();
        var lookup = factory.Services.GetRequiredService<IItemLookupService>();
        var links = factory.Services.GetRequiredService<ILinkedChildrenService>();
        var databases = factory.Services.GetRequiredService<IDbContextFactory<TesserafinDbContext>>();
        var source = new Movie { Id = Guid.NewGuid(), Name = "linked source", Path = sources.First };
        var target = new Movie { Id = Guid.NewGuid(), Name = "linked target", Path = sources.Second };
        var first = new Playlist { Id = Guid.NewGuid(), Name = "linked-reroute-first" };
        var dedupe = new Playlist { Id = Guid.NewGuid(), Name = "linked-reroute-dedupe" };
        var shortcut = new Playlist { Id = Guid.NewGuid(), Name = "linked-reroute-shortcut" };
        writes.CreateItems([source, target, first, dedupe, shortcut], null, TestContext.Current.CancellationToken);
        writes.UpsertLinkedChild(first.Id, source.Id, LinkedChildType.Manual, TestContext.Current.CancellationToken);
        writes.UpsertLinkedChild(dedupe.Id, source.Id, LinkedChildType.Manual, TestContext.Current.CancellationToken);
        writes.UpsertLinkedChild(dedupe.Id, target.Id, LinkedChildType.Manual, TestContext.Current.CancellationToken);
        writes.UpsertLinkedChild(shortcut.Id, source.Id, LinkedChildType.Shortcut, TestContext.Current.CancellationToken);
        var cachedFirst = Assert.IsType<Playlist>(lookup.GetItemById(first.Id));
        var cachedDedupe = Assert.IsType<Playlist>(lookup.GetItemById(dedupe.Id));
        var cachedShortcut = Assert.IsType<Playlist>(lookup.GetItemById(shortcut.Id));
        Assert.NotSame(first, cachedFirst);
        Assert.Equal(source.Id, Assert.Single(cachedFirst.LinkedChildren).ItemId);
        Assert.Equal(2, cachedDedupe.LinkedChildren.Length);
        var beforeFirst = cachedFirst.LinkedChildren;
        var beforeDedupe = cachedDedupe.LinkedChildren;
        var beforeShortcut = cachedShortcut.LinkedChildren;

        await using (var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            await database.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_link_reroute BEFORE DELETE ON LinkedChildren WHEN OLD.ChildType = 0 AND EXISTS (SELECT 1 FROM BaseItems WHERE Id = OLD.ParentId AND Name = 'linked-reroute-dedupe') BEGIN SELECT RAISE(ABORT, 'reroute second mutation rejected'); END;", TestContext.Current.CancellationToken);
        }

        try
        {
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => writes.RerouteLinkedChildReferencesAsync(source.Id, target.Id, TestContext.Current.CancellationToken));
            Assert.Contains("reroute second mutation rejected", failure.ToString(), StringComparison.Ordinal);
            Assert.Equal(new[] { source.Id }, links.GetLinkedChildrenIds(first.Id, (int)LinkedChildType.Manual));
            Assert.True(links.GetLinkedChildrenIds(dedupe.Id, (int)LinkedChildType.Manual).ToHashSet().SetEquals([source.Id, target.Id]));
            Assert.Same(beforeFirst, cachedFirst.LinkedChildren);
            Assert.Same(beforeDedupe, cachedDedupe.LinkedChildren);
        }
        finally
        {
            await using var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken);
            await database.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_link_reroute;", TestContext.Current.CancellationToken);
        }

        await writes.RerouteLinkedChildReferencesAsync(source.Id, target.Id, TestContext.Current.CancellationToken);

        Assert.Equal(target.Id, Assert.Single(cachedFirst.LinkedChildren).ItemId);
        Assert.Equal(target.Id, Assert.Single(cachedDedupe.LinkedChildren).ItemId);
        Assert.Same(beforeShortcut, cachedShortcut.LinkedChildren);
        Assert.Equal(source.Id, Assert.Single(cachedShortcut.LinkedChildren).ItemId);
        Assert.Same(cachedFirst, lookup.GetItemById(first.Id));
        Assert.Equal(new[] { target.Id }, links.GetLinkedChildrenIds(first.Id, (int)LinkedChildType.Manual));
        Assert.Equal(new[] { target.Id }, links.GetLinkedChildrenIds(dedupe.Id, (int)LinkedChildType.Manual));
        Assert.Equal(new[] { source.Id }, links.GetLinkedChildrenIds(shortcut.Id, (int)LinkedChildType.Shortcut));
        Assert.Empty(links.GetManualLinkedParentIds(source.Id));
        Assert.Equal(new byte[] { 1, 3, 5, 7 }, await File.ReadAllBytesAsync(sources.First, TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 1, 3, 5, 7 }, await File.ReadAllBytesAsync(sources.Second, TestContext.Current.CancellationToken));
    }

    private sealed class Sources : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("linked-write-").FullName;

        public Sources()
        {
            First = Path.Combine(_root, "first.mkv");
            Second = Path.Combine(_root, "second.mkv");
            File.WriteAllBytes(First, [1, 3, 5, 7]);
            File.WriteAllBytes(Second, [1, 3, 5, 7]);
        }

        public string First { get; }

        public string Second { get; }

        public void Dispose() => Directory.Delete(_root, true);
    }
}
