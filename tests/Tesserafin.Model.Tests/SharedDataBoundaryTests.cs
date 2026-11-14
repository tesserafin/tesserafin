using System;
using System.Reflection;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Data.Interfaces;
using Tesserafin.Model.Dto;
using Xunit;

namespace Tesserafin.Model.Tests;

public sealed class SharedDataBoundaryTests
{
    [Theory]
    [InlineData(typeof(User))]
    [InlineData(typeof(BaseItemEntity))]
    [InlineData(typeof(MediaSegment))]
    [InlineData(typeof(PermissionKind))]
    [InlineData(typeof(IHasPermissions))]
    public void SharedTypes_BelongToTheDataAssembly(Type type)
    {
        Assert.Equal(typeof(PermissionKind).Assembly, type.Assembly);
        Assert.Equal("Tesserafin.Data", type.Assembly.GetName().Name);
    }

    [Fact]
    public void DataAndModel_DoNotReferenceTheEfImplementation()
    {
        AssertNoPersistenceReference(typeof(User).Assembly);
        AssertNoPersistenceReference(typeof(BaseItemDto).Assembly);
    }

    private static void AssertNoPersistenceReference(Assembly assembly)
    {
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            reference.Name!.StartsWith("Tesserafin.Database", StringComparison.Ordinal)
            || reference.Name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }
}
