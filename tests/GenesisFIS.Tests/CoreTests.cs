using GenesisFIS.Core.Security;

namespace GenesisFIS.Tests;

public class LegacyPasswordCipherTests
{
    [Fact]
    public void Encrypt_matches_vb6_crypt_output() =>
        Assert.Equal("UVR^ZXYQ", LegacyPasswordCipher.Encrypt("TRINATH1"));

    [Fact]
    public void Verify_is_case_insensitive_on_stored_value() =>
        Assert.True(LegacyPasswordCipher.Verify("TRINATH1", "uvr^zxyq"));

    [Theory]
    [InlineData("")]
    [InlineData("wrong")]
    public void Verify_rejects_bad_passwords(string pwd) =>
        Assert.False(LegacyPasswordCipher.Verify(pwd, "UVR^ZXYQ"));

    [Fact]
    public void Verify_rejects_missing_hash() =>
        Assert.False(LegacyPasswordCipher.Verify("x", null));

    [Fact]
    public void Encrypt_handles_high_ascii_wraparound()
    {
        var result = LegacyPasswordCipher.Encrypt("Welcome1");
        Assert.Equal(8, result.Length);
    }
}

public class AccessStringTests
{
    [Theory]
    [InlineData("101", 1, true)]
    [InlineData("101", 2, false)]
    [InlineData("101", 3, true)]
    [InlineData("101", 4, false)]
    [InlineData("101", 0, false)]
    public void IsAllowed_reads_one_based_flags(string value, int pos, bool expected) =>
        Assert.Equal(expected, new AccessString(value).IsAllowed(pos));
}
