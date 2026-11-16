using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller;
using Tesserafin.Controller.Entities;
using Tesserafin.Model.Configuration;
using Tesserafin.Server.Core.Library;
using Tesserafin.Server.Core.Serialization;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

/// <summary>
/// The library options store, moved off the <c>CollectionFolder</c> statics: one <c>options.xml</c> per
/// library, media paths stored virtually, a cache, and an update event.
/// </summary>
public sealed class LibraryOptionsStoreTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("library-options-");
    private readonly string _library;
    private readonly string _dataPath;
    private readonly LibraryOptionsStore _store;

    public LibraryOptionsStoreTests()
    {
        _library = Directory.CreateDirectory(Path.Combine(_root.FullName, "Movies")).FullName;
        _dataPath = Path.Combine(_root.FullName, "data");
        var paths = Mock.Of<IServerApplicationPaths>(p =>
            p.DataPath == _dataPath
            && p.VirtualDataPath == "%AppDataPath%"
            && p.InternalMetadataPath == Path.Combine(_root.FullName, "metadata")
            && p.VirtualInternalMetadataPath == "%MetadataPath%");
        _store = new LibraryOptionsStore(new MyXmlSerializer(), paths, NullLogger<LibraryOptionsStore>.Instance);
    }

    public void Dispose() => _root.Delete(true);

    [Fact]
    public void Save_WritesVirtualPaths_AndGet_ExpandsThem()
    {
        var media = Path.Combine(_dataPath, "media", "movies");
        var options = new LibraryOptions { PreferredMetadataLanguage = "fr", PathInfos = [new MediaPathInfo(media)] };
        var updates = new List<LibraryOptionsUpdatedEventArgs>();
        _store.Updated += (_, e) => updates.Add(e);

        _store.Save(_library, options);

        var xml = File.ReadAllText(Path.Combine(_library, "options.xml"));
        Assert.Contains("%AppDataPath%", xml, StringComparison.Ordinal);
        Assert.DoesNotContain(_dataPath, xml, StringComparison.Ordinal);
        Assert.Equal(media, options.PathInfos[0].Path);
        Assert.Same(options, _store.Get(_library));
        var update = Assert.Single(updates);
        Assert.Equal(_library, update.LibraryPath);
        Assert.Same(options, update.LibraryOptions);

        _store.Invalidate();
        var reloaded = _store.Get(_library);

        Assert.NotSame(options, reloaded);
        Assert.Equal("fr", reloaded.PreferredMetadataLanguage);
        Assert.Equal(media, reloaded.PathInfos[0].Path);
    }

    [Fact]
    public void Get_MissingFile_IsDefaultOptions()
    {
        var options = _store.Get(_library);

        Assert.Null(options.PreferredMetadataLanguage);
        Assert.Empty(options.PathInfos);
    }

    [Fact]
    public void Get_UnreadableFile_IsDefaultOptions()
    {
        File.WriteAllText(Path.Combine(_library, "options.xml"), "<not-options");

        Assert.Null(_store.Get(_library).PreferredMetadataLanguage);
    }

    [Fact]
    public void Get_IsCachedUntilInvalidated()
    {
        _store.Save(_library, new LibraryOptions { PreferredMetadataLanguage = "fr" });
        var cached = _store.Get(_library);
        File.Delete(Path.Combine(_library, "options.xml"));

        Assert.Same(cached, _store.Get(_library));

        _store.Invalidate();

        Assert.Null(_store.Get(_library).PreferredMetadataLanguage);
    }

    [Fact]
    public void FailedSave_KeepsThePublishedOptions_AndRaisesNothing()
    {
        var published = new LibraryOptions { PreferredMetadataLanguage = "fr" };
        _store.Save(_library, published);
        var file = Path.Combine(_library, "options.xml");
        File.Delete(file);
        Directory.CreateDirectory(file);
        var raised = false;
        _store.Updated += (_, _) => raised = true;

        Assert.ThrowsAny<Exception>(() => _store.Save(_library, new LibraryOptions { PreferredMetadataLanguage = "de" }));

        Assert.Same(published, _store.Get(_library));
        Assert.False(raised);
    }
}
