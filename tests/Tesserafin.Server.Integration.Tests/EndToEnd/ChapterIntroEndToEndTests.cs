using System;
using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Api.Models.LibraryStructureDto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.MediaSegments;
using Tesserafin.Model.Querying;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

public sealed class ChapterIntroEndToEndTests(E2eApplicationFactory factory) : IClassFixture<E2eApplicationFactory>
{
    [Fact(Timeout = 180_000)]
    public async Task ScanAndReadSegments_UsesEachEpisodesOwnBoundaries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();
        var (token, _) = await factory.EnsureAuthenticatedAsync();
        client.DefaultRequestHeaders.AddAuthHeader(token);
        var scans = factory.Services.GetRequiredService<ILibraryScanCoordinator>();
        var catalogue = factory.Services.GetRequiredService<IItemRepository>();
        var sources = factory.Services.GetRequiredService<IMediaSourceManager>();

        var directory = Directory.CreateTempSubdirectory("chapter-intro-").FullName;
        try
        {
            var source = await EndToEndMediaFixtures.CreateH264AacMp4Async(directory, durationSeconds: 9);
            var television = Directory.CreateDirectory(Path.Combine(directory, "TV")).FullName;
            var season = Directory.CreateDirectory(Path.Combine(television, "Chapter Series", "Season 01")).FullName;

            var episodes = new[]
            {
                (File: "Chapter Series S01E01.mkv", Label: "Intro", Start: 0, End: 4_000),
                (File: "Chapter Series S01E02.mkv", Label: "Intro", Start: 1_000, End: 6_000),
                (File: "Chapter Series S01E03.mkv", Label: "Prologue", Start: 0, End: 4_000),
            };

            foreach (var episode in episodes)
            {
                var coldOpen = episode.Start == 0 ? string.Empty : Chapter("Cold open", 0, episode.Start);
                await EndToEndMediaFixtures.MuxChaptersAsync(
                    source,
                    Path.Combine(season, episode.File),
                    coldOpen + Chapter(episode.Label, episode.Start, episode.End) + Chapter("Story", episode.End, 8_500),
                    cancellationToken);
            }

            var body = new AddVirtualFolderDto
            {
                LibraryOptions = new LibraryOptions
                {
                    EnableRealtimeMonitor = false,
                    PathInfos = [new MediaPathInfo(television)],
                    TypeOptions = [new TypeOptions { Type = nameof(Series) }, new TypeOptions { Type = nameof(Season) }, new TypeOptions { Type = nameof(Episode) }],
                },
            };
            using var added = await client.PostAsJsonAsync("Library/VirtualFolders?collectionType=tvshows&refreshLibrary=false&name=ChapterIntros", body, JsonDefaults.Options, cancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, added.StatusCode);
            await scans.ScanAsync(new Progress<double>(), cancellationToken);

            foreach (var expectation in episodes)
            {
                var episode = Assert.IsType<Episode>(Assert.Single(catalogue.GetItemList(new InternalItemsQuery { Path = Path.Combine(season, expectation.File) })));
                var mediaSource = Assert.Single(sources.GetStaticMediaSources(episode, enablePathSubstitution: false));
                Assert.Equal(expectation.Label == "Intro", mediaSource.HasSegments);
                var route = $"MediaSegments/{mediaSource.Id}";
                var result = await client.GetFromJsonAsync<QueryResult<MediaSegmentDto>>(route, JsonDefaults.Options, cancellationToken);
                Assert.NotNull(result);
                if (expectation.Label != "Intro")
                {
                    Assert.Empty(result.Items);
                    continue;
                }

                var intro = Assert.Single(result.Items);
                Assert.Equal(episode.Id, intro.ItemId);
                Assert.Equal(MediaSegmentType.Intro, intro.Type);
                Assert.Equal(TimeSpan.FromMilliseconds(expectation.Start).Ticks, intro.StartTicks);
                Assert.Equal(TimeSpan.FromMilliseconds(expectation.End).Ticks, intro.EndTicks);

                var outros = await client.GetFromJsonAsync<QueryResult<MediaSegmentDto>>(route + "?includeSegmentTypes=Outro", JsonDefaults.Options, cancellationToken);
                Assert.NotNull(outros);
                Assert.Empty(outros.Items);
            }

            var firstPath = Path.Combine(season, episodes[0].File);
            var first = Assert.IsType<Episode>(Assert.Single(catalogue.GetItemList(new InternalItemsQuery { Path = firstPath })));
            await EndToEndMediaFixtures.MuxChaptersAsync(source, firstPath, Chapter("Story", 0, 8_500), cancellationToken);
            File.SetLastWriteTimeUtc(firstPath, DateTime.UtcNow.AddSeconds(2));
            await scans.ScanAsync(new Progress<double>(), cancellationToken);

            var replaced = await client.GetFromJsonAsync<QueryResult<MediaSegmentDto>>($"MediaSegments/{first.Id}", JsonDefaults.Options, cancellationToken);
            Assert.NotNull(replaced);
            Assert.Empty(replaced.Items);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Chapter(string title, int start, int end)
        => FormattableString.Invariant($"[CHAPTER]\nTIMEBASE=1/1000\nSTART={start}\nEND={end}\ntitle={title}\n");
}
