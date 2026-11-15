using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.MediaEncoding.Amf;
using Tesserafin.Controller.MediaEncoding.Nvidia;
using Tesserafin.Controller.MediaEncoding.Qsv;
using Tesserafin.Controller.MediaEncoding.Software;
using Tesserafin.Controller.MediaEncoding.Vaapi;
using Tesserafin.Controller.MediaEncoding.VideoToolbox;
using Xunit;

namespace Tesserafin.Controller.Tests.MediaEncoding;

public sealed partial class PipelineRendererArchitectureTests
{
    [Theory]
    [InlineData("Amf")]
    [InlineData("VideoToolbox")]
    [InlineData("Software")]
    [InlineData("Nvidia")]
    [InlineData("Vaapi")]
    [InlineData("Qsv")]
    public void RendererSources_ReachOnlyImmutableValuesAndFormatting(string backend)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tesserafin.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var root = Path.Combine(directory.FullName, "Tesserafin.Controller", "MediaEncoding", backend);
        var files = Directory.GetFiles(root, "*.cs");
        Assert.NotEmpty(files);
        var allowedImports = new HashSet<string>(StringComparer.Ordinal)
        {
            "System", "System.Collections.Immutable", "System.Globalization", "Tesserafin.Model.Entities",
            "Tesserafin.Controller.MediaEncoding.Software",
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

    [Theory]
    [InlineData(typeof(AmfPipelinePlan))]
    [InlineData(typeof(VideoToolboxPipelinePlan))]
    [InlineData(typeof(SoftwarePipelinePlan))]
    [InlineData(typeof(NvidiaPipelinePlan))]
    [InlineData(typeof(VaapiPipelinePlan))]
    [InlineData(typeof(QsvPipelinePlan))]
    public void CapturedPlans_ContainOnlyImmutableValues(Type planType)
    {
        var types = planType.Assembly.GetTypes()
            .Where(type => type.Namespace == planType.Namespace && !type.IsAbstract);
        foreach (var type in types)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var valueType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                Assert.True(
                    valueType.IsPrimitive || valueType.IsEnum || valueType == typeof(string) || valueType == typeof(Type) || valueType.Namespace == type.Namespace || valueType == typeof(SoftwareScalingPlan) || valueType == typeof(SoftwarePipelinePlan),
                    $"{type.Name}.{property.Name} must not retain a mutable request/configuration object.");
                if (property.SetMethod is { } setter)
                {
                    Assert.Contains(typeof(IsExternalInit), setter.ReturnParameter.GetRequiredCustomModifiers());
                }
            }
        }
    }

    [Theory]
    [InlineData("GetAmdDx11VidFiltersPrefered")]
    [InlineData("GetAmdVidFilterChain")]
    [InlineData("GetAppleVidFiltersPreferred")]
    [InlineData("GetAppleVidFilterChain")]
    [InlineData("GetSwVidFilterChain")]
    [InlineData("GetSwScaleFilter")]
    [InlineData("GetSwDeinterlaceFilter")]
    [InlineData("GetNvidiaVidFilterChain")]
    [InlineData("GetNvidiaVidFiltersPrefered")]
    [InlineData("GetVaapiVidFilterChain")]
    [InlineData("GetIntelVaapiFullVidFiltersPrefered")]
    [InlineData("GetAmdVaapiFullVidFiltersPrefered")]
    [InlineData("GetVaapiLimitedVidFiltersPrefered")]
    [InlineData("GetIntelVidFilterChain")]
    [InlineData("GetIntelQsvDx11VidFiltersPrefered")]
    [InlineData("GetIntelQsvVaapiVidFiltersPrefered")]
    public void EncodingHelper_HasNoFormerRendererImplementationEntryPoints(string method)
        => Assert.Null(typeof(EncodingHelper).GetMethod(method));

    [GeneratedRegex(@"^using\s+([A-Za-z0-9_.]+)\s*;", RegexOptions.Multiline)]
    private static partial Regex ImportPattern();

    [GeneratedRegex(@"\b(EncodingHelper|EncodingJobInfo|EncodingOptions|IMediaEncoder|IConfiguration|IServiceProvider|Environment|File|Directory|Path|Process|HttpClient|DateTime|DateTimeOffset|Random)\b")]
    private static partial Regex ForbiddenRuntimeDependency();
}
