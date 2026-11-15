using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.MediaEncoding.Software;
using Xunit;

namespace Tesserafin.Controller.Tests.MediaEncoding;

public sealed partial class SoftwarePipelineArchitectureTests
{
    [Fact]
    public void RendererSources_ReachOnlyImmutableValuesAndFormatting()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tesserafin.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var root = Path.Combine(directory.FullName, "Tesserafin.Controller", "MediaEncoding", "Software");
        var files = Directory.GetFiles(root, "*.cs");
        Assert.NotEmpty(files);
        var allowedImports = new HashSet<string>(StringComparer.Ordinal)
        {
            "System", "System.Collections.Immutable", "System.Globalization", "Tesserafin.Model.Entities",
        };
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            foreach (Match match in ImportPattern().Matches(source))
            {
                Assert.Contains(match.Groups[1].Value, allowedImports);
            }

            Assert.False(ForbiddenRuntimeDependency().IsMatch(source), $"{Path.GetFileName(file)} must not reach a job, configuration, service or I/O API.");
        }
    }

    [Fact]
    public void CapturedPlans_ContainOnlyImmutableValues()
    {
        var types = typeof(SoftwarePipelinePlan).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(SoftwarePipelinePlan).Namespace && type != typeof(SoftwarePipelineRenderer));
        foreach (var type in types)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var valueType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                Assert.True(
                    valueType.IsPrimitive || valueType.IsEnum || valueType == typeof(string) || valueType == typeof(Type) || valueType.Namespace == type.Namespace,
                    $"{type.Name}.{property.Name} must not retain a mutable request/configuration object.");
                if (property.SetMethod is { } setter)
                {
                    Assert.Contains(typeof(IsExternalInit), setter.ReturnParameter.GetRequiredCustomModifiers());
                }
            }
        }
    }

    [Theory]
    [InlineData("GetSwVidFilterChain")]
    [InlineData("GetSwScaleFilter")]
    [InlineData("GetSwDeinterlaceFilter")]
    public void EncodingHelper_HasNoFormerSoftwareImplementationEntryPoints(string method)
        => Assert.Null(typeof(EncodingHelper).GetMethod(method));

    [GeneratedRegex(@"^using\s+([A-Za-z0-9_.]+)\s*;", RegexOptions.Multiline)]
    private static partial Regex ImportPattern();

    [GeneratedRegex(@"\b(EncodingHelper|EncodingJobInfo|EncodingOptions|IMediaEncoder|IConfiguration|IServiceProvider|Environment|File|Directory|Path|Process|HttpClient|DateTime|DateTimeOffset|Random)\b")]
    private static partial Regex ForbiddenRuntimeDependency();
}
