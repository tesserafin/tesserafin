using System.Collections.Immutable;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// A serialized argument fragment and its process-local environment requirements. The text is
/// deliberately not represented as an argv vector: builders still emit quoted command lines.
/// </summary>
/// <param name="ArgumentLine">Serialized argument text.</param>
/// <param name="EnvironmentVariables">Immutable child-process overrides.</param>
public sealed record FfmpegArguments(string ArgumentLine, ImmutableDictionary<string, string> EnvironmentVariables)
{
    /// <summary>Gets an empty fragment with no environment requirements.</summary>
    public static FfmpegArguments Empty { get; } = new(string.Empty, ImmutableDictionary<string, string>.Empty);
}
