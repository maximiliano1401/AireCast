// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace AireCast.Core.Pairing;

/// <summary>
/// Cliente SRP-6a del emparejamiento con PIN, en la variante que usan HomeKit y el Apple TV. Los enteros
/// se hashean con sus bytes mínimos (big-endian) salvo los que el protocolo rellena hasta el tamaño de N.
/// </summary>
public sealed class SrpClient
{
    private static readonly BigInteger Prime3072 = Parse("FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7EDEE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F83655D23DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3BE39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF6955817183995497CEA956AE515D2261898FA051015728E5A8AAAC42DAD33170D04507A33A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864D87602733EC86A64521F2B18177B200CBBE117577A615D6C770988C0BAD946E208E24FA074E5AB3143DB5BFCE0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF");
    private static readonly BigInteger Prime2048 = Parse("AC6BDB41324A9A9BF166DE5E1389582FAF72B6651987EE07FC3192943DB56050A37329CBB4A099ED8193E0757767A13DD52312AB4B03310DCD7F48A9DA04FD50E8083969EDB767B0CF6095179A163AB3661A05FBD5FAAAE82918A9962F0B93B855F97993EC975EEAA80D740ADBF4FF747359D041D5C33EA71D281E446B14773BCA97B43A23FB801676BD207A436C6481F1D2B9078717461A5B9D32E688F87748544523B524B0D57D5EA77A2775D2ECFA032CFBDBF52FB3786160279004E57AE6AF874E7303CE53299CCC041C7BC308D82A5698F3A8D0C38271AE35F8E9DBFBB694B5C803D89F7AE435DE236D525F54759B65E372FCD68EF20FA7111F9E4AFF73");

    private readonly BigInteger _prime;
    private readonly BigInteger _generator;
    private readonly Func<byte[], byte[]> _hash;
    private readonly Func<SrpClient, BigInteger, byte[]> _sessionKey;
    private readonly byte[] _user;
    private readonly byte[] _password;
    private readonly BigInteger _private;

    private SrpClient(BigInteger prime, BigInteger generator, Func<byte[], byte[]> hash, Func<SrpClient, BigInteger, byte[]> sessionKey,
        string user, string password, byte[] privateKey)
    {
        _prime = prime;
        _generator = generator;
        _hash = hash;
        _sessionKey = sessionKey;
        _user = Encoding.UTF8.GetBytes(user);
        _password = Encoding.UTF8.GetBytes(password);
        _private = new BigInteger(privateKey, isUnsigned: true, isBigEndian: true);
    }

    /// <summary>HomeKit: 3072 bits, SHA-512, usuario «Pair-Setup».</summary>
    public static SrpClient HomeKit(string pin, byte[] privateKey) =>
        new(Prime3072, 5, SHA512.HashData, (client, secret) => client.Hash(secret), "Pair-Setup", pin, privateKey);

    /// <summary>Apple TV antiguos: 2048 bits, SHA-1 y una clave de sesión de dos hashes.</summary>
    public static SrpClient Legacy(string user, string pin, byte[] privateKey) =>
        new(Prime2048, 2, SHA1.HashData,
            (client, secret) => [.. client.Hash(secret, new byte[] { 0, 0, 0, 0 }), .. client.Hash(secret, new byte[] { 0, 0, 0, 1 })],
            user, pin, privateKey);

    public byte[] Public => Bytes(BigInteger.ModPow(_generator, _private, _prime));

    /// <summary>Calcula la prueba M1 y la clave de sesión K a partir de la sal y la clave pública de la TV.</summary>
    /// <exception cref="PairingException">La clave pública de la TV no es válida.</exception>
    public (byte[] Proof, byte[] SessionKey) Process(byte[] salt, byte[] serverPublic)
    {
        var server = new BigInteger(serverPublic, isUnsigned: true, isBigEndian: true);
        if (server % _prime == 0)
            throw new PairingException("Clave pública de la TV no válida");

        BigInteger client = BigInteger.ModPow(_generator, _private, _prime);
        BigInteger multiplier = Number(Hash(_prime, Pad(_generator)));
        BigInteger common = Number(Hash(Pad(client), Pad(server)));
        BigInteger passwordHash = Number(Hash(salt, Hash(_user, ":"u8.ToArray(), _password)));
        BigInteger verifier = BigInteger.ModPow(_generator, passwordHash, _prime);

        BigInteger difference = (server - multiplier * verifier) % _prime;
        if (difference.Sign < 0)
            difference += _prime;
        BigInteger secret = BigInteger.ModPow(difference, _private + common * passwordHash, _prime);
        byte[] key = _sessionKey(this, secret);

        BigInteger mixed = Number(Hash(_prime)) ^ Number(Hash(_generator));
        byte[] proof = Hash(mixed, Hash(_user), salt, client, server, key);
        return (proof, key);
    }

    private byte[] Hash(params object[] values)
    {
        using var data = new MemoryStream();
        foreach (object value in values)
            data.Write(value is BigInteger number ? Bytes(number) : (byte[])value);
        return _hash(data.ToArray());
    }

    private byte[] Pad(BigInteger value)
    {
        byte[] bytes = Bytes(value);
        byte[] padded = new byte[Bytes(_prime).Length];
        bytes.CopyTo(padded, padded.Length - bytes.Length);
        return padded;
    }

    private static byte[] Bytes(BigInteger value) => value.ToByteArray(isUnsigned: true, isBigEndian: true);

    private static BigInteger Number(byte[] value) => new(value, isUnsigned: true, isBigEndian: true);

    private static BigInteger Parse(string hex) => BigInteger.Parse("0" + hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
