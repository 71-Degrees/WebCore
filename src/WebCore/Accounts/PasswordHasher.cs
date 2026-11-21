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
    // A stored count above the maximum would hang sign-in on one request; below the minimum PBKDF2 throws, and this code never wrote one.
    private const int MinIterations = 1;
    private const int MaxIterations = 10_000_000;
    // Fewer salt bytes than this is too little randomness for a per-password salt.
    private const int MinSaltBytes = 8;
    // Older hashes copied in from the apps may differ in key size, but a key under 16 bytes is never one this code wrote.
    // The upper bound stops a stored hash from making Verify allocate or compare an unbounded key.
    private const int MinKeyBytes = 16;
    private const int MaxKeyBytes = 64;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    /// <summary>Produces a self-contained hash string: "{iterations}.{saltBase64}.{keyBase64}".</summary>
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algorithm, KeySizeBytes);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    /// <summary>True when the stored hash was made with fewer iterations than today's, so a successful sign-in should save a fresh one. A missing hash counts as needing one.</summary>
    public static bool NeedsRehash(string hash) =>
        string.IsNullOrEmpty(hash) || !int.TryParse(hash.Split('.', 2)[0], out var iterations) || iterations < Iterations;

    // A hash made once, so a sign-in for an address with no account (or no password) can spend the same time as one for a real account.
    private static readonly Lazy<string> Decoy = new(() => Hash("decoy password that no account has"));

    /// <summary>Does the work of a failed <see cref="Verify"/> and answers nothing: run when there is no real hash to check, so "no such account" takes as long as "wrong password" and the sign-in page cannot be used to learn which addresses are registered.</summary>
    public static void SpendVerifyTime(string password) => _ = Verify(password, Decoy.Value);

    public static bool Verify(string password, string hash)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (string.IsNullOrEmpty(hash) || !TryParse(hash, out var iterations, out var salt, out var expectedKey))
        {
            return false;
        }

        var actualKey = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, Algorithm, expectedKey.Length);
        return CryptographicOperations.FixedTimeEquals(actualKey, expectedKey);
    }

    // Refuses anything this code could not have written before any PBKDF2 work is done. An empty key would
    // otherwise compare equal to the empty PBKDF2 output and verify every password.
    private static bool TryParse(string hash, out int iterations, out byte[] salt, out byte[] key)
    {
        iterations = 0;
        salt = [];
        key = [];

        var parts = hash.Split('.', 3);
        if (parts.Length != 3 || !int.TryParse(parts[0], out iterations))
        {
            return false;
        }

        if (iterations is < MinIterations or > MaxIterations)
        {
            return false;
        }

        try
        {
            salt = Convert.FromBase64String(parts[1]);
            key = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length >= MinSaltBytes && key.Length is >= MinKeyBytes and <= MaxKeyBytes;
    }
}
