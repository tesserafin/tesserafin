namespace Tesserafin.Api.Helpers;

/// <summary>
/// What <see cref="PlaybackSessionDeliveryGuard"/> decided.
/// </summary>
public enum PlaybackSessionDeliveryOutcome
{
    /// <summary>The caller is served.</summary>
    Granted,

    /// <summary>Nothing servable under these names: answered as a missing resource.</summary>
    NotFound,

    /// <summary>The session exists and is not the caller's.</summary>
    Forbidden,
}
