using System;
using System.IO;

namespace Tesserafin.Model.IO;

/// <summary>
/// Operations on filesystem path strings.
/// </summary>
public static class FilePath
{
    /// <summary>
    /// Makes a relative path absolute against its containing folder.
    /// </summary>
    /// <param name="folderPath">The containing folder.</param>
    /// <param name="filePath">The path to resolve.</param>
    /// <returns>The resolved path, or the original value if it cannot be resolved.</returns>
    public static string MakeAbsolute(string? folderPath, string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return filePath;
        }

        var isAbsolutePath = Path.IsPathRooted(filePath) && (!OperatingSystem.IsWindows() || filePath[0] != '\\');

        if (isAbsolutePath)
        {
            return filePath;
        }

        // Preserve UNC paths on every platform.
        if (filePath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return filePath;
        }

        var filePathSpan = filePath.AsSpan();

        // Treat a single leading backslash as relative to the containing folder.
        if (filePath[0] == '\\')
        {
            filePathSpan = filePathSpan.Slice(1);
        }

        try
        {
            return Path.GetFullPath(Path.Join(folderPath, filePathSpan));
        }
        catch (ArgumentException)
        {
            return filePath;
        }
        catch (PathTooLongException)
        {
            return filePath;
        }
        catch (NotSupportedException)
        {
            return filePath;
        }
    }

    /// <summary>Checks whether a path contains the specified directory and a separator.</summary>
    /// <param name="parentPath">The directory to look for.</param>
    /// <param name="path">The path to check.</param>
    /// <returns>Whether the directory occurs in the path.</returns>
    public static bool ContainsSubPath(string parentPath, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(parentPath);
        ArgumentException.ThrowIfNullOrEmpty(path);

        return path.Contains(
            Path.TrimEndingDirectorySeparator(parentPath) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
