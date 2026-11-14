namespace Tesserafin.Playback.Decision;

/// <summary>
/// How a user wants subtitles auto-selected when no explicit stream index is requested. Mirrors
/// the legacy <c>Tesserafin.Data.Enums.SubtitlePlaybackMode</c> (same member names
/// and numeric values) without depending on it: the domain assembly references nothing outside
/// itself (RFC PR91 §4/§7), so this is a domain-local copy of the vocabulary, not a reuse of the
/// legacy type.
/// </summary>
/// <remarks>
/// PR103: consumed by <c>PlaybackEngine</c>'s subtitle auto-selection, which reproduces
/// <c>Tesserafin.Server.Core.Library.MediaStreamSelector.GetDefaultSubtitleStreamIndex</c>
/// (MediaStreamSelector.cs:31-87) mode-by-mode - see the engine's <c>SelectDefaultSubtitle</c> for
/// the per-mode semantics each member drives.
/// </remarks>
public enum SubtitlePlaybackMode
{
    /// <summary>
    /// Load subtitles according to the external/default/forced flags on the stream itself.
    /// </summary>
    Default = 0,

    /// <summary>
    /// Always load a full (non-forced) subtitle of a preferred language if one exists, otherwise
    /// behave like <see cref="OnlyForced"/>.
    /// </summary>
    Always = 1,

    /// <summary>
    /// Only load subtitles flagged forced, in a preferred language or with an undefined language.
    /// </summary>
    OnlyForced = 2,

    /// <summary>
    /// Never auto-select a subtitle.
    /// </summary>
    None = 3,

    /// <summary>
    /// Only load subtitles when the selected audio track's language is not already one of the
    /// preferred subtitle languages; when it is, behave like <see cref="OnlyForced"/>.
    /// </summary>
    Smart = 4,
}
