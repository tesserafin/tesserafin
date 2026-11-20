namespace Tesserafin.Controller.Library
{
    /// <summary>
    /// Whether a video is an active recording, which its source type reads, on top of
    /// <see cref="IStandaloneVisibilityService"/>.
    /// </summary>
    /// <remarks>
    /// The visibility check uses this service to distinguish an in-progress recording from a video's
    /// catalogue source. Implementations must not depend on <see cref="ILibraryManager"/>, directly or deferred.
    /// </remarks>
    public interface IRecordingVisibilityService : IStandaloneVisibilityService
    {
        /// <summary>
        /// Gets a value indicating whether a recording is in progress at a path.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <returns><c>true</c> if a recording is in progress at that path.</returns>
        bool IsActiveRecording(string path);
    }
}
