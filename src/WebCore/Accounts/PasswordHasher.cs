/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Security.Cryptography;

namespace WebCore.Accounts;

/// <summary>
/// PBKDF2-HMACSHA256 password hashing with a random salt per password and the iteration count
/// embedded in the stored hash string (so it can be raised later without invalidating existing
/// hashes). No external dependency - built entirely on <see cref="Rfc2898DeriveBytes"/>.
/// </summary>
public static class PasswordHasher
{
    private const int SaltSizeBytes = 16;
    private const int KeySizeBytes = 32;
    // OWASP's current figure for PBKDF2-HMAC-SHA256 (2026-11-03, up from 100 000); older hashes still verify and are upgraded at the next sign-in (see NeedsRehash).
    private const int Iterations = 600_000;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    /// <summary>Produces a self-contained hash string: "{iterations}.{saltBase64}.{keyBase64}".</summary>
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algorithm, KeySizeBytes);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    /// <summary>True when the stored hash was made with fewer iterations than today's, so a successful sign-in should save a fresh one.</summary>
    public static bool NeedsRehash(string hash) =>
        !int.TryParse(hash.Split('.', 2)[0], out var iterations) || iterations < Iterations;

    // A hash made once, so a sign-in for an address with no account (or no password) can spend the same time as one for a real account.
    private static readonly Lazy<string> Decoy = new(() => Hash("decoy password that no account has"));

    /// <summary>Does the work of a failed <see cref="Verify"/> and answers nothing: run when there is no real hash to check, so "no such account" takes as long as "wrong password" and the sign-in page cannot be used to learn which addresses are registered.</summary>
    public static void SpendVerifyTime(string password) => _ = Verify(password, Decoy.Value);

    public static bool Verify(string password, string hash)
    {
        var parts = hash.Split('.', 3);
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations))
        {
            return false;
        }

        byte[] salt, expectedKey;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expectedKey = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actualKey = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, Algorithm, expectedKey.Length);
        return CryptographicOperations.FixedTimeEquals(actualKey, expectedKey);
    }
}
