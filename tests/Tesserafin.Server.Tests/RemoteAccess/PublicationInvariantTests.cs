using System;
using Tesserafin.Server.RemoteAccess;
using Xunit;

namespace Tesserafin.Server.Tests.RemoteAccess;

public class PublicationInvariantTests
{
    [Fact]
    public void IsSatisfied_AllTrue_ReturnsTrue()
    {
        var facts = new PublicationFacts(
            WizardCompleted: true,
            HasAdministratorCredential: true,
            KnownProxies: ["127.0.0.1"],
            IsClassifiedAsRemote: true);

        Assert.True(PublicationInvariant.IsSatisfied(facts));
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void IsSatisfied_AnyFalse_ReturnsFalse(
        bool wizard,
        bool admin,
        bool hasProxies,
        bool remote)
    {
        var facts = new PublicationFacts(
            wizard,
            admin,
            hasProxies ? ["127.0.0.1"] : null,
            remote);

        Assert.False(PublicationInvariant.IsSatisfied(facts));
    }

    [Fact]
    public void Require_NotSatisfied_Throws()
    {
        var facts = new PublicationFacts(false, true, ["127.0.0.1"], true);

        var ex = Assert.Throws<InvalidOperationException>(() => PublicationInvariant.Require(facts));
        Assert.Contains("Publication invariant", ex.Message);
    }

    [Fact]
    public void Require_Satisfied_DoesNotThrow()
    {
        var facts = new PublicationFacts(true, true, ["127.0.0.1"], true);

        PublicationInvariant.Require(facts);
    }
}
