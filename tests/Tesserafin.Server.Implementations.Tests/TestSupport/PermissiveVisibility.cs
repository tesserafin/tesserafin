using System;
using Moq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Data.Entities;
using Tesserafin.Model.Configuration;

namespace Tesserafin.Server.Implementations.Tests.TestSupport;

/// <summary>
/// A visibility scope that admits every item by tags and reports no parents, libraries or rating, for tests
/// of entity read paths whose subject is not visibility.
/// </summary>
internal static class PermissiveVisibility
{
    public static IStandaloneVisibilityService Scope { get; } = Create();

    public static InternalItemsQuery Query(User? user) => new(user) { Visibility = Scope };

    private static IStandaloneVisibilityService Create()
    {
        var scope = new Mock<IStandaloneVisibilityService>();
        scope.Setup(s => s.IsVisibleViaTags(It.IsAny<BaseItem>(), It.IsAny<User>(), It.IsAny<bool>())).Returns(true);
        scope.Setup(s => s.GetParents(It.IsAny<BaseItem>())).Returns(Array.Empty<BaseItem>());
        scope.Setup(s => s.GetCollectionFolders(It.IsAny<BaseItem>())).Returns(Array.Empty<Folder>());
        scope.Setup(s => s.GetLibraryIds()).Returns(Array.Empty<Guid>());
        scope.Setup(s => s.GetLibraryOptions(It.IsAny<BaseItem>())).Returns(new LibraryOptions());
        return scope.Object;
    }
}
