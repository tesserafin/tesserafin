using System;
using Tesserafin.Common.Extensions;
using Tesserafin.Controller.Configuration;

namespace Tesserafin.Server.Core.Library;

/// <summary>One deterministic identity policy for item creation and media resolution.</summary>
internal static class ItemIdentity
{
    public static Guid Create(IServerConfigurationManager configuration, string key, Type type, bool forceCaseInsensitive = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(type);

        string programDataPath = configuration.ApplicationPaths.ProgramDataPath;
        if (key.StartsWith(programDataPath, StringComparison.Ordinal))
        {
            // Try to normalize paths located underneath program-data in an attempt to make them more portable
            key = key.Substring(programDataPath.Length)
                .TrimStart('/', '\\')
                .Replace('/', '\\');
        }

        if (forceCaseInsensitive || !configuration.Configuration.EnableCaseSensitiveItemIds)
        {
            key = key.ToLowerInvariant();
        }

        key = type.FullName + key;

        return key.GetMD5();
    }
}
