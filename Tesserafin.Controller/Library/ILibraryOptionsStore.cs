using System;
using Tesserafin.Controller.Entities;
using Tesserafin.Model.Configuration;

namespace Tesserafin.Controller.Library
{
    /// <summary>
    /// Owns each library's persisted options (<c>options.xml</c> in the library folder): reading, caching,
    /// writing and change notification.
    /// </summary>
    /// <remarks>
    /// A leaf service: implementations depend on serialization and application paths only, never on
    /// <see cref="ILibraryManager"/> or a service locator, so library membership and visibility services may use it.
    /// </remarks>
    public interface ILibraryOptionsStore
    {
        /// <summary>
        /// Occurs after a library's options were saved.
        /// </summary>
        event EventHandler<LibraryOptionsUpdatedEventArgs>? Updated;

        /// <summary>
        /// Gets the options of the library stored at <paramref name="libraryPath"/>.
        /// </summary>
        /// <param name="libraryPath">The library folder path.</param>
        /// <returns>The cached options; default options when the file is missing or unreadable.</returns>
        LibraryOptions Get(string libraryPath);

        /// <summary>
        /// Writes the options of the library stored at <paramref name="libraryPath"/>, caches them and raises
        /// <see cref="Updated"/>.
        /// </summary>
        /// <param name="libraryPath">The library folder path.</param>
        /// <param name="options">The options.</param>
        void Save(string libraryPath, LibraryOptions options);

        /// <summary>
        /// Drops every cached entry, after the set of libraries changed on disk.
        /// </summary>
        void Invalidate();
    }
}
