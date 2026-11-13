using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Model.Entities;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// PR119: registers a real, playable library item against the real, booted server's own persistence
/// (<see cref="ILibraryManager.CreateItems"/> - real EF/SQLite, not a mock) plus real
/// <see cref="MediaStream"/> rows (<see cref="IMediaStreamRepository.SaveMediaStreams"/>), WITHOUT
/// driving a full library-folder scan.
/// </summary>
/// <remarks>
/// <para>
/// Why this is a faithful shortcut and not a fake one: <c>ci/smoke.sh</c>'s own header (and
/// <c>HlsSmokeTests</c>'s remarks) name the exact gap this PR closes - "no existing test harness in
/// this repo provisions a library/media item/auth token." Driving an actual
/// <c>RefreshMediaLibraryTask</c> library scan end to end (virtual folder → disk walk → resolver →
/// probe) would ALSO close that gap, but adds a second large, independently-flaky surface (task
/// scheduling/polling, resolver/naming-convention matching) on top of the one this PR is actually
/// about (the URL contract). This seeder instead drives the same real, final persistence calls a scan
/// would end up making for a single resolved item -
/// <see cref="ILibraryManager.CreateItems(IReadOnlyList{Controller.Entities.BaseItem}, Controller.Entities.BaseItem?, CancellationToken)"/>
/// (real <c>IItemRepository</c>/<c>IItemPersistenceService</c> underneath - confirmed against
/// <c>LibraryManagerItemStoreTests</c>, which cross-checks this exact save-then-register path) and
/// <see cref="IMediaStreamRepository.SaveMediaStreams"/> (the same call
/// <c>MediaSourceManager.GetPlaybackMediaSources</c>'s own FFProbe-refresh branch would end up making) -
/// so the item this seeder produces is retrievable, playable, and stream-source-resolvable through
/// the EXACT SAME code paths <see cref="Tesserafin.Api.Controllers.PlaybackSessionsController"/> uses for
/// a scanned item, with no shortcut taken past that point. What is skipped is ffprobe'ing the file
/// ourselves: since the fixture was itself synthesized by <see cref="EndToEndMediaFixtures"/> with known
/// parameters, the caller supplies matching <see cref="MediaStream"/> rows directly instead of paying
/// for a redundant probe of a file whose exact codec/resolution/duration this same test process just
/// dictated to ffmpeg.
/// </para>
/// </remarks>
public static class LibraryItemSeeder
{
    /// <summary>
    /// Creates, persists, and registers a single-source <see cref="Movie"/> item for
    /// <paramref name="mediaPath"/>.
    /// </summary>
    /// <param name="libraryManager">The real, DI-resolved library manager.</param>
    /// <param name="mediaStreamRepository">The real, DI-resolved media stream repository.</param>
    /// <param name="mediaPath">The real file on disk to register.</param>
    /// <param name="container">The container of <paramref name="mediaPath"/> (for example <c>"mp4"</c>).</param>
    /// <param name="streams">The real, known media streams describing <paramref name="mediaPath"/>.</param>
    /// <param name="name">The item's display name.</param>
    /// <param name="runTimeTicks">The duration of <paramref name="mediaPath"/>.</param>
    /// <param name="tags">The item's tags, for a test that restricts a user by tag.</param>
    /// <param name="parentId">The folder the item is in, for a test that restricts a user by library.</param>
    /// <param name="configure">Anything else a test wants the item to carry before it is saved.</param>
    /// <param name="film">
    /// <see langword="false"/> seeds a plain <see cref="Video"/> instead of a <see cref="Movie"/>: a
    /// video outside the migrated film/episode scope keeps the pre-consolidation playback chain.
    /// <paramref name="configure"/> is not applied to it.
    /// </param>
    /// <param name="video">An explicit video subtype, for example an episode; overrides <paramref name="film"/>.</param>
    /// <returns>The new item's id.</returns>
    public static Guid SeedVideo(
        ILibraryManager libraryManager,
        IMediaStreamRepository mediaStreamRepository,
        string mediaPath,
        string container,
        IReadOnlyList<MediaStream> streams,
        string name = "PR119 End-to-End Fixture",
        long runTimeTicks = EndToEndMediaFixtures.DurationTicks,
        string[]? tags = null,
        Guid parentId = default,
        Action<Movie>? configure = null,
        bool film = true,
        Video? video = null)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(mediaStreamRepository);
        ArgumentNullException.ThrowIfNull(streams);

        var movie = Fill(video ?? (film ? new Movie() : new Video()));
        if (movie is Movie seededFilm)
        {
            configure?.Invoke(seededFilm);
        }

        Video Fill(Video video)
        {
            video.Id = Guid.NewGuid();
            video.Name = name;
            video.Path = mediaPath;
            video.Container = container;
            video.Size = new FileInfo(mediaPath).Length;
            video.RunTimeTicks = runTimeTicks;
            video.VideoType = VideoType.VideoFile;
            video.IsInMixedFolder = true;
            video.DateCreated = DateTime.UtcNow;
            video.DateModified = DateTime.UtcNow;
            video.Tags = tags ?? [];
            video.ParentId = parentId;
            return video;
        }

        // What a scan's refresh would have set: without it every seeded item shares one (empty) key,
        // and a query that groups versions by that key answers with one of them for all.
        movie.PresentationUniqueKey = movie.CreatePresentationUniqueKey();

        // No parent: this is the historical CreateItems path, not IItemStore - real IItemRepository/
        // IItemPersistenceService underneath (LibraryManagerItemStoreTests cross-checks the same call
        // against the real, non-mocked types this factory's DI container hands out).
        libraryManager.CreateItems([movie], null, CancellationToken.None);

        // MediaSourceManager.GetStaticMediaSources reads MediaStreams from this repository, keyed by
        // item id (Tesserafin.Controller.Entities.BaseItem.GetVersionInfo) - never from the in-memory
        // item instance. Must be saved AFTER CreateItems: MediaStreamInfo declares a required
        // navigation to the owning BaseItemEntity row, which only exists once CreateItems has
        // persisted it.
        mediaStreamRepository.SaveMediaStreams(movie.Id, streams, CancellationToken.None);

        return movie.Id;
    }
}
