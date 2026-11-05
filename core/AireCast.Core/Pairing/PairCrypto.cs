// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace AireCast.Core.Pairing;

/// <summary>
/// Primitivas de emparejamiento. Van con BouncyCastle porque .NET no trae Ed25519 ni X25519 y
/// ChaCha20-Poly1305 depende del sistema: así funciona igual en Windows, Linux y Android.
/// </summary>
public static class PairCrypto
{
    private const int NonceLength = 12;
    private const int TagBits = 128;

    public static byte[] Seal(byte[] key, byte[] nonce, byte[] plaintext, byte[]? aad = null) =>
        Run(true, key, nonce, plaintext, aad);

    /// <exception cref="PairingException">Los datos no se autentican con esa clave.</exception>
    public static byte[] Open(byte[] key, byte[] nonce, byte[] ciphertext, byte[]? aad = null)
    {
        try
        {
            return Run(false, key, nonce, ciphertext, aad);
        }
        catch (InvalidCipherTextException ex)
        {
            throw new PairingException("No se pudo descifrar la respuesta de la TV", ex);
        }
    }

    private static byte[] Run(bool encrypt, byte[] key, byte[] nonce, byte[] input, byte[]? aad)
    {
        var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
        cipher.Init(encrypt, new AeadParameters(new KeyParameter(key), TagBits, nonce, aad));
        byte[] output = new byte[cipher.GetOutputSize(input.Length)];
        int length = cipher.ProcessBytes(input, 0, input.Length, output, 0);
        length += cipher.DoFinal(output, length);
        return length == output.Length ? output : output[..length];
    }

    /// <summary>Nonce de texto fijo (por ejemplo «PV-Msg02»), rellenado con ceros a la izquierda.</summary>
    public static byte[] TextNonce(string text)
    {
        byte[] nonce = new byte[NonceLength];
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        bytes.CopyTo(nonce, NonceLength - bytes.Length);
        return nonce;
    }

    /// <summary>Nonce de contador: 4 ceros y el contador de 64 bits en little-endian.</summary>
    public static byte[] CounterNonce(ulong counter)
    {
        byte[] nonce = new byte[NonceLength];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), counter);
        return nonce;
    }

    public static byte[] Hkdf(string salt, string info, byte[] secret) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA512, secret, 32, Encoding.ASCII.GetBytes(salt), Encoding.ASCII.GetBytes(info));

    public static byte[] Sign(byte[] seed, byte[] message)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(seed));
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }

    public static bool VerifySignature(byte[] publicKey, byte[] message, byte[] signature)
    {
        var signer = new Ed25519Signer();
        signer.Init(false, new Ed25519PublicKeyParameters(publicKey));
        signer.BlockUpdate(message, 0, message.Length);
        return signer.VerifySignature(signature);
    }

    public static byte[] Ed25519PublicKey(byte[] seed) => new Ed25519PrivateKeyParameters(seed).GeneratePublicKey().GetEncoded();

    public static byte[] X25519PublicKey(byte[] privateKey) => new X25519PrivateKeyParameters(privateKey).GeneratePublicKey().GetEncoded();

    public static byte[] X25519Shared(byte[] privateKey, byte[] publicKey)
    {
        byte[] shared = new byte[32];
        new X25519PrivateKeyParameters(privateKey).GenerateSecret(new X25519PublicKeyParameters(publicKey), shared, 0);
        return shared;
    }
}

public sealed class PairingException(string message, Exception? inner = null) : Exception(message, inner);
