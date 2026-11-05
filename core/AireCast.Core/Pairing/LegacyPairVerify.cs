// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;

namespace AireCast.Core.Pairing;

/// <summary>
/// Pair-verify de los Apple TV antiguos. La clave de 32 bytes guardada sirve a la vez de semilla
/// Ed25519 y de clave X25519. No cifra el canal: solo demuestra que somos el emisor emparejado.
/// </summary>
public sealed class LegacyPairVerify(Credentials credentials)
{
    private readonly byte[] _seed = credentials.Ltsk;
    private readonly byte[] _public = PairCrypto.X25519PublicKey(credentials.Ltsk);

    public byte[] Message1() => [1, 0, 0, 0, .. _public, .. PairCrypto.Ed25519PublicKey(_seed)];

    /// <summary>A partir de la respuesta (clave de la TV y datos), la firma cifrada que se devuelve.</summary>
    /// <exception cref="PairingException">La respuesta es demasiado corta.</exception>
    public byte[] Message2(byte[] response)
    {
        if (response.Length < 32)
            throw new PairingException("Respuesta de pair-verify incompleta");
        byte[] tvPublic = response[..32];
        byte[] shared = PairCrypto.X25519Shared(_seed, tvPublic);
        byte[] key = Sha512("Pair-Verify-AES-Key", shared)[..16];
        byte[] iv = Sha512("Pair-Verify-AES-IV", shared)[..16];

        using var aes = new AesCtr(key, iv);
        aes.Transform(response.AsSpan(32)); // el contador debe avanzar antes sobre los datos de la TV
        byte[] signature = aes.Transform(PairCrypto.Sign(_seed, [.. _public, .. tvPublic]));
        return [0, 0, 0, 0, .. signature];
    }

    private static byte[] Sha512(string prefix, byte[] secret) => SHA512.HashData([.. Encoding.ASCII.GetBytes(prefix), .. secret]);
}
