using System;

namespace Tesserafin.Server.RemoteAccess;

/// <summary>
/// Hard gate that must pass before any public publication is allowed (R0-B).
/// </summary>
/// <remarks>
/// Combines wizard completion, administrator credential presence, KnownProxies
/// narrowing, and remote classification. Callers supply the observed facts;
/// this type only evaluates the invariant.
/// </remarks>
public static class PublicationInvariant
{
    /// <summary>
    /// Returns true only when every publication prerequisite is satisfied.
    /// </summary>
    public static bool IsSatisfied(PublicationFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (!facts.WizardCompleted) return false;
        if (!facts.HasAdministratorCredential) return false;
        if (facts.KnownProxies is null || facts.KnownProxies.Length == 0) return false;
        if (!facts.IsClassifiedAsRemote) return false;

        return true;
    }

    /// <summary>
    /// Throws when the invariant is not satisfied. Use immediately before ApplyAsync.
    /// </summary>
    public static void Require(PublicationFacts facts)
    {
        if (!IsSatisfied(facts))
        {
            throw new InvalidOperationException(
                "Publication invariant not satisfied: wizard, administrator credential, KnownProxies and remote classification are all required.");
        }
    }
}

/// <summary>
/// Observed facts needed to evaluate the publication invariant.
/// </summary>
public sealed record PublicationFacts(
    bool WizardCompleted,
    bool HasAdministratorCredential,
    string[]? KnownProxies,
    bool IsClassifiedAsRemote);
