using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Tesserafin.Server.Tests.Architecture;

/// <summary>
/// Consolidation step 0 (#129): the legacy surfaces the programme removes may not grow. Each rule
/// measures the tree as it is and compares it with <c>consolidation-ratchet-baseline.json</c>, the
/// named list of existing exceptions. A new entry fails (new debt); an entry the tree no longer has
/// also fails, so the baseline is edited in the same change and can only shrink.
/// </summary>
/// <remarks>
/// Set <c>CONSOLIDATION_RATCHET_WRITE</c> to a file path to write the measured lists there, for writing a baseline that
/// only removes entries. Never add an entry to make this test pass: remove the new dependency.
/// </remarks>
public sealed partial class ConsolidationRatchetTests
{
    private static readonly string[] _sourceRoots = ["."];

    private static readonly JsonSerializerOptions _indented = new() { WriteIndented = true };

    // Projects whose purpose forbids reaching another project, directly or transitively.
    private static readonly (string From, string To, string Why)[] _forbiddenProjects =
    [
        ("Tesserafin.Model", "Tesserafin.Data", "Model must not depend on persistence"),
        ("Tesserafin.Model", "Tesserafin.Database.Implementations", "Model must not depend on EF"),
        ("Tesserafin.Data", "Tesserafin.Database.Implementations", "shared values must not depend on EF"),
        ("Tesserafin.Server.Core", "Tesserafin.Api", "the core must not depend on HTTP composition"),
        ("Tesserafin.MediaEncoding", "Tesserafin.Playback.Shadow", "encoding must not depend on the shadow comparison"),
    ];

    // The pure playback domain reaches only itself.
    private static readonly string[] _purePlaybackDomain =
    [
        "Tesserafin.Playback.Decision",
        "Tesserafin.Playback.Engine",
        "Tesserafin.Playback.Execution",
    ];

