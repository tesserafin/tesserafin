using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller;
using Tesserafin.Model.IO;

namespace Tesserafin.Server.Core.Library;

/// <summary>Shared interpretation of library shortcut locations for queries and commands.</summary>
internal static class LibraryPathReader
{
    public static string[] ReadLocations(string path, IFileSystem files, IServerApplicationHost host, ILogger logger)
        => files.GetFilePaths(path, false)
            .Where(file => Path.GetExtension(file.AsSpan()).Equals(".mblink", StringComparison.OrdinalIgnoreCase))
            .Select(file =>
            {
                try
                {
                    return host.ExpandVirtualPath(files.ResolveShortcut(file));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error resolving shortcut file {File}", file);
                    return null;
                }
            })
            .Where(path => path is not null)
            .Select(path => path!)
            .Order()
            .ToArray();
}
