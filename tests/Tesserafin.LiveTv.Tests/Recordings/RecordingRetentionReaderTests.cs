using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Moq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Data.Enums;
using Tesserafin.LiveTv.Recordings;
using Tesserafin.Model.MediaInfo;
using Xunit;

namespace Tesserafin.LiveTv.Tests.Recordings;

public sealed class RecordingRetentionReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetExcessFiles_UsesTheExistingScopeAndKeepsTheNewestExistingFile(bool isSeries)
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            const string SeriesKey = "recorded-series";
            Folder folder = isSeries ? new Series { PresentationUniqueKey = SeriesKey } : new Folder();
            folder.Id = Guid.NewGuid();
            var newest = new Episode { Path = Path.Combine(directory.FullName, "newest.ts") };
            var oldest = new Episode { Path = Path.Combine(directory.FullName, "oldest.ts") };
            var missing = new Episode { Path = Path.Combine(directory.FullName, "missing.ts") };
            File.WriteAllText(newest.Path, string.Empty);
            File.WriteAllText(oldest.Path, string.Empty);

            var paths = new Mock<IItemPathLookupService>(MockBehavior.Strict);
            paths.Setup(p => p.FindByPath(directory.FullName, true)).Returns(folder);
            var scopes = new Mock<IItemQueryScopeService>(MockBehavior.Strict);
            if (!isSeries)
            {
                scopes.Setup(s => s.SetTopParentIdsOrAncestors(It.IsAny<InternalItemsQuery>(), It.Is<IReadOnlyCollection<BaseItem>>(p => p.Single() == folder)))
                    .Callback<InternalItemsQuery, IReadOnlyCollection<BaseItem>>((query, _) => query.AncestorIds = [folder.Id]);
            }

            var repository = new Mock<IItemRepository>(MockBehavior.Strict);
            repository.Setup(r => r.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) =>
            {
                Assert.False(query.IsVirtualItem);
                Assert.False(query.IsFolder);
                Assert.False(query.EnableTotalRecordCount);
                Assert.True(query.Recursive);
                Assert.Equal((ItemSortBy.DateCreated, SortOrder.Descending), Assert.Single(query.OrderBy));
                if (isSeries)
                {
                    Assert.Equal(SeriesKey, query.SeriesPresentationUniqueKey);
                    Assert.Equal([BaseItemKind.Episode, BaseItemKind.Season], query.IncludeItemTypes);
                    Assert.Empty(query.AncestorIds);
                }
                else
                {
                    Assert.Null(query.SeriesPresentationUniqueKey);
                    Assert.Equal(folder.Id, Assert.Single(query.AncestorIds));
                    Assert.Empty(query.IncludeItemTypes);
                }

                return new BaseItem[] { missing, newest, oldest };
            });
            var sources = Mock.Of<IMediaSourceManager>(s => s.GetPathProtocol(It.IsAny<string>()) == MediaProtocol.File);
            var reader = new RecordingRetentionReader(paths.Object, scopes.Object, repository.Object, sources);

            Assert.Same(oldest, Assert.Single(reader.GetExcessFiles(directory.FullName, 1)));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void GetExcessFiles_AnAggregateRootDoesNotStartACatalogueQuery()
    {
        const string Path = "/recordings";
        var paths = new Mock<IItemPathLookupService>(MockBehavior.Strict);
        paths.Setup(p => p.FindByPath(Path, true)).Returns(new AggregateFolder());
        var scope = new Mock<IItemQueryScopeService>(MockBehavior.Strict);
        var repository = new Mock<IItemRepository>(MockBehavior.Strict);
        var reader = new RecordingRetentionReader(paths.Object, scope.Object, repository.Object, Mock.Of<IMediaSourceManager>());

        Assert.Empty(reader.GetExcessFiles(Path, 1));
        repository.VerifyNoOtherCalls();
    }
}
