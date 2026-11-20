using Xunit;

namespace Tesserafin.Controller.Tests.Entities;

/// <summary>
/// <see cref="BaseItem"/> statics such as <c>BaseItem.LibraryManager</c> are shared mutable process-wide state. Any test class that
/// sets one of them must join this collection so xUnit runs it sequentially with the others instead
/// of in parallel - otherwise one test's static assignment can leak into another test running at the
/// same time (observed as flaky failures in <see cref="BaseItemTests"/> once <see cref="FolderTests"/>
/// started mutating the same statics).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public static class BaseItemStaticStateFixture
{
    public const string Name = "BaseItem static state";
}
