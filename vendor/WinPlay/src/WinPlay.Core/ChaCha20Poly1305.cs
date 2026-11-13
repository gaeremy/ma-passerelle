// Ajout de Ma Passerelle (voir UPSTREAM.md) : Windows 10 n'offre pas ChaCha20-Poly1305 dans CNG,
// et System.Security.Cryptography.ChaCha20Poly1305 y lève PlatformNotSupportedException.
// Ce type, placé dans l'espace de noms WinPlay.Core, a priorité sur celui de System.Security.Cryptography
// dans tout le code de WinPlay.Core : les fichiers repris n'ont pas besoin d'être modifiés.
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using SysChaCha = System.Security.Cryptography.ChaCha20Poly1305;
using BcChaCha = Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305;

namespace WinPlay.Core;

/// <summary>
/// ChaCha20-Poly1305 (RFC 8439), même interface que System.Security.Cryptography.ChaCha20Poly1305.
/// Utilise l'implémentation de Windows quand elle existe (Windows 11), sinon BouncyCastle (Windows 10).
/// </summary>
public sealed class ChaCha20Poly1305 : IDisposable
{
    private const int TagSize = 16;

    /// <summary>Vrai si Windows fournit l'algorithme ; sinon le repli BouncyCastle est utilisé.</summary>
    public static bool IsNative => SysChaCha.IsSupported;

    private readonly SysChaCha? _native;
    private readonly byte[]? _key;

    public ChaCha20Poly1305(byte[] key) : this((ReadOnlySpan<byte>)key) { }

    public ChaCha20Poly1305(ReadOnlySpan<byte> key) : this(key, forceManaged: false) { }

    /// <summary>Pour les tests : <paramref name="forceManaged"/> impose le repli BouncyCastle.</summary>
    internal ChaCha20Poly1305(ReadOnlySpan<byte> key, bool forceManaged)
    {
        if (key.Length != 32) throw new ArgumentException("ChaCha20-Poly1305 key must be 32 bytes", nameof(key));
        if (SysChaCha.IsSupported && !forceManaged) _native = new SysChaCha(key);
        else _key = key.ToArray();
    }

    public void Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext,
        Span<byte> tag, ReadOnlySpan<byte> associatedData = default)
    {
        if (_native is not null) { _native.Encrypt(nonce, plaintext, ciphertext, tag, associatedData); return; }
        if (ciphertext.Length != plaintext.Length || tag.Length != TagSize)
            throw new ArgumentException("ciphertext length must equal plaintext length and tag must be 16 bytes");
        var cipher = Create(true, nonce, associatedData);
        byte[] output = new byte[plaintext.Length + TagSize];
        int n = cipher.ProcessBytes(plaintext.ToArray(), 0, plaintext.Length, output, 0);
        cipher.DoFinal(output, n);
        output.AsSpan(0, plaintext.Length).CopyTo(ciphertext);
        output.AsSpan(plaintext.Length, TagSize).CopyTo(tag);
    }

    public void Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag,
        Span<byte> plaintext, ReadOnlySpan<byte> associatedData = default)
    {
        if (_native is not null) { _native.Decrypt(nonce, ciphertext, tag, plaintext, associatedData); return; }
        if (plaintext.Length != ciphertext.Length || tag.Length != TagSize)
            throw new ArgumentException("plaintext length must equal ciphertext length and tag must be 16 bytes");
        var cipher = Create(false, nonce, associatedData);
        byte[] input = new byte[ciphertext.Length + TagSize];
        ciphertext.CopyTo(input);
        tag.CopyTo(input.AsSpan(ciphertext.Length));
        byte[] output = new byte[ciphertext.Length];
        try
        {
            int n = cipher.ProcessBytes(input, 0, input.Length, output, 0);
            cipher.DoFinal(output, n);
        }
        catch (InvalidCipherTextException)
        {
            plaintext.Clear();
            throw new System.Security.Cryptography.AuthenticationTagMismatchException();
        }
        output.CopyTo(plaintext);
    }

    private BcChaCha Create(bool encrypt, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> associatedData)
    {
        if (nonce.Length != 12) throw new ArgumentException("ChaCha20-Poly1305 nonce must be 12 bytes", nameof(nonce));
        var cipher = new BcChaCha();
        cipher.Init(encrypt, new AeadParameters(new KeyParameter(_key!), TagSize * 8, nonce.ToArray(),
            associatedData.IsEmpty ? null : associatedData.ToArray()));
        return cipher;
    }

    public void Dispose()
    {
        _native?.Dispose();
        if (_key is not null) Array.Clear(_key);
    }
}
