// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
using AireCast.Core.Pairing;

namespace AireCast.Core.Audio;

/// <summary>Cómo se empaqueta el audio de cada tipo de receptor.</summary>
public interface IAudioPacketizer
{
    byte[] Packet(byte[] pcm, ushort sequence, uint rtpTime, bool first);

    byte[] Sync(ulong headTime, uint rtpTime, uint latency, bool first);
}

/// <summary>Tiempo de audio en muestras y su equivalente NTP (como RAOP).</summary>
public static class AudioClock
{
    public static ulong SamplesFromNtp(ulong ntp) => (ntp >> 16) * AudioFormat.Rate >> 16;

    public static ulong NtpFromSamples(ulong samples) => (samples << 16) / AudioFormat.Rate << 16;
}

/// <summary>AirPlay 2: PCM cifrado con ChaCha20-Poly1305 con la clave que enviamos en el SETUP.</summary>
public sealed class ModernAudioPacketizer(byte[] key, uint ssrc) : IAudioPacketizer
{
    private ulong _counter;

    public byte[] Packet(byte[] pcm, ushort sequence, uint rtpTime, bool first)
    {
        byte[] header = new byte[12];
        header[0] = 0x80;
        header[1] = first ? (byte)0xE0 : (byte)0x60;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), rtpTime);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), ssrc);
        byte[] nonce = PairCrypto.CounterNonce(_counter++);
        byte[] payload = PairCrypto.Seal(key, nonce, pcm, header[4..12]);
        return [.. header, .. payload, .. nonce.AsSpan(4)]; // el nonce va al final del paquete
    }

    public byte[] Sync(ulong headTime, uint rtpTime, uint latency, bool first)
    {
        ulong ntp = AudioClock.NtpFromSamples(headTime);
        byte[] packet = new byte[20];
        packet[0] = first ? (byte)0x90 : (byte)0x80;
        packet[1] = 0xD4;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 7);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), rtpTime - latency);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(8), ntp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), rtpTime);
        return packet;
    }
}

/// <summary>Apple TV antiguos: ALAC sin comprimir, cifrado con AES-128-CBC con la clave FairPlay.</summary>
public sealed class LegacyAudioPacketizer(byte[] key, byte[] iv) : IAudioPacketizer
{
    public byte[] Packet(byte[] pcm, ushort sequence, uint rtpTime, bool first)
    {
        byte[] header = new byte[12];
        header[0] = 0x80;
        header[1] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), rtpTime); // SSRC 0

        byte[] frame = AlacVerbatim(pcm);
        int full = frame.Length / 16 * 16; // solo se cifran los bloques completos; el resto viaja en claro
        using Aes aes = Aes.Create();
        aes.Key = key;
        byte[] encrypted = aes.EncryptCbc(frame.AsSpan(0, full), iv, PaddingMode.None);
        return [.. header, .. encrypted, .. frame.AsSpan(full)];
    }

    public byte[] Sync(ulong headTime, uint rtpTime, uint latency, bool first)
    {
        uint playhead = rtpTime - latency;
        byte[] packet = new byte[20];
        packet[0] = first ? (byte)0x90 : (byte)0x80;
        packet[1] = 0xD4;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 4);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), playhead);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(8), AudioClock.NtpFromSamples(headTime));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), playhead);
        return packet;
    }

    /// <summary>
    /// Trama ALAC «verbatim» (sin comprimir), estéreo 16 bits, a partir de PCM big-endian. Bits: tag
    /// CPE(3)=1, instancia(4), sin uso(12), hasSize(1)=1, extraBytes(2)=0, verbatim(1)=1, nº de
    /// muestras(32), las muestras tal cual y el tag END(3)=7; se rellena hasta el byte.
    /// </summary>
    public static byte[] AlacVerbatim(byte[] pcm)
    {
        const int HeaderBits = 3 + 4 + 12 + 1 + 2 + 1 + 32;
        int totalBits = HeaderBits + pcm.Length * 8 + 3;
        byte[] frame = new byte[(totalBits + 7) / 8];
        int position = 0;

        void Write(ulong value, int bits)
        {
            for (int bit = bits - 1; bit >= 0; bit--, position++)
            {
                if ((value >> bit & 1) != 0)
                    frame[position >> 3] |= (byte)(0x80 >> (position & 7));
            }
        }

        Write(1, 3);
        Write(0, 4);
        Write(0, 12);
        Write(1, 1);
        Write(0, 2);
        Write(1, 1);
        Write(AudioFormat.SamplesPerPacket, 32);
        foreach (byte sample in pcm)
            Write(sample, 8);
        Write(7, 3);
        return frame;
    }
}
