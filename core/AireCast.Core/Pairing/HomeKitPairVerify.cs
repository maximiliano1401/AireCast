// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;

namespace AireCast.Core.Pairing;

/// <summary>
/// Pair-verify de HomeKit, en dos intercambios: M1 (nuestra clave efímera) -> M2 (la de la TV,
/// firmada) y M3 (nuestra firma) -> M4. Después, el secreto compartido cifra el canal.
/// </summary>
public sealed class HomeKitPairVerify
{
    private readonly Credentials _credentials;
    private readonly byte[] _private;
    private readonly byte[] _public;
    private byte[]? _shared;

    public HomeKitPairVerify(Credentials credentials, byte[]? ephemeralPrivateKey = null)
    {
        _credentials = credentials;
        _private = ephemeralPrivateKey ?? RandomNumberGenerator.GetBytes(32);
        _public = PairCrypto.X25519PublicKey(_private);
    }

    public byte[] Message1() => Tlv8.Write((Tlv8.SeqNo, [1]), (Tlv8.PublicKey, _public));

    /// <summary>Comprueba la identidad de la TV en M2 y devuelve M3.</summary>
    /// <exception cref="PairingException">La TV no es la emparejada o rechazó las credenciales.</exception>
    public byte[] Message3(byte[] message2)
    {
        var data = Tlv8.Read(message2);
        ThrowOnError(data);
        if (!data.TryGetValue(Tlv8.PublicKey, out byte[]? tvPublic) || !data.TryGetValue(Tlv8.EncryptedData, out byte[]? encrypted))
            throw new PairingException("Respuesta de pair-verify incompleta");

        _shared = PairCrypto.X25519Shared(_private, tvPublic);
        byte[] key = PairCrypto.Hkdf("Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info", _shared);

        var inner = Tlv8.Read(PairCrypto.Open(key, PairCrypto.TextNonce("PV-Msg02"), encrypted));
        if (!inner.TryGetValue(Tlv8.Identifier, out byte[]? identifier) || !inner.TryGetValue(Tlv8.Signature, out byte[]? signature))
            throw new PairingException("Respuesta de pair-verify incompleta");
        if (!identifier.AsSpan().SequenceEqual(_credentials.AccessoryId))
            throw new PairingException("La TV no es la que se emparejó");
        if (!PairCrypto.VerifySignature(_credentials.Ltpk, [.. tvPublic, .. identifier, .. _public], signature))
            throw new PairingException("La firma de la TV no es válida");

        byte[] ourSignature = PairCrypto.Sign(_credentials.Ltsk, [.. _public, .. _credentials.ClientId, .. tvPublic]);
        byte[] tlv = Tlv8.Write((Tlv8.Identifier, _credentials.ClientId), (Tlv8.Signature, ourSignature));
        byte[] sealedTlv = PairCrypto.Seal(key, PairCrypto.TextNonce("PV-Msg03"), tlv);
        return Tlv8.Write((Tlv8.SeqNo, [3]), (Tlv8.EncryptedData, sealedTlv));
    }

    /// <exception cref="PairingException">La TV rechazó M3.</exception>
    public void CheckMessage4(byte[] message4) => ThrowOnError(Tlv8.Read(message4));

    /// <summary>Clave derivada del secreto compartido; sirve para el canal RTSP, los eventos y los datos.</summary>
    public byte[] DeriveKey(string salt, string info) =>
        PairCrypto.Hkdf(salt, info, _shared ?? throw new InvalidOperationException("Pair-verify sin terminar"));

    private static void ThrowOnError(Dictionary<byte, byte[]> data)
    {
        if (data.TryGetValue(Tlv8.Error, out byte[]? error))
            throw new PairingException($"La TV rechazó las credenciales (código {Convert.ToHexString(error)}): hay que volver a emparejar");
    }
}
