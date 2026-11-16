using AutoFixture;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.IO;
using Tesserafin.Naming.Common;
using Tesserafin.Server.Core.Library;

namespace Tesserafin.Server.Implementations.Tests.Library;

internal static class LibraryWriteFixture
{
    public static IItemWriteCommands Register(IFixture fixture)
    {
        var configuration = fixture.Create<IServerConfigurationManager>();
        if (configuration.GetConfiguration("metadata") is null)
        {
            Mock.Get(configuration).Setup(c => c.GetConfiguration("metadata")).Returns(new MetadataConfiguration());
        }

        var options = new LibraryItemOptions(fixture.Create<ICollectionFolderResolver>(), fixture.Create<IItemLookupService>(), fixture.Create<IUserRootFolderProvider>(), configuration, fixture.Create<IFileSystem>());
        fixture.Inject<ILibraryItemOptions>(options);
        var resolution = new ItemResolutionService(configuration, fixture.Create<IFileSystem>(), fixture.Create<IServerApplicationHost>(), options, fixture.Create<IItemLookupService>(), fixture.Create<NamingOptions>(), NullLogger<ItemResolutionService>.Instance);
        fixture.Inject<IItemResolutionService>(resolution);
        var versions = new ItemVersionGraphPlanner(resolution, options, fixture.Create<IItemLookupService>());
        fixture.Inject(versions);
        var writes = new ItemWriteCommands(versions, fixture.Create<IItemPersistenceService>(), fixture.Create<IItemCacheStore>(), fixture.Create<IItemLookupService>(), fixture.Create<ILinkedChildrenService>(), fixture.Create<IItemMetadataExporter>(), fixture.Create<IItemChangePublisher>(), new ActiveRecordings(), NullLogger<ItemWriteCommands>.Instance);
        fixture.Inject<IItemWriteCommands>(writes);
        return writes;
    }
}
