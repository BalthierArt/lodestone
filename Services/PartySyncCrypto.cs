using System.Security.Cryptography;
using System.Text;

namespace Lodestone.Services;

public static class PartySyncCrypto
{
    private const string Prefix = "ps1";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static string PartyHash(string partyKey)
        => Sha256Hex(partyKey.Trim());

    public static string EncryptDisplayValue(string value, string partyKey, string context)
    {
        var plaintext = Encoding.UTF8.GetBytes(value ?? string.Empty);
        Span<byte> nonce = stackalloc byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(DeriveKey(partyKey), TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(context));

        return string.Join('.', Prefix, Base64Url(nonce), Base64Url(ciphertext), Base64Url(tag));
    }

    public static string DecryptDisplayValue(string? value, string partyKey, string context)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var parts = value.Split('.');
        if (parts.Length != 4 || parts[0] != Prefix)
            return value.Trim();

        try
        {
            var nonce = FromBase64Url(parts[1]);
            var ciphertext = FromBase64Url(parts[2]);
            var tag = FromBase64Url(parts[3]);
            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(DeriveKey(partyKey), TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData(context));
            return Encoding.UTF8.GetString(plaintext);
        }
        catch
        {
            return "[encrypted]";
        }
    }

    private static byte[] DeriveKey(string partyKey)
        => SHA256.HashData(Encoding.UTF8.GetBytes($"Lodestone.PartySync.Display.v1:{partyKey.Trim()}"));

    private static byte[] AssociatedData(string context)
        => Encoding.UTF8.GetBytes($"Lodestone.PartySync.Display:{context}");

    private static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        return Convert.FromBase64String(padded);
    }
}
