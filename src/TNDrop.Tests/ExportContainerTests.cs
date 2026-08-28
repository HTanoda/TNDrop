using System;
using System.Text;
using TNDrop.Core;

public class ExportContainerTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("{\"hello\":\"世界\"}");

    [Fact]
    public void RoundTrip_ReturnsOriginalBytes()
    {
        var container = ExportContainer.Encrypt(Payload, "correct horse");
        var back = ExportContainer.Decrypt(container, "correct horse");
        Assert.Equal(Payload, back);
    }

    [Fact]
    public void Encrypt_ProducesDifferentBytesEachTime()
    {
        // salt/IV が毎回ランダムであることの確認 (固定だと同じ平文が同じ暗号文になる)
        var a = ExportContainer.Encrypt(Payload, "pw12345678");
        var b = ExportContainer.Encrypt(Payload, "pw12345678");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void WrongPassword_ThrowsPasswordException()
    {
        var container = ExportContainer.Encrypt(Payload, "correct horse");
        Assert.Throws<ExportPasswordException>(() => ExportContainer.Decrypt(container, "wrong horse"));
    }

    [Fact]
    public void TamperedByte_ThrowsPasswordException()
    {
        var container = ExportContainer.Encrypt(Payload, "correct horse");
        container[container.Length / 2] ^= 0xFF;
        Assert.Throws<ExportPasswordException>(() => ExportContainer.Decrypt(container, "correct horse"));
    }

    [Fact]
    public void WrongMagic_ThrowsFormatException()
    {
        var container = ExportContainer.Encrypt(Payload, "correct horse");
        container[0] = (byte)'X';
        Assert.Throws<ExportFormatException>(() => ExportContainer.Decrypt(container, "correct horse"));
    }

    [Fact]
    public void UnknownVersion_ThrowsFormatException()
    {
        var container = ExportContainer.Encrypt(Payload, "correct horse");
        container[4] = 0x63;
        Assert.Throws<ExportFormatException>(() => ExportContainer.Decrypt(container, "correct horse"));
    }

    [Fact]
    public void TooShortInput_ThrowsFormatException()
    {
        Assert.Throws<ExportFormatException>(() => ExportContainer.Decrypt(new byte[10], "correct horse"));
    }

    [Fact]
    public void MinPasswordLength_IsEight()
    {
        Assert.Equal(8, ExportContainer.MinPasswordLength);
    }

    [Fact]
    public void Encrypt_ShortPassword_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => ExportContainer.Encrypt(Payload, "1234567"));
    }

    [Fact]
    public void Decrypt_ShortWrongPassword_ThrowsPasswordException_NotArgumentException()
    {
        // Decrypt はあえて MinPasswordLength を検証しない (import 側は寛容に保つ)。
        // 短いパスワードでも ArgumentException にはならず、誤りパスワードと同じ
        // ExportPasswordException になることを確認する。
        var container = ExportContainer.Encrypt(Payload, "correct horse");
        Assert.Throws<ExportPasswordException>(() => ExportContainer.Decrypt(container, "short"));
    }

    [Fact]
    public void ContainerShorterThanOneAesBlock_ThrowsFormatException()
    {
        // HeaderSize(37) + MacSize(32) = 69B はヘッダー+MAC ちょうどで、ciphertext が
        // 0 バイトになり AES-CBC/PKCS7 として無効。70B でもまだ 1 AES ブロック (16B) に
        // 満たないため、too-short として弾かれるべき。
        Assert.Throws<ExportFormatException>(() => ExportContainer.Decrypt(new byte[70], "correct horse"));
    }
}
