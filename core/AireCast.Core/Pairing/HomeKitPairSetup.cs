// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;

namespace AireCast.Core.Pairing;

/// <summary>
/// Pair-setup de HomeKit con PIN: M1/M2 dan la sal y la clave de la TV, M3 la prueba SRP, M5/M6
/// intercambian las identidades cifradas. El resultado son las credenciales de la app.
/// </summary>
public sealed class HomeKitPairSetup
{
    private readonly byte[] _seed;
    private readonly byte[] _pairingId;
    private byte[] _sessionKey = [];
    private byte[] _encryptKey = [];

    public HomeKitPairSetup(byte[]? seed = null, byte[]? pairingId = null)
    {
        _seed = seed ?? RandomNumberGenerator.GetBytes(32);
        _pairingId = pairingId ?? Encoding.ASCII.GetBytes(Guid.NewGuid().ToString());
    }

    public static byte[] Message1() => Tlv8.Write((0, [0]), (Tlv8.SeqNo, [1]));

    /// <summary>A partir de M2 y el PIN, la prueba M3.</summary>
    /// <exception cref="PairingException">La TV devolvió un error.</exception>
    public byte[] Message3(byte[] message2, string pin)
    {
        var data = Tlv8.Read(message2);
        ThrowOnError(data);
        if (!data.TryGetValue(Tlv8.Salt, out byte[]? salt) || !data.TryGetValue(Tlv8.PublicKey, out byte[]? serverPublic))
            throw new PairingException("Respuesta de pair-setup incompleta");

        SrpClient srp = SrpClient.HomeKit(pin, _seed);
        var (proof, key) = srp.Process(salt, serverPublic);
        _sessionKey = key;
        return Tlv8.Write((Tlv8.SeqNo, [3]), (Tlv8.PublicKey, srp.Public), (Tlv8.Proof, proof));
    }

    /// <summary>Nuestra identidad firmada y cifrada. Una respuesta M4 con error es un PIN incorrecto.</summary>
    /// <exception cref="PairingException">La TV rechazó el PIN.</exception>
    public byte[] Message5(byte[] message4)
    {
        if (Tlv8.Read(message4).ContainsKey(Tlv8.Error))
            throw new PairingException("PIN incorrecto");

        byte[] signSeed = PairCrypto.Hkdf("Pair-Setup-Controller-Sign-Salt", "Pair-Setup-Controller-Sign-Info", _sessionKey);
        _encryptKey = PairCrypto.Hkdf("Pair-Setup-Encrypt-Salt", "Pair-Setup-Encrypt-Info", _sessionKey);
        byte[] publicKey = PairCrypto.Ed25519PublicKey(_seed);
        byte[] signature = PairCrypto.Sign(_seed, [.. signSeed, .. _pairingId, .. publicKey]);
        byte[] tlv = Tlv8.Write((Tlv8.Identifier, _pairingId), (Tlv8.PublicKey, publicKey), (Tlv8.Signature, signature));
        return Tlv8.Write((Tlv8.SeqNo, [5]), (Tlv8.EncryptedData, PairCrypto.Seal(_encryptKey, PairCrypto.TextNonce("PS-Msg05"), tlv)));
    }

    /// <summary>Las credenciales, a partir de la identidad de la TV en M6.</summary>
    /// <exception cref="PairingException">La TV devolvió un error.</exception>
    public Credentials Finish(byte[] message6)
    {
        var data = Tlv8.Read(message6);
        ThrowOnError(data);
        if (!data.TryGetValue(Tlv8.EncryptedData, out byte[]? encrypted))
            throw new PairingException("Respuesta de pair-setup incompleta");
        var inner = Tlv8.Read(PairCrypto.Open(_encryptKey, PairCrypto.TextNonce("PS-Msg06"), encrypted));
        if (!inner.TryGetValue(Tlv8.Identifier, out byte[]? identifier) || !inner.TryGetValue(Tlv8.PublicKey, out byte[]? publicKey))
            throw new PairingException("Respuesta de pair-setup incompleta");
        return new Credentials(publicKey, _seed, identifier, _pairingId);
    }

    private static void ThrowOnError(Dictionary<byte, byte[]> data)
    {
        if (data.TryGetValue(Tlv8.Error, out byte[]? error))
            throw new PairingException($"La TV rechazó el emparejamiento (código {Convert.ToHexString(error)})");
    }
}
