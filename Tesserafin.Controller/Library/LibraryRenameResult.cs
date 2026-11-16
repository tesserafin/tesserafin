namespace Tesserafin.Controller.Library;

/// <summary>The outcome of a library rename, independent of HTTP.</summary>
public enum LibraryRenameResult
{
    /// <summary>The rename completed.</summary>
    Renamed,

    /// <summary>The source library does not exist.</summary>
    NotFound,

    /// <summary>The destination library already exists.</summary>
    Conflict
}
