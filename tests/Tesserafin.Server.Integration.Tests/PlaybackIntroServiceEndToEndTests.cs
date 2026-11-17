using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Dto;
using Tesserafin.Model.IO;
using Tesserafin.Model.Querying;
using Xunit;

namespace Tesserafin.Server.Integration.Tests;

public sealed class PlaybackIntroServiceEndToEndTests(TesserafinApplicationFactory factory) : IClassFixture<TesserafinApplicationFactory>
{
    [Fact]
    public async Task HttpIntros_ResolveSavedVideos_FilterVisibility_AndIsolateProviderFailure_WithoutSavingCandidates()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var intros = factory.Services.GetRequiredService<IPlaybackIntroService>();
        var resolution = factory.Services.GetRequiredService<IItemResolutionService>();
        var writes = factory.Services.GetRequiredService<IItemWriteCommands>();
        var repository = factory.Services.GetRequiredService<IItemRepository>();
        var users = factory.Services.GetRequiredService<IUserManager>();
        var files = factory.Services.GetRequiredService<IFileSystem>();
        var viewer = await users.CreateUserAsync("intro-viewer-" + Guid.NewGuid().ToString("N"));
        viewer.SetPreference(PreferenceKind.BlockedTags, ["hidden-intro"]);
        await users.UpdateUserAsync(viewer);
        var directory = Directory.CreateTempSubdirectory("intro-http-").FullName;
        try
        {
            var savedPath = Path.Combine(directory, "saved.mp4");
            var unsavedPath = Path.Combine(directory, "unsaved.mp4");
            await File.WriteAllBytesAsync(savedPath, [1, 2, 3], TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(unsavedPath, [4, 5, 6], TestContext.Current.CancellationToken);
            var saved = Assert.IsAssignableFrom<Video>(resolution.ResolvePath(files.GetFileSystemInfo(savedPath)));
            saved.Name = "persisted intro metadata";
            var unsaved = Assert.IsAssignableFrom<Video>(resolution.ResolvePath(files.GetFileSystemInfo(unsavedPath)));
            var main = new Movie { Id = Guid.NewGuid(), Name = "main film" };
            var byId = new Video { Id = Guid.NewGuid(), Name = "intro by id", Tags = ["hide-on-next-request"] };
            var hidden = new Video { Id = Guid.NewGuid(), Name = "hidden intro", Tags = ["hidden-intro"] };
            var nonVideo = new Folder { Id = Guid.NewGuid(), Name = "not an intro" };
            writes.CreateItems([main, byId, saved, hidden, nonVideo], null, TestContext.Current.CancellationToken);
            var broken = new Mock<IIntroProvider>();
            broken.SetupGet(p => p.Name).Returns("unavailable provider");
            broken.Setup(p => p.GetIntrosAsync(It.IsAny<BaseItem>(), It.IsAny<User>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("provider unavailable"));
            var provider = new Mock<IIntroProvider>();
            provider.SetupGet(p => p.Name).Returns("ordered provider");
            provider.Setup(p => p.GetIntrosAsync(It.Is<BaseItem>(i => i.Id == main.Id), It.Is<User>(u => u.Id == viewer.Id), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new IntroInfo[]
                {
                    new() { ItemId = byId.Id },
                    new() { Path = savedPath },
                    new() { ItemId = hidden.Id },
                    new() { ItemId = Guid.NewGuid() },
                    new() { ItemId = nonVideo.Id },
                    new() { Path = unsavedPath },
                    new(),
                });
            intros.AddParts([broken.Object, provider.Object]);

            using var response = await client.GetAsync($"Items/{main.Id}/Intros?userId={viewer.Id}", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<QueryResult<BaseItemDto>>(JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(result);
            Assert.Equal(new[] { byId.Id, saved.Id }, result.Items.Select(i => i.Id));
            Assert.Equal("persisted intro metadata", result.Items[1].Name);
            Assert.Null(repository.RetrieveItem(unsaved.Id));

            viewer.SetPreference(PreferenceKind.BlockedTags, ["hidden-intro", "hide-on-next-request"]);
            await users.UpdateUserAsync(viewer);
            var after = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>($"Items/{main.Id}/Intros?userId={viewer.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(after);
            Assert.Equal(saved.Id, Assert.Single(after.Items).Id);
            Assert.Null(repository.RetrieveItem(unsaved.Id));
            provider.Verify(p => p.GetIntrosAsync(It.Is<BaseItem>(i => i.Id == main.Id), It.Is<User>(u => u.Id == viewer.Id), It.IsAny<CancellationToken>()), Times.Exactly(2));
        }
        finally
        {
            intros.AddParts([]);
            Directory.Delete(directory, true);
        }
    }
}
