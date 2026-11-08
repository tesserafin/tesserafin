using Tesserafin.Server.RemoteAccess;
using Xunit;

namespace Tesserafin.Server.Tests.RemoteAccess;

public class CredentialStrengthGateTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("short", false)]
    [InlineData("password123", false)]
    [InlineData("CorrectHorseBatteryStaple!42", true)]
    public void MeetsPublicationStrength_ReturnsExpected(string? password, bool expected)
    {
        Assert.Equal(expected, CredentialStrengthGate.MeetsPublicationStrength(password));
    }

    [Fact]
    public void RequireForPublication_Weak_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CredentialStrengthGate.RequireForPublication("weak"));

        Assert.Contains("minimum strength", ex.Message);
    }

    [Fact]
    public void RequireForPublication_Strong_DoesNotThrow()
    {
        CredentialStrengthGate.RequireForPublication("CorrectHorseBatteryStaple!42");
    }

    [Theory]
    [InlineData("a", 1)]
    [InlineData("abcdefghijkl", 2)]
    [InlineData("abcdefghijklmnop", 3)]
    public void EstimateScore_LengthContributes(string password, int minExpected)
    {
        Assert.True(CredentialStrengthGate.EstimateScore(password) >= minExpected);
    }
}
