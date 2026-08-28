using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TNDrop.Core;

/// <summary>.tndexport のフォーマットエラー (TNDrop のエクスポートファイルではない)。</summary>
public sealed class ExportFormatException : Exception
{
    public ExportFormatException(string message) : base(message) { }
}

/// <summary>HMAC 不一致 = パスワード誤りまたは改ざん。どちらかは区別できない (設計書 §6.2)。</summary>
public sealed class ExportPasswordException : Exception
{
    public ExportPasswordException(string message) : base(message) { }
}

/// <summary>
/// 別端末移行ファイル (.tndexport) の暗号コンテナ (設計書 §6.3, format 1)。
/// レイアウト: magic "TNDX" (4B) | version (1B=0x01) | salt (16B) | IV (16B)
/// | ciphertext (AES-256-CBC, PKCS7) | HMAC-SHA256 (32B, 先頭から ciphertext 末尾まで)。
/// 鍵導出: PBKDF2-SHA256 600,000 回で 64B → 前半 32B が AES 鍵、後半 32B が HMAC 鍵。
/// encrypt-then-MAC: 復号よりも先に HMAC を検証するので、細工された暗号文が
/// AES/PKCS7 のエラー経路に到達しない。
/// </summary>
public static class ExportContainer
{
    public const int MinPasswordLength = 8;
    public const byte FormatVersion = 0x01;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("TNDX");
    private const int SaltSize = 16;
    private const int IvSize = 16;
    private const int MacSize = 32;
    private const int Pbkdf2Iterations = 600_000;
    private const int HeaderSize = 4 + 1 + SaltSize + IvSize;

    public static byte[] Encrypt(byte[] plaintext, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var iv = RandomNumberGenerator.GetBytes(IvSize);
        var (aesKey, macKey) = DeriveKeys(password, salt);

        using var aes = Aes.Create();
        aes.Key = aesKey;
        var ciphertext = aes.EncryptCbc(plaintext, iv, PaddingMode.PKCS7);

        using var ms = new MemoryStream();
        ms.Write(Magic);
        ms.WriteByte(FormatVersion);
        ms.Write(salt);
        ms.Write(iv);
        ms.Write(ciphertext);

        using var hmac = new HMACSHA256(macKey);
        var mac = hmac.ComputeHash(ms.ToArray());
        ms.Write(mac);
        return ms.ToArray();
    }

    public static byte[] Decrypt(byte[] container, string password)
    {
        if (container.Length < HeaderSize + MacSize)
            throw new ExportFormatException("container too short");
        if (!container.AsSpan(0, 4).SequenceEqual(Magic))
            throw new ExportFormatException("bad magic");
        if (container[4] != FormatVersion)
            throw new ExportFormatException($"unknown format version {container[4]}");

        var salt = container.AsSpan(5, SaltSize).ToArray();
        var iv = container.AsSpan(5 + SaltSize, IvSize).ToArray();
        var (aesKey, macKey) = DeriveKeys(password, salt);

        var macOffset = container.Length - MacSize;
        using var hmac = new HMACSHA256(macKey);
        var expected = hmac.ComputeHash(container, 0, macOffset);
        if (!CryptographicOperations.FixedTimeEquals(expected, container.AsSpan(macOffset)))
            throw new ExportPasswordException("HMAC mismatch");

        var ciphertext = container.AsSpan(HeaderSize, macOffset - HeaderSize).ToArray();
        using var aes = Aes.Create();
        aes.Key = aesKey;
        return aes.DecryptCbc(ciphertext, iv, PaddingMode.PKCS7);
    }

    private static (byte[] AesKey, byte[] MacKey) DeriveKeys(string password, byte[] salt)
    {
        var derived = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 64);
        return (derived.AsSpan(0, 32).ToArray(), derived.AsSpan(32, 32).ToArray());
    }
}
