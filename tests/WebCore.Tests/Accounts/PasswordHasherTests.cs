/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Accounts;

namespace WebCore.Tests.Accounts;

public class PasswordHasherTests
{
    [Fact]
    public void Verify_CorrectPassword_ReturnsTrue()
    {
        var hash = PasswordHasher.Hash("correct horse battery staple");

        Assert.True(PasswordHasher.Verify("correct horse battery staple", hash));
    }

    [Fact]
    public void Verify_WrongPassword_ReturnsFalse()
    {
        var hash = PasswordHasher.Hash("correct horse battery staple");

        Assert.False(PasswordHasher.Verify("wrong password", hash));
    }

    [Fact]
    public void Hash_SamePasswordTwice_ProducesDifferentHashes()
    {
        var hash1 = PasswordHasher.Hash("same password");
        var hash2 = PasswordHasher.Hash("same password");

        Assert.NotEqual(hash1, hash2); // different random salt each time
        Assert.True(PasswordHasher.Verify("same password", hash1));
        Assert.True(PasswordHasher.Verify("same password", hash2));
    }

    [Theory]
    [InlineData("not-a-valid-hash")]
    [InlineData("")]
    [InlineData("abc.def")]
    [InlineData("notanumber.c2FsdA==.a2V5")]
    [InlineData("100000.not-valid-base64!!.a2V5")]
    public void Verify_MalformedHash_ReturnsFalseInsteadOfThrowing(string malformedHash)
    {
        Assert.False(PasswordHasher.Verify("anything", malformedHash));
    }

    [Fact]
    public void Hash_ProducesThreeDotSeparatedParts()
    {
        var hash = PasswordHasher.Hash("password123");

        Assert.Equal(3, hash.Split('.').Length);
    }
}
