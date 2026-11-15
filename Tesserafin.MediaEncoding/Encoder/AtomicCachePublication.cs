using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Model.IO;

namespace Tesserafin.MediaEncoding.Encoder;

/// <summary>
/// Private extraction staging outside the served cache. Publication is an atomic rename per
/// artifact; multiple artifacts do not form a filesystem transaction.
/// </summary>
internal sealed partial class AtomicCachePublication : IDisposable
{
    private readonly IFileSystem _fileSystem;
    private readonly ILogger _logger;
    private readonly string _directory;
    private readonly HashSet<int> _metadata = [];

    public AtomicCachePublication(IReadOnlyList<string> paths, IFileSystem fileSystem, ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfZero(paths.Count);
        _fileSystem = fileSystem;
        _logger = logger;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var fullPaths = paths.Select(Path.GetFullPath).ToArray();
        var folder = Path.GetDirectoryName(fullPaths[0])!;
        if (fullPaths.Distinct(comparer).Count() != fullPaths.Length || fullPaths.Any(path => !comparer.Equals(folder, Path.GetDirectoryName(path))))
        {
            throw new ArgumentException("A cache publication requires distinct artifacts in one cache folder.", nameof(paths));
        }

        Directory.CreateDirectory(folder);
        _directory = Path.Combine(Path.GetDirectoryName(folder) ?? folder, ".tesserafin-extraction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        Files = Array.AsReadOnly(fullPaths.Select((path, index) => new CacheFile(path, Path.Combine(_directory, index + "-" + Path.GetFileName(path)))).ToArray());
        StagedPaths = Array.AsReadOnly(Files.Select(file => file.StagedPath).ToArray());
    }

    public IReadOnlyList<CacheFile> Files { get; }

    public IReadOnlyList<string> StagedPaths { get; }

    public async Task WriteMetadataAsync(int index, string text, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Files[index].StagedPath + ".meta", text, cancellationToken).ConfigureAwait(false);
        _metadata.Add(index);
    }

    public void Validate()
    {
        for (var index = 0; index < Files.Count; index++)
        {
            RequireContent(Files[index].StagedPath);
            if (_metadata.Contains(index))
            {
                RequireContent(Files[index].StagedPath + ".meta");
            }
        }
    }

    public void Publish(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate();
        // Final cancellation checkpoint. Finish each complete artifact and its freshness marker
        // under the caller's cache locks, without cancellation gaps after renaming has begun.
        cancellationToken.ThrowIfCancellationRequested();
        for (var index = 0; index < Files.Count; index++)
        {
            var file = Files[index];
            Rename(file.StagedPath, file.Path);
            if (_metadata.Contains(index))
            {
                // Data first: metadata failure must never bless old data with the new stamp.
                Rename(file.StagedPath + ".meta", file.Path + ".meta");
            }
        }
    }

    private void RequireContent(string path)
    {
        var file = _fileSystem.GetFileInfo(path);
        if (!file.Exists || file.Length == 0)
        {
            throw new Tesserafin.Common.FfmpegException($"Extraction produced no content at {path}.");
        }
    }

    internal static void Rename(string source, string target)
    {
        // No cross-volume copy/delete fallback: a mount boundary must fail closed.
        var succeeded = OperatingSystem.IsWindows()
            ? MoveFileEx(WindowsPath(source), WindowsPath(target), 1) // REPLACE_EXISTING, without COPY_ALLOWED.
            : RenameUnix(source, target) == 0;
        if (!succeeded)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException($"Cannot atomically publish {source} to {target}.", new Win32Exception(error));
        }
    }

    private static string WindowsPath(string path)
    {
        path = Path.GetFullPath(path);
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("libc", EntryPoint = "rename", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameUnix(string source, string target);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string source, string target, uint flags);

    public void Dispose()
    {
        foreach (var file in Files)
        {
            foreach (var path in new[] { file.StagedPath, file.StagedPath + ".meta" })
            {
                try
                {
                    _fileSystem.DeleteFile(path);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unable to delete extraction staging file {Path}", path);
                }
            }
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to delete extraction staging directory {Path}", _directory);
        }
    }

    internal sealed record CacheFile(string Path, string StagedPath);
}
