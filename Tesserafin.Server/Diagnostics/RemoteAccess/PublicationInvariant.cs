using System;

namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// Publication invariant (R0-B).
/// </summary>
/// <remarks>
/// A managed proxy may be configured only when all of the following hold.
/// This is the single gate that R2-L / R2-D must call before any mutation.
/// </remarks>
public static class PublicationInvariant
{
    /// <summary>
    /// Returns true only when it is safe to publish.
    /// </summary>
    public static bool IsSatisfied(
        bool wizardCompleted,
        bool hasEnabledAdminWithPassword,
        bool knownProxiesNamesProxySource,
        bool externalClassificationIsRemote)
    {
        return wizardCompleted
            && hasEnabledAdminWithPassword
            && knownProxiesNamesProxySource
            && externalClassificationIsRemote;
    }

    /// <summary>
    /// Throws if the invariant is not satisfied.
    /// </summary>
    public static void Require(
        bool wizardCompleted,
        bool hasEnabledAdminWithPassword,
        bool knownProxiesNamesProxySource,
        bool externalClassificationIsRemote)
    {
        if (!IsSatisfied(wizardCompleted, hasEnabledAdminWithPassword, knownProxiesNamesProxySource, externalClassificationIsRemote))
        {
            throw new InvalidOperationException(
                "Publication invariant not satisfied: wizard complete, admin exists, KnownProxies correct, and external classification remote are all required.");
        }
    }
}
