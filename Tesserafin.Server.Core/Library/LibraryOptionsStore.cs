using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Serialization;

namespace Tesserafin.Server.Core.Library
{
    /// <summary>
    /// Sole production <see cref="ILibraryOptionsStore"/>: one <c>options.xml</c> per library folder, with
    /// media paths stored in their virtual form. Moved from the former <c>CollectionFolder</c> statics.
    /// </summary>
    internal sealed class LibraryOptionsStore : ILibraryOptionsStore
    {
        private const string FileName = "options.xml";

        private static readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;

        private readonly ConcurrentDictionary<string, LibraryOptions> _cache = new();
        private readonly IXmlSerializer _xmlSerializer;
        private readonly IServerApplicationPaths _paths;
        private readonly ILogger<LibraryOptionsStore> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="LibraryOptionsStore"/> class.
        /// </summary>
        /// <param name="xmlSerializer">Reads and writes the options file.</param>
        /// <param name="paths">Maps media paths to and from their virtual form.</param>
        /// <param name="logger">The logger.</param>
        public LibraryOptionsStore(IXmlSerializer xmlSerializer, IServerApplicationPaths paths, ILogger<LibraryOptionsStore> logger)
        {
            _xmlSerializer = xmlSerializer;
            _paths = paths;
            _logger = logger;
        }

        /// <inheritdoc />
        public event EventHandler<LibraryOptionsUpdatedEventArgs>? Updated;

        /// <inheritdoc />
        public LibraryOptions Get(string libraryPath)
        {
            ArgumentException.ThrowIfNullOrEmpty(libraryPath);
            return _cache.GetOrAdd(libraryPath, Load);
        }

        /// <inheritdoc />
        public void Save(string libraryPath, LibraryOptions options)
        {
            ArgumentException.ThrowIfNullOrEmpty(libraryPath);
            ArgumentNullException.ThrowIfNull(options);

            var stored = JsonSerializer.Deserialize<LibraryOptions>(JsonSerializer.SerializeToUtf8Bytes(options, _jsonOptions), _jsonOptions)!;
            foreach (var mediaPath in stored.PathInfos)
            {
                if (!string.IsNullOrEmpty(mediaPath.Path))
                {
                    mediaPath.Path = VirtualPaths.Reverse(_paths, mediaPath.Path);
                }
            }

            _xmlSerializer.SerializeToFile(stored, Path.Combine(libraryPath, FileName));
            _cache[libraryPath] = options;

            Updated?.Invoke(this, new LibraryOptionsUpdatedEventArgs(libraryPath, options));
        }

        /// <inheritdoc />
        public void Invalidate() => _cache.Clear();

        private LibraryOptions Load(string libraryPath)
        {
            try
            {
                if (_xmlSerializer.DeserializeFromFile(typeof(LibraryOptions), Path.Combine(libraryPath, FileName)) is not LibraryOptions result)
                {
                    return new LibraryOptions();
                }

                foreach (var mediaPath in result.PathInfos)
                {
                    if (!string.IsNullOrEmpty(mediaPath.Path))
                    {
                        mediaPath.Path = VirtualPaths.Expand(_paths, mediaPath.Path);
                    }
                }

                return result;
            }
            catch (IOException)
            {
                return new LibraryOptions();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading library options");
                return new LibraryOptions();
            }
        }
    }
}
