using System.Security.Cryptography;

namespace VRChatPhotoSync.Tests;

public sealed class SecretProtectorTests
{
    private const string FakeKey = "test-key-0123456789abcdefABCDEF";

    [Fact]
    public void RoundTrip_GivesOriginal()
    {
        var protectedValue = SecretProtector.Protect(FakeKey);
        Assert.True(SecretProtector.TryUnprotect(protectedValue, out var secret));
        Assert.Equal(FakeKey, secret);
        Assert.Equal(FakeKey, SecretProtector.Unprotect(protectedValue));
    }

    [Fact]
    public void Protected_IsBase64_AndNotPlainText()
    {
        var protectedValue = SecretProtector.Protect(FakeKey);
        Assert.DoesNotContain(FakeKey, protectedValue);
        var bytes = Convert.FromBase64String(protectedValue); // throws if not base64
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public void Protect_IsRandomised()
    {
        Assert.NotEqual(SecretProtector.Protect(FakeKey), SecretProtector.Protect(FakeKey));
    }

    [Fact]
    public void NonAsciiSecret_RoundTrips()
    {
        const string key = "kéy-ü-日本";
        Assert.Equal(key, SecretProtector.Unprotect(SecretProtector.Protect(key)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Protect_RejectsEmpty(string? value)
    {
        Assert.ThrowsAny<ArgumentException>(() => SecretProtector.Protect(value!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64!!")]
    [InlineData("AAAAAAAAAAAAAAAA")] // valid base64, not a DPAPI blob
    public void Unprotect_InvalidInput_ReturnsFalse(string? value)
    {
        Assert.False(SecretProtector.TryUnprotect(value, out var secret));
        Assert.Null(secret);
        Assert.Null(SecretProtector.Unprotect(value));
    }

    [Fact]
    public void BlobWithoutAppEntropy_IsRejected()
    {
        // Encrypted by DPAPI for this user, but not by SecretProtector (no app entropy).
        var foreign = ProtectedData.Protect("x"u8.ToArray(), null, DataProtectionScope.CurrentUser);
        Assert.Null(SecretProtector.Unprotect(Convert.ToBase64String(foreign)));
    }

    [Fact]
    public void TamperedBlob_IsRejected()
    {
        var bytes = Convert.FromBase64String(SecretProtector.Protect(FakeKey));
        bytes[^1] ^= 0xFF;
        Assert.Null(SecretProtector.Unprotect(Convert.ToBase64String(bytes)));
    }

    [Fact]
    public void ConfigStore_RoundTrip_KeepsProtectedKey()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PhotoSyncTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new ConfigStore(dir, Path.Combine(dir, "appdata"));
            store.Save(new AppConfig { ApiKeyProtected = SecretProtector.Protect(FakeKey) });

            Assert.DoesNotContain(FakeKey, File.ReadAllText(store.ConfigPath));
            Assert.Equal(FakeKey, SecretProtector.Unprotect(store.Load().Config.ApiKeyProtected));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
