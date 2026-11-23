using System;
using System.Globalization;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Model.IO;

namespace Tesserafin.Server.Core.Library;

/// <summary>Shares named paths and deterministic identities between reads and explicit writes.</summary>
internal sealed class NamedItemIdentity(IServerConfigurationManager configuration, IFileSystem files)
{
    public static string NormalizeName<T>(string name)
        where T : BaseItem, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (typeof(T) == typeof(Year))
        {
            if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var year) || year <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(name), "A year must be a positive integer.");
            }

            return year.ToString(CultureInfo.InvariantCulture);
        }

        return name;
    }

    public string GetPath<T>(string name)
        where T : BaseItem, new()
    {
        name = NormalizeName<T>(name);
        var paths = configuration.ApplicationPaths;
        if (typeof(T) == typeof(MusicArtist))
        {
            return MusicArtist.GetPath(name, files, paths.ArtistsPath);
        }

        if (typeof(T) == typeof(Studio))
        {
            return Studio.GetPath(name, files, paths.StudioPath);
        }

        if (typeof(T) == typeof(Genre))
        {
            return Genre.GetPath(name, files, paths.GenrePath);
        }

        if (typeof(T) == typeof(MusicGenre))
        {
            return MusicGenre.GetPath(name, files, paths.MusicGenrePath);
        }

        if (typeof(T) == typeof(Year))
        {
            return Year.GetPath(name, files, paths.YearPath);
        }

        throw new ArgumentException("The item type does not support named catalogue materialization.", nameof(T));
    }

    public Guid GetId<T>(string name)
        where T : BaseItem, new()
        => ItemIdentity.Create(configuration, GetPath<T>(name), typeof(T), configuration.Configuration.EnableNormalizedItemByNameIds);
}
