// SPDX-License-Identifier: GPL-3.0-or-later
namespace AireCast.Core.Audio;

/// <summary>Formato del audio del duplicado: PCM 44100 Hz, 16 bits, estéreo, en paquetes de 352 muestras.</summary>
public static class AudioFormat
{
    public const int Rate = 44100;
    public const int Channels = 2;
    public const int SamplesPerPacket = 352;
    public const int PacketBytes = SamplesPerPacket * Channels * 2;

    /// <summary>Margen de los receptores AirPlay 2 que no declaran el suyo (25 ms, calibrado con la Roku).</summary>
    public const int DefaultLatency = Rate / 40;

    /// <summary>Máximo de margen que se anuncia en el SETUP.</summary>
    public const int MaxLatency = DefaultLatency * 4;

    /// <summary>Se descarta el audio con más de 40 ms de retraso: mejor saltar que atrasarse.</summary>
    public const int MaxBufferBytes = Rate / 25 * Channels * 2;

    /// <summary>Margen sobre el que pide el receptor, para la red y los reenvíos.</summary>
    public const int NetworkMarginMs = 70;

    /// <summary>Paquetes guardados para reenviarlos (~4 s).</summary>
    public const int Backlog = Rate * 4 / SamplesPerPacket;
}

/// <summary>PCM capturado esperando a enviarse; lo llena la captura y lo vacía el hilo de envío.</summary>
public sealed class PcmBuffer
{
    private readonly object _lock = new();
    private byte[] _data = new byte[AudioFormat.MaxBufferBytes * 4];
    private int _length;

    public void Write(ReadOnlySpan<byte> pcm)
    {
        lock (_lock)
        {
            if (_length + pcm.Length > _data.Length)
                Array.Resize(ref _data, Math.Max(_data.Length * 2, _length + pcm.Length));
            pcm.CopyTo(_data.AsSpan(_length));
            _length += pcm.Length;
        }
    }

    /// <summary>
    /// Un paquete de PCM en big-endian, como lo espera AirPlay. Si no hay bastante, se rellena con
    /// silencio: la captura no entrega nada mientras no suena nada.
    /// </summary>
    public byte[] TakePacket()
    {
        byte[] packet = new byte[AudioFormat.PacketBytes];
        lock (_lock)
        {
            if (_length > AudioFormat.MaxBufferBytes)
            {
                int drop = _length - AudioFormat.MaxBufferBytes;
                _data.AsSpan(drop, AudioFormat.MaxBufferBytes).CopyTo(_data);
                _length = AudioFormat.MaxBufferBytes;
            }
            int take = Math.Min(_length, packet.Length);
            _data.AsSpan(0, take).CopyTo(packet);
            _data.AsSpan(take, _length - take).CopyTo(_data);
            _length -= take;
        }
        for (int i = 0; i + 1 < packet.Length; i += 2)
            (packet[i], packet[i + 1]) = (packet[i + 1], packet[i]);
        return packet;
    }
}
