// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;

namespace AireCast.Core.Pairing;

/// <summary>
/// Emparejamiento temporal de HomeKit, para receptores que no piden PIN: solo M1 a M4 de pair-setup, con
/// un PIN fijo del protocolo. No deja credenciales: el secreto SRP sirve para esta conexión.
/// </summary>
public sealed class HomeKitTransientPairing(byte[]? seed = null) : ISessionKeys
{
    private const string TransientPin = "3939";
    private const byte TransientFlag = 0x10;

    private readonly byte[] _seed = seed ?? RandomNumberGenerator.GetBytes(32);
    private byte[]? _sessionKey;
    private bool _finished;

    public static byte[] Message1() => Tlv8.Write((0, [0]), (Tlv8.SeqNo, [1]), (Tlv8.Flags, [TransientFlag]));

    /// <summary>A partir de M2, la prueba M3.</summary>
    /// <exception cref="PairingException">La TV no admite el emparejamiento temporal.</exception>
    public byte[] Message3(byte[] message2)
    {
        var data = Tlv8.Read(message2);
        ThrowOnError(data);
        if (!data.TryGetValue(Tlv8.Salt, out byte[]? salt) || !data.TryGetValue(Tlv8.PublicKey, out byte[]? serverPublic))
            throw new PairingException("Respuesta de pair-setup incompleta");

        SrpClient srp = SrpClient.HomeKit(TransientPin, _seed);
        var (proof, key) = srp.Process(salt, serverPublic);
        _sessionKey = key;
        return Tlv8.Write((Tlv8.SeqNo, [3]), (Tlv8.PublicKey, srp.Public), (Tlv8.Proof, proof));
    }

    /// <exception cref="PairingException">La TV rechazó el emparejamiento.</exception>
    public void Finish(byte[] message4)
    {
        ThrowOnError(Tlv8.Read(message4));
        _finished = true;
    }

    public byte[] DeriveKey(string salt, string info) =>
        _finished ? PairCrypto.Hkdf(salt, info, _sessionKey!) : throw new InvalidOperationException("Emparejamiento sin terminar");

    private static void ThrowOnError(Dictionary<byte, byte[]> data)
    {
        if (data.TryGetValue(Tlv8.Error, out byte[]? error))
            throw new PairingException($"Esta TV pide emparejar con PIN (código {Convert.ToHexString(error)})");
    }
}
