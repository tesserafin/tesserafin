using System;
using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Api.Models.LibraryStructureDto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dto;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

public sealed class UserDataConservationEndToEndTests(TesserafinApplicationFactory factory) : IClassFixture<TesserafinApplicationFactory>
{
    [Fact(Timeout = 180_000)]
    public async Task DeleteAndRescanAtNewPath_RestoresEachUsersHistory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(admin));
        var owner = await AuthHelper.GetUserDtoAsync(admin);
        using var createdUser = await admin.PostAsJsonAsync("Users/New", new { Name = "conservation-viewer" }, JsonDefaults.Options, cancellationToken);
        createdUser.EnsureSuccessStatusCode();
        var viewer = await createdUser.Content.ReadFromJsonAsync<UserDto>(JsonDefaults.Options, cancellationToken);
        Assert.NotNull(viewer);

        var scans = factory.Services.GetRequiredService<ILibraryScanCoordinator>();
        var catalogue = factory.Services.GetRequiredService<IItemRepository>();
        var directory = Directory.CreateTempSubdirectory("userdata-conservation-").FullName;
        try
        {
            const string ImdbId = "tt0351351";
            const string LibraryName = "User data conservation";
            var originalPath = await EndToEndMediaFixtures.CreateH264AacMp4Async(directory, $"Original (2026) [imdbid={ImdbId}].mp4", durationSeconds: 1);
            var replacementPath = Path.Combine(directory, $"Replacement (2026) [imdbid={ImdbId}].mp4");
            var body = new AddVirtualFolderDto
            {
                LibraryOptions = new LibraryOptions
                {
                    EnableRealtimeMonitor = false,
                    PathInfos = [new MediaPathInfo(directory)],
                    TypeOptions = [new TypeOptions { Type = nameof(Movie) }],
                },
            };
            using var added = await admin.PostAsJsonAsync("Library/VirtualFolders?collectionType=movies&refreshLibrary=false&name=" + Uri.EscapeDataString(LibraryName), body, JsonDefaults.Options, cancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, added.StatusCode);
            await scans.ScanAsync(new Progress<double>(), cancellationToken);
            var original = Assert.IsType<Movie>(Assert.Single(catalogue.GetItemList(new InternalItemsQuery { Path = originalPath })));
            Assert.Contains(ImdbId, original.GetUserDataKeys());

            var histories = new[]
            {
                (User: owner.Id, Data: new UpdateUserItemDataDto { PlayCount = 3, PlaybackPositionTicks = 2_000_000, IsFavorite = true }),
                (User: viewer.Id, Data: new UpdateUserItemDataDto { PlayCount = 1, PlaybackPositionTicks = 4_000_000, IsFavorite = false }),
            };
            foreach (var (user, history) in histories)
            {
                using var saved = await admin.PostAsJsonAsync(UserDataRoute(original.Id, user), history, JsonDefaults.Options, cancellationToken);
                Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            }

            var media = await File.ReadAllBytesAsync(originalPath, cancellationToken);
            using var deleted = await admin.DeleteAsync($"Items/{original.Id}", cancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            Assert.False(File.Exists(originalPath));
            using var missing = await admin.GetAsync(UserDataRoute(original.Id, owner.Id), cancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

            await File.WriteAllBytesAsync(replacementPath, media, cancellationToken);
            await scans.ScanAsync(new Progress<double>(), cancellationToken);
            var replacement = Assert.IsType<Movie>(Assert.Single(catalogue.GetItemList(new InternalItemsQuery { Path = replacementPath })));
            Assert.NotEqual(original.Id, replacement.Id);
            Assert.Contains(ImdbId, replacement.GetUserDataKeys());

            foreach (var (user, expected) in histories)
            {
                var restored = await admin.GetFromJsonAsync<UserItemDataDto>(UserDataRoute(replacement.Id, user), JsonDefaults.Options, cancellationToken);
                Assert.NotNull(restored);
                Assert.Equal(expected.PlayCount, restored.PlayCount);
                Assert.Equal(expected.PlaybackPositionTicks, restored.PlaybackPositionTicks);
                Assert.Equal(expected.IsFavorite, restored.IsFavorite);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string UserDataRoute(Guid itemId, Guid userId) => $"UserItems/{itemId}/UserData?userId={userId}";
}
