using System;
using Tesserafin.Controller.Net.PlaybackCredentials;

namespace Tesserafin.Server.Integration.Tests.PlaybackCredentials;

/// <summary>
/// One media route, named the way #153 names media classes.
/// </summary>
/// <param name="Name">A stable name, used in test output.</param>
/// <param name="MediaClass">The media class from #153-A0-R1 this route belongs to.</param>
/// <param name="Scope">The capability scope the route demands.</param>
/// <param name="Method">The HTTP method.</param>
/// <param name="Path">The concrete path, already bound to the fixture's item and media source.</param>
/// <param name="ItemBound">Whether the route names an item.</param>
/// <param name="MediaSourceBound">Whether the route names a media source.</param>
/// <param name="PlaySessionBound">Whether the route exposes a playSessionId query parameter.</param>
/// <param name="Evidence">What the positive case can prove.</param>
public sealed record MediaRoute(
    string Name,
    string MediaClass,
    PlaybackCapabilityScope Scope,
    string Method,
    string Path,
    bool ItemBound,
    bool MediaSourceBound,
    bool PlaySessionBound,
    MediaRouteEvidence Evidence,
    string? PlaySessionId = null)
{
    /// <summary>
    /// Appends a query parameter to this route's path, choosing the right separator.
    /// </summary>
    /// <param name="key">The parameter name.</param>
    /// <param name="value">The parameter value, appended already-escaped.</param>
    /// <returns>The path with the parameter appended.</returns>
    public string WithQuery(string key, string value)
        => Path + (Path.Contains('?', StringComparison.Ordinal) ? '&' : '?') + key + '=' + value;

    /// <inheritdoc />
    public override string ToString() => Name;
}
