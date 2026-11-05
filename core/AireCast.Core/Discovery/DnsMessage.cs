// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace AireCast.Core.Discovery;

public enum DnsType : ushort
{
    A = 1,
    Ptr = 12,
    Txt = 16,
    Srv = 33,
}

public abstract record DnsRecord(string Name);

public sealed record PtrRecord(string Name, string Target) : DnsRecord(Name);

public sealed record SrvRecord(string Name, string Target, int Port) : DnsRecord(Name);

public sealed record TxtRecord(string Name, IReadOnlyDictionary<string, string> Values) : DnsRecord(Name);

public sealed record AddressRecord(string Name, IPAddress Address) : DnsRecord(Name);

/// <summary>Formato de los mensajes DNS (RFC 1035) que usa mDNS: solo los registros que hacen falta.</summary>
public static class DnsMessage
{
    private const int HeaderLength = 12;
    private const ushort ClassIn = 1;
    private const int MaxNameJumps = 32;

    /// <summary>Consulta con estas preguntas (nombre, tipo).</summary>
    public static byte[] BuildQuery(IReadOnlyCollection<(string Name, DnsType Type)> questions)
    {
        using var message = new MemoryStream();
        Span<byte> header = stackalloc byte[HeaderLength];
        header.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], (ushort)questions.Count);
        message.Write(header);
        Span<byte> tail = stackalloc byte[4];
        foreach (var (name, type) in questions)
        {
            foreach (string label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(label);
                message.WriteByte((byte)bytes.Length);
                message.Write(bytes);
            }
            message.WriteByte(0);
            BinaryPrimitives.WriteUInt16BigEndian(tail, (ushort)type);
            BinaryPrimitives.WriteUInt16BigEndian(tail[2..], ClassIn);
            message.Write(tail);
        }
        return message.ToArray();
    }

    /// <summary>Registros de una respuesta (respuestas, autoridad y adicionales); ignora los demás tipos.</summary>
    /// <exception cref="FormatException">El mensaje está mal formado.</exception>
    public static List<DnsRecord> Parse(ReadOnlySpan<byte> message)
    {
        if (message.Length < HeaderLength)
            throw new FormatException("Mensaje DNS demasiado corto");
        int questions = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        int records = BinaryPrimitives.ReadUInt16BigEndian(message[6..])
            + BinaryPrimitives.ReadUInt16BigEndian(message[8..])
            + BinaryPrimitives.ReadUInt16BigEndian(message[10..]);

        int pos = HeaderLength;
        for (int i = 0; i < questions; i++)
        {
            ReadName(message, ref pos);
            pos += 4;
        }

        var result = new List<DnsRecord>();
        for (int i = 0; i < records; i++)
        {
            string name = ReadName(message, ref pos);
            if (pos + 10 > message.Length)
                throw new FormatException("Registro DNS incompleto");
            var type = (DnsType)BinaryPrimitives.ReadUInt16BigEndian(message[pos..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(message[(pos + 8)..]);
            int data = pos + 10;
            if (data + length > message.Length)
                throw new FormatException("Datos del registro DNS incompletos");
            pos = data + length;

            int cursor = data;
            switch (type)
            {
                case DnsType.Ptr:
                    result.Add(new PtrRecord(name, ReadName(message, ref cursor)));
                    break;
                case DnsType.Srv when length >= 6:
                    int port = BinaryPrimitives.ReadUInt16BigEndian(message[(data + 4)..]);
                    cursor = data + 6;
                    result.Add(new SrvRecord(name, ReadName(message, ref cursor), port));
                    break;
                case DnsType.Txt:
                    result.Add(new TxtRecord(name, ParseTxt(message.Slice(data, length))));
                    break;
                case DnsType.A when length == 4:
                    result.Add(new AddressRecord(name, new IPAddress(message.Slice(data, 4))));
                    break;
            }
        }
        return result;
    }

    /// <summary>Lee un nombre, siguiendo los punteros de compresión; deja <paramref name="pos"/> tras el nombre.</summary>
    private static string ReadName(ReadOnlySpan<byte> message, ref int pos)
    {
        var labels = new List<string>();
        int cursor = pos;
        int jumps = 0;
        while (true)
        {
            if (cursor >= message.Length)
                throw new FormatException("Nombre DNS incompleto");
            int length = message[cursor];
            if (length == 0)
            {
                if (jumps == 0)
                    pos = cursor + 1;
                return string.Join('.', labels);
            }
            if ((length & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= message.Length || ++jumps > MaxNameJumps)
                    throw new FormatException("Puntero DNS no válido");
                if (jumps == 1)
                    pos = cursor + 2;
                cursor = (length & 0x3F) << 8 | message[cursor + 1];
                continue;
            }
            if (cursor + 1 + length > message.Length)
                throw new FormatException("Etiqueta DNS incompleta");
            labels.Add(Encoding.UTF8.GetString(message.Slice(cursor + 1, length)));
            cursor += 1 + length;
        }
    }

    private static Dictionary<string, string> ParseTxt(ReadOnlySpan<byte> data)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int pos = 0;
        while (pos < data.Length)
        {
            int length = data[pos++];
            if (pos + length > data.Length)
                break;
            string entry = Encoding.UTF8.GetString(data.Slice(pos, length));
            pos += length;
            int equals = entry.IndexOf('=');
            if (equals > 0)
                values[entry[..equals]] = entry[(equals + 1)..];
        }
        return values;
    }
}
