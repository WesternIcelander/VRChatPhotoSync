using System.Security.Cryptography;
using System.Text;

namespace VRChatPhotoSync;

/// <summary>
/// Encrypts the Immich API key with Windows DPAPI (<see cref="ProtectedData"/>,
/// <see cref="DataProtectionScope.CurrentUser"/>) for storage in <c>config.json</c> as
/// base64. The encrypted value can only be decrypted by the same Windows user on the same
/// machine, so a copied config file is useless to anyone else.
/// </summary>
public static class SecretProtector
{
    /// <summary>
    /// Extra entropy so other apps running as the same user can't decrypt the blob just by
    /// calling DPAPI on it. Not a secret; it only ties the blob to this app.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VRChatPhotoSync.ApiKey.v1");

    /// <summary>
    /// Encrypts <paramref name="secret"/> and returns it base64-encoded. Throws
    /// <see cref="ArgumentException"/> for a null or empty secret.
    /// </summary>
    public static string Protect(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        var plain = Encoding.UTF8.GetBytes(secret);
        try
        {
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>
    /// Decrypts a value made by <see cref="Protect"/>. Returns false (and a null
    /// <paramref name="secret"/>) when the value is null or empty, isn't valid base64, or
    /// can't be decrypted (for example, the config was copied from another user or machine).
    /// Never throws.
    /// </summary>
    public static bool TryUnprotect(string? protectedValue, out string? secret)
    {
        secret = null;
        if (string.IsNullOrWhiteSpace(protectedValue))
            return false;

        byte[] encrypted;
        try
        {
            encrypted = Convert.FromBase64String(protectedValue.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        byte[]? plain = null;
        try
        {
            plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            secret = Encoding.UTF8.GetString(plain);
            return secret.Length > 0;
        }
        catch (CryptographicException)
        {
            secret = null;
            return false;
        }
        finally
        {
            if (plain is not null)
                CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Decrypts a value made by <see cref="Protect"/>, or returns null if it can't (see <see cref="TryUnprotect"/>).</summary>
    public static string? Unprotect(string? protectedValue) =>
        TryUnprotect(protectedValue, out var secret) ? secret : null;
}