    private static readonly string[] _forbiddenDomainPackages =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
    ];

    [Fact]
    public void Measured_legacy_surfaces_match_the_baseline()
    {
        var root = FindRepositoryRoot();
        var measured = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal)
        {
            ["forbiddenDependencies"] = MeasureForbiddenDependencies(root),
            ["streamBuilderFiles"] = FilesMatching(root, StreamBuilderPattern()),
            ["toStreamInfoCallers"] = FilesMatching(root, ToStreamInfoPattern()),
            ["entityStaticServices"] = MeasureEntityStaticServices(root),
            ["entityStaticServiceAccessFiles"] = MeasureEntityStaticServiceAccess(root),
        };

        if (Environment.GetEnvironmentVariable("CONSOLIDATION_RATCHET_WRITE") is { Length: > 0 } outputPath)
        {
            File.WriteAllText(outputPath, JsonSerializer.Serialize(measured, _indented));
        }

        var baselinePath = Path.Combine(root, "tests", "Tesserafin.Server.Tests", "Architecture", "consolidation-ratchet-baseline.json");
        var baseline = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(baselinePath))!;

        var failures = new List<string>();
        foreach (var (rule, current) in measured)
        {
            var allowed = new SortedSet<string>(baseline.TryGetValue(rule, out var entries) ? entries : [], StringComparer.Ordinal);
            failures.AddRange(current.Except(allowed).Select(entry => $"{rule}: NEW {entry} - remove it; the baseline only shrinks."));
            failures.AddRange(allowed.Except(current).Select(entry => $"{rule}: GONE {entry} - delete it from the baseline in this change."));
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static SortedSet<string> MeasureForbiddenDependencies(string root)
    {
        var projects = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsExcluded(root, path) && !Relative(root, path).StartsWith("tests/", StringComparison.Ordinal))
            .ToDictionary(path => Path.GetFileNameWithoutExtension(path), path => path, StringComparer.Ordinal);

        var references = projects.ToDictionary(
            pair => pair.Key,
            pair => ReadReferences(pair.Value).ToList(),
            StringComparer.Ordinal);
        var packages = projects.ToDictionary(
            pair => pair.Key,
            pair => ReadPackages(pair.Value).ToList(),
            StringComparer.Ordinal);

        HashSet<string> Closure(string project)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>(references.GetValueOrDefault(project) ?? []);
            while (pending.Count > 0)
            {
                var next = pending.Pop();
                if (seen.Add(next))
                {
                    foreach (var child in references.GetValueOrDefault(next) ?? [])
                    {
                        pending.Push(child);
                    }
                }
            }

            return seen;
        }

        var violations = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (from, to, _) in _forbiddenProjects)
        {
            if (projects.ContainsKey(from) && Closure(from).Contains(to))
            {
                violations.Add($"{from} -> {to}");
            }
        }

        foreach (var domain in _purePlaybackDomain.Where(projects.ContainsKey))
        {
            var reach = Closure(domain);
            foreach (var reached in reach.Where(project => !_purePlaybackDomain.Contains(project, StringComparer.Ordinal)))
            {
                violations.Add($"{domain} -> {reached}");
            }

            foreach (var project in reach.Append(domain))
            {
                foreach (var package in (packages.GetValueOrDefault(project) ?? []).Where(p => _forbiddenDomainPackages.Any(f => p.StartsWith(f, StringComparison.Ordinal))))
                {
                    violations.Add($"{domain} -> package:{package}");
                }
            }
        }

        return violations;
    }

    private static SortedSet<string> MeasureEntityStaticServices(string root)
    {
        var declared = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceFiles(root).Where(f => Relative(root, f).StartsWith("Tesserafin.Controller/Entities/", StringComparison.Ordinal)))
        {
            foreach (Match match in StaticServicePattern().Matches(File.ReadAllText(file)))
            {
                declared.Add($"{Path.GetFileNameWithoutExtension(file)}.{match.Groups["name"].Value}");
            }
        }

        return declared;
    }

    private static SortedSet<string> MeasureEntityStaticServiceAccess(string root)
    {
        var services = MeasureEntityStaticServices(root);
        var pattern = new Regex(@"\b(" + string.Join("|", services.Select(Regex.Escape)) + @")\b", RegexOptions.CultureInvariant);
        return FilesMatching(root, pattern);
    }

    private static SortedSet<string> FilesMatching(string root, Regex pattern)
        => new(
            SourceFiles(root)
                .Where(file => File.ReadLines(file).Any(line => !IsComment(line) && pattern.IsMatch(line)))
                .Select(file => Relative(root, file)),
            StringComparer.Ordinal);

    private static IEnumerable<string> SourceFiles(string root)
        => _sourceRoots
            .SelectMany(source => Directory.EnumerateFiles(Path.Combine(root, source), "*.cs", SearchOption.AllDirectories))
            .Where(path => !IsExcluded(root, path) && !Relative(root, path).StartsWith("tests/", StringComparison.Ordinal));

    private static IEnumerable<string> ReadReferences(string csproj)
        => XDocument.Load(csproj).Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(include => include is not null && include.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')));

    private static IEnumerable<string> ReadPackages(string csproj)
        => XDocument.Load(csproj).Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => e.Attribute("Include")?.Value)
            .OfType<string>();

    private static bool IsComment(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*');
    }

    private static bool IsExcluded(string root, string path)
    {
        var relative = Relative(root, path);
        return relative.Contains("/bin/", StringComparison.Ordinal)
            || relative.Contains("/obj/", StringComparison.Ordinal)
            || relative.StartsWith("fuzz/", StringComparison.Ordinal)
            || relative.StartsWith('.');
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tesserafin.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Tesserafin.sln not found above the test output.");
    }

    [GeneratedRegex(@"\bStreamBuilder\b", RegexOptions.CultureInvariant)]
    private static partial Regex StreamBuilderPattern();

    [GeneratedRegex(@"\bToStreamInfo\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex ToStreamInfoPattern();

    [GeneratedRegex(@"public static I[A-Z][A-Za-z]+ (?<name>[A-Z][A-Za-z]+) \{ get; (?:internal |protected )?set; \}", RegexOptions.CultureInvariant)]
    private static partial Regex StaticServicePattern();
}
