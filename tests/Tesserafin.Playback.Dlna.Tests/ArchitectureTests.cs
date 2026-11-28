using System;
using System.Linq;
using System.Reflection;
using Tesserafin.Playback.Decision;
using Xunit;

namespace Tesserafin.Playback.Dlna.Tests;

/// <summary>
/// Tests that the DLNA-to-domain adapter respects the one-way boundary from RFC PR91 §7: the
/// adapter may depend on the domain, but the domain must never depend back on the adapter, and
/// every public conversion on the facade must return a domain type.
/// </summary>
public static class ArchitectureTests
{
    [Fact]
    public static void DlnaPlaybackAdapter_AllPublicMethodsReturnDomainTypes()
    {
        var decisionAssembly = typeof(PlaybackDecision).Assembly;

        var publicMethods = typeof(DlnaPlaybackAdapter)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => !m.IsSpecialName);

        Assert.NotEmpty(publicMethods);
        Assert.All(publicMethods, method => Assert.Same(decisionAssembly, method.ReturnType.Assembly));
    }

    [Fact]
    public static void DecisionAssembly_FullNameStartsWithExpectedNamespace()
    {
        var decisionAssemblyName = typeof(ClientCapabilities).Assembly.FullName;

        Assert.NotNull(decisionAssemblyName);
        Assert.StartsWith("Tesserafin.Playback.Decision", decisionAssemblyName, StringComparison.Ordinal);
    }

    [Fact]
    public static void DlnaAssembly_ReferencesDecisionAssembly()
    {
        var referencedNames = typeof(DlnaPlaybackAdapter).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name);

        Assert.Contains(
            referencedNames,
            name => name is not null && name.StartsWith("Tesserafin.Playback.Decision", StringComparison.Ordinal));
    }

    [Fact]
    public static void DecisionAssembly_DoesNotReferenceDlnaAssembly()
    {
        var referencedNames = typeof(PlaybackDecision).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name);

        Assert.DoesNotContain(
            referencedNames,
            name => name is not null && name.StartsWith("Tesserafin.Playback.Dlna", StringComparison.Ordinal));
    }
}
