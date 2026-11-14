using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller;
using Tesserafin.Controller.Drawing;
using Tesserafin.Controller.Entities;
using Tesserafin.Data.Entities;
using Tesserafin.Database.Implementations;
using Tesserafin.Database.Implementations.Locking;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Globalization;
using Tesserafin.Server.Implementations.Item;
using Tesserafin.Server.Implementations.Tests.Data;
using Xunit;
using KeyframeDataDto = Tesserafin.MediaEncoding.Keyframes.KeyframeData;

namespace Tesserafin.Server.Implementations.Tests.Item;

/// <summary>
/// The repositories that replace the rows of an item: delete the previous set, then save the new one.
/// A save that fails after the delete ran must reach the caller and leave the previous set, as a new
/// connection reads it; the next save of the item must succeed from another thread; another item's
/// rows are never touched. Runs against a migrated SQLite file once per database locking mode.
/// </summary>
public abstract class ReplaceSetRepositoryTests : IDisposable
{
    private readonly MigratedSqliteDatabase _database;
    private readonly Guid _itemId;
    private readonly Guid _otherItemId;

    protected ReplaceSetRepositoryTests(IEntityFrameworkCoreLockingBehavior locking)
    {
        _database = new MigratedSqliteDatabase(locking);
        _itemId = SeedItem();
        _otherItemId = SeedItem();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _database.Dispose();
        }
    }

    [Fact]
    public void SaveMediaStreams_FailedSave_KeepsThePreviousStreams()
    {
        static MediaStreamRepository Repository(IDbContextFactory<TesserafinDbContext> factory)
            => new(factory, Mock.Of<IServerApplicationHost>(), Mock.Of<ILocalizationManager>());

        static MediaStream[] Streams(params string[] codecs)
            => codecs.Select((codec, index) => new MediaStream { Index = index, Type = MediaStreamType.Audio, Codec = codec }).ToArray();

        AssertReplaceIsAtomic(
            (factory, item, codecs) => Repository(factory).SaveMediaStreams(item, Streams(codecs), CancellationToken.None),
            (context, item) => context.MediaStreamInfos.Where(e => e.ItemId.Equals(item)).OrderBy(e => e.StreamIndex).Select(e => e.Codec!).ToArray());
    }

    [Fact]
    public void SaveMediaStreams_DuplicateStreamIndex_KeepsThePreviousStreams()
    {
        // A real input that fails without any injection: two streams on one key (ItemId, StreamIndex).
        // EF Core refuses to track the second one, after the delete of the previous set already ran.
        var repository = new MediaStreamRepository(_database.Factory(), Mock.Of<IServerApplicationHost>(), Mock.Of<ILocalizationManager>());
        var previous = new[] { new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264" } };
        repository.SaveMediaStreams(_itemId, previous, CancellationToken.None);

        var duplicate = new[]
        {
            new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "aac" },
            new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "opus" },
        };
        Assert.IsType<InvalidOperationException>(Record.Exception(() => repository.SaveMediaStreams(_itemId, duplicate, CancellationToken.None)));

        using var context = _database.CreateDbContext();
        Assert.Equal(["h264"], context.MediaStreamInfos.Where(e => e.ItemId.Equals(_itemId)).Select(e => e.Codec!).ToArray());
        AssertNextSaveSucceeds(
            () => repository.SaveMediaStreams(_itemId, [new MediaStream { Index = 0, Type = MediaStreamType.Audio, Codec = "flac" }], CancellationToken.None),
            () =>
            {
                using var reread = _database.CreateDbContext();
                return reread.MediaStreamInfos.Where(e => e.ItemId.Equals(_itemId)).Select(e => e.Codec!).ToArray();
            },
            ["flac"]);
    }

    [Fact]
    public void SaveChapters_FailedSave_KeepsThePreviousChapters()
    {
        AssertReplaceIsAtomic(
            (factory, item, names) => new ChapterRepository(factory, Mock.Of<IImageProcessor>())
                .SaveChapters(item, names.Select((name, index) => new ChapterInfo { Name = name, StartPositionTicks = index * 1000 }).ToArray()),
            (context, item) => context.Chapters.Where(e => e.ItemId.Equals(item)).OrderBy(e => e.ChapterIndex).Select(e => e.Name!).ToArray());
    }

    [Fact]
    public void SaveMediaAttachments_FailedSave_KeepsThePreviousAttachments()
    {
        AssertReplaceIsAtomic(
            (factory, item, codecs) => new MediaAttachmentRepository(factory)
                .SaveMediaAttachments(item, codecs.Select((codec, index) => new MediaAttachment { Index = index, Codec = codec }).ToArray(), CancellationToken.None),
            (context, item) => context.AttachmentStreamInfos.Where(e => e.ItemId.Equals(item)).OrderBy(e => e.Index).Select(e => e.Codec!).ToArray());
    }

    [Fact]
    public void SaveKeyframeDataAsync_FailedSave_KeepsThePreviousKeyframes()
    {
        // The asynchronous transaction of the set: one row per item, its ticks as the set.
        AssertReplaceIsAtomic(
            (factory, item, ticks) => new KeyframeRepository(factory)
                .SaveKeyframeDataAsync(item, new KeyframeDataDto(ticks.Length, ticks.Select(long.Parse).ToArray()), CancellationToken.None)
                .GetAwaiter().GetResult(),
            (context, item) => context.KeyframeData.Where(e => e.ItemId.Equals(item)).AsEnumerable().SelectMany(e => e.KeyframeTicks!).Select(tick => tick.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
            previous: ["1", "2"],
            next: ["3"]);
    }

    [Fact]
    public void SaveImagesAsync_FailedSave_KeepsThePreviousImages()
    {
        AssertReplaceIsAtomic(
            (factory, item, paths) => new ItemPersistenceService(factory, Mock.Of<IServerApplicationHost>(), NullLogger<ItemPersistenceService>.Instance)
                .SaveImagesAsync(new Video
                {
                    Id = item,
                    ImageInfos = paths.Select(path => new ItemImageInfo { Path = path, Type = ImageType.Primary, DateModified = DateTime.UnixEpoch }).ToArray(),
                })
                .GetAwaiter().GetResult(),
            (context, item) => context.BaseItemImageInfos.Where(e => e.ItemId.Equals(item)).Select(e => e.Path).OrderBy(path => path).ToArray(),
            previous: ["/images/a.jpg", "/images/b.jpg"],
            next: ["/images/c.jpg"]);
    }

    private void AssertReplaceIsAtomic(
        Action<IDbContextFactory<TesserafinDbContext>, Guid, string[]> save,
        Func<TesserafinDbContext, Guid, string[]> read,
        string[]? previous = null,
        string[]? next = null)
    {
        previous ??= ["before-0", "before-1"];
        next ??= ["after-0"];
        string[] Read(Guid item)
        {
            using var context = _database.CreateDbContext();
            return read(context, item);
        }

        save(_database.Factory(), _itemId, previous);
        save(_database.Factory(), _otherItemId, previous);
        Assert.Equal(previous, Read(_itemId));

        // Each save runs on a thread of its own under a deadline, so that a lock it leaves behind
        // fails the next step instead of hanging the suite.
        var failing = new MigratedSqliteDatabase.FailingSaveInterceptor();
        var error = MigratedSqliteDatabase.RunOnAnotherThread(() => save(_database.Factory(failing), _itemId, next));
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(1, failing.Failures);

        Assert.Equal(previous, Read(_itemId));
        AssertNextSaveSucceeds(() => save(_database.Factory(), _itemId, next), () => Read(_itemId), next);
        Assert.Equal(previous, Read(_otherItemId));
    }

    private static void AssertNextSaveSucceeds(Action save, Func<string[]> read, string[] expected)
    {
        Assert.Null(MigratedSqliteDatabase.RunOnAnotherThread(save));
        Assert.Equal(expected, read());
    }

    private Guid SeedItem()
    {
        var id = Guid.NewGuid();
        using var context = _database.CreateDbContext();
        context.BaseItems.Add(new BaseItemEntity { Id = id, Type = "Tesserafin.Controller.Entities.Video", Name = "item" });
        context.SaveChanges();
        return id;
    }

#pragma warning disable CA1034 // Nested types should not be visible

    /// <summary>
    /// The default mode.
    /// </summary>
    public sealed class NoLock : ReplaceSetRepositoryTests
    {
        public NoLock()
            : base(new NoLockBehavior(NullLogger<NoLockBehavior>.Instance))
        {
        }
    }

    /// <summary>
    /// A process-wide reader/writer lock around commands and transactions.
    /// </summary>
    public sealed class Pessimistic : ReplaceSetRepositoryTests
    {
        public Pessimistic()
            : base(new PessimisticLockBehavior(NullLogger<PessimisticLockBehavior>.Instance, NullLoggerFactory.Instance))
        {
        }
    }

    /// <summary>
    /// Retries of commands and transactions that find the database locked.
    /// </summary>
    public sealed class Optimistic : ReplaceSetRepositoryTests
    {
        public Optimistic()
            : base(new OptimisticLockBehavior(NullLogger<OptimisticLockBehavior>.Instance))
        {
        }
    }

#pragma warning restore CA1034
}
