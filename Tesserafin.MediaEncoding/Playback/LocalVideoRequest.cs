using Tesserafin.Playback.Decision;

namespace Tesserafin.MediaEncoding.Playback;

/// <summary>
/// What a client declared on <c>POST</c>/<c>PUT Playback/Sessions</c> for a local film or episode, handed to
/// <see cref="LocalVideoPlanner"/> as the engine's own inputs.
/// </summary>
/// <param name="Capabilities">The declared client capabilities.</param>
/// <param name="Constraints">The declared playback constraints.</param>
public sealed record LocalVideoRequest(ClientCapabilities Capabilities, PlaybackConstraints Constraints);
