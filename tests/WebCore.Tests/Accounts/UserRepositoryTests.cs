/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Storage;
using WebCore.Accounts;


namespace WebCore.Tests.Accounts;

public class UserRepositoryTests : IDisposable
{
    private readonly string _tempFolder;
    private readonly UserRepository _repository;

    public UserRepositoryTests()
    {
        _tempFolder = Path.Combine(Path.GetTempPath(), "CrateDiggerUserTests_" + Guid.NewGuid().ToString("N"));
        _repository = new UserRepository(new FileDocumentStore(_tempFolder));
    }

    private static User MakeUser(string email = "alice@example.com") => new()
    {
        Email = email,
        DisplayName = "Alice",
        PasswordHash = PasswordHasher.Hash("hunter22"),
    };

    [Fact]
    public void Constructor_CreatesFolderIfMissing()
    {
        Assert.True(Directory.Exists(_tempFolder));
    }

    [Fact]
    public async Task CreateAndGetByEmail_RoundTripsAllFields()
    {
        var user = MakeUser();
        await _repository.CreateAsync(user);

        var loaded = await _repository.GetByEmailAsync("alice@example.com");

        Assert.NotNull(loaded);
        Assert.Equal(user.Id, loaded!.Id);
        Assert.Equal("Alice", loaded.DisplayName);
        Assert.True(PasswordHasher.Verify("hunter22", loaded.PasswordHash));
    }

    [Fact]
    public async Task GetByEmailAsync_IsCaseInsensitiveAndTrims()
    {
        await _repository.CreateAsync(MakeUser("Alice@Example.com"));

        var loaded = await _repository.GetByEmailAsync("  alice@example.com  ");

        Assert.NotNull(loaded);
    }

    [Fact]
    public async Task GetByEmailAsync_UnknownEmail_ReturnsNull()
    {
        var result = await _repository.GetByEmailAsync("nobody@example.com");
        Assert.Null(result);
    }

    [Fact]
    public async Task CreateAsync_DuplicateEmail_Throws()
    {
        await _repository.CreateAsync(MakeUser("alice@example.com"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.CreateAsync(MakeUser("alice@example.com")));
    }

    [Fact]
    public async Task CreateAsync_DuplicateEmailDifferentCase_Throws()
    {
        await _repository.CreateAsync(MakeUser("alice@example.com"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.CreateAsync(MakeUser("ALICE@EXAMPLE.COM")));
    }

    [Fact]
    public async Task CreateAsync_DifferentEmails_BothSucceed()
    {
        await _repository.CreateAsync(MakeUser("alice@example.com"));
        await _repository.CreateAsync(MakeUser("bob@example.com"));

        Assert.NotNull(await _repository.GetByEmailAsync("alice@example.com"));
        Assert.NotNull(await _repository.GetByEmailAsync("bob@example.com"));
    }

    [Fact]
    public async Task GetByEmailAsync_CorruptFile_ReturnsNullInsteadOfThrowing()
    {
        // Simulate a corrupt file at the exact path a real registration would use.
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("corrupt@example.com")));
        await File.WriteAllTextAsync(Path.Combine(_tempFolder, hash + ".json"), "{ not valid json");

        var result = await _repository.GetByEmailAsync("corrupt@example.com");

        Assert.Null(result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempFolder))
        {
            Directory.Delete(_tempFolder, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetByIdAndList_FindAccountsWithoutTheirEmail()
    {
        var a = await _repository.CreateAsync(MakeUser("a@example.com"));
        await _repository.CreateAsync(MakeUser("b@example.com"));

        Assert.Equal("a@example.com", (await _repository.GetByIdAsync(a.Id))!.Email);
        Assert.Null(await _repository.GetByIdAsync("no-such-id"));
        Assert.Equal(2, (await _repository.ListAsync()).Count);
    }

    [Fact]
    public async Task Save_UpdatesAnExistingAccount_ButNeverCreatesOne()
    {
        var user = await _repository.CreateAsync(MakeUser());
        var when = DateTimeOffset.UtcNow;

        user.EmailVerifiedAt = when;
        await _repository.SaveAsync(user);
        await _repository.SaveAsync(MakeUser("ghost@example.com"));

        Assert.Equal(when.ToUnixTimeSeconds(), (await _repository.GetByEmailAsync("alice@example.com"))!.EmailVerifiedAt!.Value.ToUnixTimeSeconds());
        Assert.Null(await _repository.GetByEmailAsync("ghost@example.com"));
    }

    [Fact]
    public async Task ChangeEmail_MovesTheAccount_AndKeepsItsIdAndPassword()
    {
        var user = await _repository.CreateAsync(MakeUser("old@example.com"));

        await _repository.ChangeEmailAsync(user, " New@Example.com ");

        Assert.Null(await _repository.GetByEmailAsync("old@example.com"));
        var moved = (await _repository.GetByEmailAsync("new@example.com"))!;
        Assert.Equal(user.Id, moved.Id);
        Assert.Equal("new@example.com", moved.Email);
        Assert.True(PasswordHasher.Verify("hunter22", moved.PasswordHash));
        Assert.Single(await _repository.ListAsync());
    }

    [Fact]
    public async Task ChangeEmail_ToAnAddressAnotherAccountHas_Throws_AndChangesNothing()
    {
        var a = await _repository.CreateAsync(MakeUser("a@example.com"));
        await _repository.CreateAsync(MakeUser("b@example.com"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.ChangeEmailAsync(a, "b@example.com"));

        Assert.Equal("a@example.com", a.Email);
        Assert.NotNull(await _repository.GetByEmailAsync("a@example.com"));
        Assert.Equal(2, (await _repository.ListAsync()).Count);
    }
}
