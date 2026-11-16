/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Storage;
using System.Security.Cryptography;
using System.Text;

namespace WebCore.Accounts;

/// <summary>
/// Registered accounts, one document each. The document name is a SHA-256 hash of the normalized (trimmed, lowercased) email rather than the email itself: deterministic and safe on every file system
/// (an email has characters like '@' that are not), and it gives a direct lookup by email with no separate index. A user's <see cref="User.Id"/> (their identity everywhere else) is a random GUID,
/// decoupled from the email so that changing the email never changes who someone is.
/// The store's atomic create-if-absent is what stops two concurrent registrations for one email from both succeeding.
/// </summary>
public sealed class UserRepository(IDocumentStore store) : IUserRepository
{
    private const string AlreadyRegistered = "That email is already registered.";

    public Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default) =>
        store.GetAsync<User>(NameForEmail(email), cancellationToken);

    public async Task<User> CreateAsync(User user, CancellationToken cancellationToken = default)
    {
        if (!await store.TrySetIfNotExistsAsync(NameForEmail(user.Email), user, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(AlreadyRegistered);
        }

        return user;
    }

    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken = default)
    {
        var all = new List<User>();
        await foreach (var (_, user) in store.ListAsync<User>(cancellationToken).ConfigureAwait(false))
        {
            all.Add(user);
        }

        return all;
    }

    public async Task<User?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(id) ? null : (await ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(u => u.Id == id);

    /// <summary>Saves changes to an existing account; an account that does not exist is left alone (a save never registers anyone).</summary>
    public async Task SaveAsync(User user, CancellationToken cancellationToken = default)
    {
        var name = NameForEmail(user.Email);
        if (await store.GetAsync<User>(name, cancellationToken).ConfigureAwait(false) is not null)
        {
            await store.SetAsync(name, user, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ChangeEmailAsync(User user, string newEmail, CancellationToken cancellationToken = default)
    {
        var oldName = NameForEmail(user.Email);
        var newName = NameForEmail(newEmail);
        var previousEmail = user.Email;
        user.Email = newEmail.Trim().ToLowerInvariant();
        if (oldName == newName)
        {
            await store.SetAsync(newName, user, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!await store.TrySetIfNotExistsAsync(newName, user, cancellationToken).ConfigureAwait(false))
        {
            user.Email = previousEmail;
            throw new InvalidOperationException(AlreadyRegistered);
        }

        await store.DeleteAsync(oldName, cancellationToken).ConfigureAwait(false);
    }

    private static string NameForEmail(string email) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant()))) + ".json";
}
