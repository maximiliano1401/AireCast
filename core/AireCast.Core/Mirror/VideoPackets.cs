// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace AireCast.Core.Mirror;

/// <summary>Paquetes del canal de vídeo: una cabecera de 128 bytes y su contenido.</summary>
public static class VideoPackets
{
    public const int HeaderLength = 128;
    private const byte TypeVideo = 0;
    private const byte TypeCodec = 1;
    private const byte CodecOption = 0x16;
    public const byte TypeHeartbeat = 2;
    public const byte HeartbeatOption = 0x1E;

    /// <summary>Cabecera: tamaño, tipo, flags, opción, un byte a cero y la marca de tiempo; el resto, relleno.</summary>
    public static byte[] Header(int size, byte type, byte flags, byte option, ulong time)
    {
        byte[] header = new byte[HeaderLength];
        BinaryPrimitives.WriteInt32LittleEndian(header, size);
        header[4] = type;
        header[5] = flags;
        header[6] = option;
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), time);
        return header;
    }

    public static byte[] VideoHeader(int size, ulong time) => Header(size, TypeVideo, 0, 0, time);

    /// <summary>
    /// Paquete de configuración del códec (avcC en claro), con el tamaño de la imagen y el tamaño al
    /// que debe presentarla la TV (los últimos dos valores).
    /// </summary>
    public static byte[] Codec(byte[] avcc, int width, int height, ulong time, (int Width, int Height)? display = null)
    {
        byte[] header = Header(avcc.Length, TypeCodec, 0, CodecOption, time);
        var (displayWidth, displayHeight) = display ?? (width, height);
        Span<float> dimensions = [width, height, 0, 0, 0, 0, width, height, 0, 0, displayWidth, displayHeight];
        for (int i = 0; i < dimensions.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(16 + i * 4), dimensions[i]);
        return [.. header, .. avcc];
    }

    /// <summary>Un fotograma en formato AVCC: cada NAL con su longitud (4 bytes, big-endian).</summary>
    public static byte[] Avcc(IEnumerable<byte[]> nals)
    {
        using var output = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (byte[] nal in nals)
        {
            BinaryPrimitives.WriteInt32BigEndian(length, nal.Length);
            output.Write(length);
            output.Write(nal);
        }
        return output.ToArray();
    }
}
