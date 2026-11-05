// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AireCast.Core.FairPlay;

/// <summary>
/// FairPlay SAP del lado emisor: intercambio /fp-setup y envoltura de la clave del flujo. Traducción de
/// fairplay.py, que a su vez lo es de doubletake (https://github.com/omarroth/doubletake), LGPL-3.0-or-later.
/// </summary>
public sealed class FairPlaySession
{
    private const int SapLength = 128;
    private static readonly byte[] Magic = "FPLY"u8.ToArray();
    private static readonly byte[] Message1Payload = [0x02, 0x00, 0x03, 0xbb];
    private static readonly byte[] M3Label = [0x8f, 0x1a, 0x9c];

    private readonly byte[] _localSap;
    private byte[]? _remoteSap;
    private byte[]? _message3;

    public FairPlaySession(byte[]? entropy = null)
    {
        _localSap = [0x00, 0x01, .. entropy ?? RandomNumberGenerator.GetBytes(SapLength - 2)];
    }

    internal byte[]? RemoteSap => _remoteSap;

    public static byte[] Message1()
    {
        byte[] record = Record(1, Message1Payload.Length);
        Message1Payload.CopyTo(record, 12);
        return record;
    }

    /// <exception cref="FormatException">La respuesta m2 no es válida.</exception>
    public byte[] Message3(byte[] message2)
    {
        ValidateRecord(message2, 2, 130);
        if (message2[12] != 2)
            throw new FormatException($"Marcador de m2 no válido: {message2[12]}");
        int mode = message2[13];
        if (mode >= SapCipher.Modes)
            throw new FormatException($"m2 eligió el modo FairPlay {mode}, no soportado");

        byte[] record = Record(3, 152);
        record[12] = (byte)mode;
        M3Label.CopyTo(record, 13);
        SapCipher.Encrypt(mode, _localSap).CopyTo(record, 16);
        _remoteSap = SapCipher.DecryptBody(mode, message2.AsSpan(14, SapLength));
        SapExchange.Exchange(_localSap, _remoteSap).CopyTo(record, 144);
        _message3 = record;
        return record;
    }

    /// <exception cref="FormatException">La confirmación m4 no coincide con m3.</exception>
    public void Confirm(byte[] message4)
    {
        ValidateRecord(message4, 4, 20);
        if (!message4.AsSpan(12).SequenceEqual(_message3.AsSpan(144)))
            throw new FormatException("La confirmación m4 no coincide con m3");
    }

    /// <summary>Registro ekey de 72 bytes que lleva la clave AES cifrada para el receptor.</summary>
    public byte[] WrapKey(byte[] rawKey, byte[]? mask = null) =>
        WrapKey(_remoteSap ?? throw new InvalidOperationException("FairPlay sin terminar"), _message3!, rawKey, mask);

    internal static byte[] WrapKey(byte[] receiverSap, byte[] message3, byte[] rawKey, byte[]? mask)
    {
        ValidateRecord(message3, 3, 152);
        if (rawKey.Length != 16)
            throw new ArgumentException("La clave AES debe tener 16 bytes");
        mask ??= RandomNumberGenerator.GetBytes(16);
        byte[] ekey = new byte[72];
        byte[] prefix = [.. Magic, 1, 2, 1, 0, 0, 0, 0, 0x3c, 0, 0, 0, 0];
        prefix.CopyTo(ekey, 0);
        mask.CopyTo(ekey, 16);
        BinaryPrimitives.WriteInt32BigEndian(ekey.AsSpan(32), rawKey.Length);

        using Aes aes = Aes.Create();
        aes.Key = SapExchange.DeriveWrappingKey(receiverSap, message3);
        byte[] masked = rawKey.Zip(mask, (key, bits) => (byte)(key ^ bits)).ToArray();
        aes.EncryptEcb(masked, PaddingMode.None).CopyTo(ekey, 56);

        byte[] macKey = SapExchange.Descriptor(SapCipher.DecryptMessage(message3), receiverSap);
        byte[] macInput = [.. ekey.AsSpan(0, 36), .. rawKey];
        HMACSHA1.HashData(macKey, macInput).CopyTo(ekey, 36);
        return ekey;
    }

    private static byte[] Record(byte type, int payloadLength)
    {
        byte[] record = new byte[12 + payloadLength];
        Magic.CopyTo(record, 0);
        record[4] = 3;
        record[5] = 1;
        record[6] = type;
        BinaryPrimitives.WriteInt32BigEndian(record.AsSpan(8), payloadLength);
        return record;
    }

    private static void ValidateRecord(byte[] record, byte type, int payloadLength)
    {
        if (record.Length != 12 + payloadLength)
            throw new FormatException($"Mensaje FPLY de {record.Length} bytes, se esperaban {12 + payloadLength}");
        if (!record.AsSpan(0, 4).SequenceEqual(Magic) || record[4] != 3 || record[5] != 1 || record[6] != type || record[7] != 0)
            throw new FormatException("Cabecera FPLY no válida");
        if (BinaryPrimitives.ReadInt32BigEndian(record.AsSpan(8)) != payloadLength)
            throw new FormatException("Longitud declarada en FPLY incorrecta");
    }
}
