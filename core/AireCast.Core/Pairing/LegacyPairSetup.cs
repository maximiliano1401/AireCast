// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace AireCast.Core.Pairing;

/// <summary>Pair-setup de los Apple TV antiguos, con plist: SRP de 2048 bits y la clave pública cifrada con AES-GCM.</summary>
public sealed class LegacyPairSetup
{
    private const int TagBits = 128;

    private readonly byte[] _seed;
    private readonly byte[] _clientId;
    private byte[] _sessionKey = [];

    public LegacyPairSetup(byte[]? seed = null, byte[]? clientId = null)
    {
        _seed = seed ?? RandomNumberGenerator.GetBytes(32);
        _clientId = clientId ?? RandomNumberGenerator.GetBytes(8);
    }

    public string User => Convert.ToHexString(_clientId);

    public Credentials Credentials => new([], _seed, [], _clientId);

    /// <summary>La clave pública y la prueba SRP a partir de la respuesta de la TV.</summary>
    public (byte[] Public, byte[] Proof) Respond(byte[] salt, byte[] serverPublic, string pin)
    {
        SrpClient srp = SrpClient.Legacy(User, pin, _seed);
        var (proof, key) = srp.Process(salt, serverPublic);
        _sessionKey = key;
        return (srp.Public, proof);
    }

    /// <summary>Nuestra clave pública cifrada, y su etiqueta. El IV termina en su último byte más uno.</summary>
    public (byte[] Epk, byte[] Tag) EncryptPublicKey()
    {
        byte[] key = Sha512("Pair-Setup-AES-Key")[..16];
        byte[] iv = Sha512("Pair-Setup-AES-IV")[..16];
        iv[^1]++;

        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(true, new AeadParameters(new KeyParameter(key), TagBits, iv));
        byte[] plain = PairCrypto.Ed25519PublicKey(_seed);
        byte[] output = new byte[cipher.GetOutputSize(plain.Length)];
        int length = cipher.ProcessBytes(plain, 0, plain.Length, output, 0);
        length += cipher.DoFinal(output, length);
        return (output[..plain.Length], output[plain.Length..length]);
    }

    private byte[] Sha512(string prefix) => SHA512.HashData([.. Encoding.ASCII.GetBytes(prefix), .. _sessionKey]);
}
