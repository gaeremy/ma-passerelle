// SPDX-License-Identifier: GPL-3.0-or-later
// Ajout de Ma Passerelle (voir UPSTREAM.md) : le repli BouncyCastle utilisé sous Windows 10.
using System.Security.Cryptography;
using Xunit;

namespace WinPlay.Core.Tests;

public class ChaCha20Poly1305FallbackTests
{
    // RFC 8439, section 2.8.2.
    private static readonly byte[] Key = Convert.FromHexString("808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f");
    private static readonly byte[] Nonce = Convert.FromHexString("070000004041424344454647");
    private static readonly byte[] Aad = Convert.FromHexString("50515253c0c1c2c3c4c5c6c7");
    private static readonly byte[] Plain = "Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it."u8.ToArray();
    private static readonly byte[] Tag = Convert.FromHexString("1ae10b594f09e26a7e902ecbd0600691");
    private static readonly byte[] CipherStart = Convert.FromHexString("d31a8d34648e60db7b86afbc53ef7ec2");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MatchesRfc8439Vector(bool managed)
    {
        using var c = new ChaCha20Poly1305(Key, managed);
        byte[] cipher = new byte[Plain.Length], tag = new byte[16];
        c.Encrypt(Nonce, Plain, cipher, tag, Aad);
        Assert.Equal(CipherStart, cipher[..16]);
        Assert.Equal(Tag, tag);

        byte[] back = new byte[Plain.Length];
        c.Decrypt(Nonce, cipher, tag, back, Aad);
        Assert.Equal(Plain, back);
    }

    [Fact]
    public void ManagedAndNativeAgreeWithoutAad()
    {
        byte[] data = new byte[5000];
        new Random(7).NextBytes(data);
        using var managed = new ChaCha20Poly1305(Key, forceManaged: true);
        using var native = new ChaCha20Poly1305(Key);
        byte[] c1 = new byte[data.Length], t1 = new byte[16], c2 = new byte[data.Length], t2 = new byte[16];
        managed.Encrypt(Nonce, data, c1, t1);
        native.Encrypt(Nonce, data, c2, t2);
        Assert.Equal(c2, c1);
        Assert.Equal(t2, t1);
    }

    [Fact]
    public void ManagedRejectsTamperedTag()
    {
        using var c = new ChaCha20Poly1305(Key, forceManaged: true);
        byte[] cipher = new byte[Plain.Length], tag = new byte[16];
        c.Encrypt(Nonce, Plain, cipher, tag, Aad);
        tag[0] ^= 1;
        Assert.Throws<AuthenticationTagMismatchException>(() => c.Decrypt(Nonce, cipher, tag, new byte[Plain.Length], Aad));
    }
}
