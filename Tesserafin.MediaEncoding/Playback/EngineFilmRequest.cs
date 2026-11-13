using Tesserafin.Playback.Decision;

namespace Tesserafin.MediaEncoding.Playback;

/// <summary>
/// What a client declared on <c>POST</c>/<c>PUT Playback/Sessions</c> for a film, handed to
/// <see cref="EngineFilmPlanner"/> as the engine's own inputs.
/// </summary>
/// <param name="Capabilities">The declared client capabilities.</param>
/// <param name="Constraints">The declared playback constraints.</param>
public sealed record EngineFilmRequest(ClientCapabilities Capabilities, PlaybackConstraints Constraints);
