// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace AireCast.Core.Mirror;

/// <summary>Lo que hace falta saber del flujo H.264: tipos de NAL, tamaño de imagen y registro avcC.</summary>
public static class H264
{
    public const int NalIdr = 5;
    public const int NalSps = 7;
    public const int NalPps = 8;
    public const int NalAud = 9;

    public static int NalType(byte[] nal) => nal[0] & 0x1F;

    /// <summary>Los NAL de un búfer Annex-B completo, como los que entrega MediaCodec en Android.</summary>
    public static List<byte[]> SplitBuffer(ReadOnlySpan<byte> data)
    {
        var splitter = new AnnexBSplitter();
        List<byte[]> nals = splitter.Feed(data);
        nals.AddRange(splitter.Feed([0, 0, 1])); // un código de inicio final cierra el último NAL
        return nals;
    }

    /// <summary>Registro AVCDecoderConfigurationRecord a partir de SPS y PPS.</summary>
    public static byte[] BuildAvcc(byte[] sps, byte[] pps)
    {
        byte[] spsLength = new byte[2], ppsLength = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(spsLength, (ushort)sps.Length);
        BinaryPrimitives.WriteUInt16BigEndian(ppsLength, (ushort)pps.Length);
        return [1, sps[1], sps[2], sps[3], 0xFF, 0xE1, .. spsLength, .. sps, 1, .. ppsLength, .. pps];
    }

    /// <summary>Ancho y alto de la imagen según un SPS (NAL completo, con su byte de cabecera).</summary>
    /// <exception cref="FormatException">El SPS usa algo que no se entiende.</exception>
    public static (int Width, int Height) SpsSize(byte[] sps)
    {
        var bits = new BitReader(RemoveEmulationPrevention(sps.AsSpan(1)));
        int profile = bits.Read(8);
        bits.Read(16); // restricciones y nivel
        bits.ReadUe(); // seq_parameter_set_id
        if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            if (bits.ReadUe() == 3) // chroma_format_idc
                bits.Read(1);
            bits.ReadUe();
            bits.ReadUe();
            bits.Read(1);
            if (bits.Read(1) != 0)
                throw new FormatException("SPS con matrices de escalado: no soportado");
        }
        bits.ReadUe(); // log2_max_frame_num_minus4
        int pocType = bits.ReadUe();
        if (pocType == 0)
        {
            bits.ReadUe();
        }
        else if (pocType == 1)
        {
            bits.Read(1);
            bits.ReadSe();
            bits.ReadSe();
            for (int i = bits.ReadUe(); i > 0; i--)
                bits.ReadSe();
        }
        bits.ReadUe(); // max_num_ref_frames
        bits.Read(1); // gaps_in_frame_num_allowed
        int width = (bits.ReadUe() + 1) * 16;
        int mapUnits = bits.ReadUe() + 1;
        int frameMbsOnly = bits.Read(1);
        if (frameMbsOnly == 0)
            bits.Read(1);
        bits.Read(1); // direct_8x8_inference
        int height = (2 - frameMbsOnly) * mapUnits * 16;
        if (bits.Read(1) != 0) // recorte (4:2:0: en pares de píxeles)
        {
            int left = bits.ReadUe(), right = bits.ReadUe(), top = bits.ReadUe(), bottom = bits.ReadUe();
            width -= 2 * (left + right);
            height -= 2 * (2 - frameMbsOnly) * (top + bottom);
        }
        return (width, height);
    }

    /// <summary>Quita los bytes 03 de las secuencias 00 00 03.</summary>
    private static byte[] RemoveEmulationPrevention(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length);
        int zeros = 0;
        foreach (byte value in data)
        {
            if (zeros >= 2 && value == 3)
            {
                zeros = 0;
                continue;
            }
            zeros = value == 0 ? zeros + 1 : 0;
            output.Add(value);
        }
        return [.. output];
    }

    private sealed class BitReader(byte[] data)
    {
        private int _position;

        private int ReadBit()
        {
            if (_position >= data.Length * 8)
                throw new FormatException("SPS incompleto");
            int bit = data[_position >> 3] >> (7 - (_position & 7)) & 1;
            _position++;
            return bit;
        }

        public int Read(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
                value = value << 1 | ReadBit();
            return value;
        }

        public int ReadUe()
        {
            int zeros = 0;
            while (ReadBit() == 0)
            {
                if (++zeros > 31)
                    throw new FormatException("SPS con un valor no válido");
            }
            return (1 << zeros) - 1 + Read(zeros);
        }

        public int ReadSe()
        {
            int value = ReadUe();
            return value % 2 != 0 ? (value + 1) / 2 : -(value / 2);
        }
    }
}

/// <summary>
/// Divide un flujo Annex-B en NALs completos. Un código de inicio de 4 bytes (00 00 00 01) contiene
/// el de 3: su primer cero cae al final del NAL anterior y se quita con los demás ceros finales.
/// </summary>
public sealed class AnnexBSplitter
{
    private static readonly byte[] StartCode = [0, 0, 1];

    private byte[] _buffer = [];

    /// <summary>Añade datos y devuelve los NAL que ya están completos; el último espera al siguiente código.</summary>
    public List<byte[]> Feed(ReadOnlySpan<byte> data)
    {
        byte[] buffer = [.. _buffer, .. data];
        var nals = new List<byte[]>();
        int start = -1;
        int pos = buffer.AsSpan().IndexOf(StartCode);
        while (pos >= 0)
        {
            if (start >= 0)
                nals.Add(buffer.AsSpan(start, pos - start).TrimEnd((byte)0).ToArray());
            start = pos + StartCode.Length;
            int next = buffer.AsSpan(start).IndexOf(StartCode);
            pos = next < 0 ? -1 : start + next;
        }
        _buffer = start < 0 ? buffer : [0, 0, 0, 1, .. buffer.AsSpan(start)];
        return nals;
    }
}
