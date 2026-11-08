using System;
using System.Text.RegularExpressions;

namespace Tesserafin.Server.RemoteAccess;

/// <summary>
/// Publication-time credential strength gate (D-5).
/// </summary>
/// <remarks>
/// Lightweight estimator. Not a full zxcvbn port; good enough for the MVP gate.
/// Threshold is deliberately conservative (score ≥ 3 on a 0-4 scale).
/// </remarks>
public static class CredentialStrengthGate
{
    public static bool MeetsPublicationStrength(string? password)
    {
        if (string.IsNullOrWhiteSpace(password)) return false;
        return EstimateScore(password) >= 3;
    }

    public static int EstimateScore(string password)
    {
        if (string.IsNullOrEmpty(password)) return 0;

        var score = 0;
        if (password.Length >= 12) score++;
        if (password.Length >= 16) score++;
        if (Regex.IsMatch(password, "[a-z]")) score++;
        if (Regex.IsMatch(password, "[A-Z]")) score++;
        if (Regex.IsMatch(password, "[0-9]")) score++;
        if (Regex.IsMatch(password, "[^a-zA-Z0-9]")) score++;

        if (Regex.IsMatch(password, @"(.)\1{2,}")) score--;
        if (Regex.IsMatch(password, @"(012|123|234|345|456|567|678|789|890)")) score--;
        if (Regex.IsMatch(password, @"(?i)password|admin|tesserafin|qwerty")) score -= 2;

        return Math.Clamp(score, 0, 4);
    }

    public static void RequireForPublication(string? password)
    {
        if (!MeetsPublicationStrength(password))
        {
            throw new InvalidOperationException(
                "Administrator password does not meet the minimum strength required before public publication.");
        }
    }
}
