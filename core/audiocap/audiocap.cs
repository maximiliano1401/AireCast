// audiocap: captura el audio del sistema para AireCast sin que tenga que sonar en el PC.
//
// Uso: audiocap.exe <pid_excluido> [--mute]
//
// - Captura con "process loopback" (Windows 10 2004+) todo el audio excepto el del árbol de
//   procesos <pid_excluido> (AireCast). Esta captura ocurre ANTES del volumen y del
//   silencio de Windows, así que la TV recibe la señal completa aunque el PC esté silenciado.
// - Escribe PCM 44100 Hz, 16 bits, estéreo, little-endian por stdout.
// - Con --mute silencia la salida del PC mientras corre y la restaura al terminar. Termina cuando
//   se cierra su stdin (incluido si AireCast muere), así el silencio nunca se queda puesto.
// - Escribe "ok" (o "error: …") en stderr al iniciar.
//
// Se compila con el csc.exe de .NET Framework incluido en Windows (C# 5): ver build-audiocap.ps1.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IActivateAudioInterfaceAsyncOperation
{
    void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
}

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IActivateAudioInterfaceCompletionHandler
{
    void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op);
}

[ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAgileObject { }

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient
{
    int Initialize(int shareMode, int flags, long bufferDuration, long periodicity, IntPtr format, IntPtr session);
    int GetBufferSize(out uint frames);
    int GetStreamLatency(out long latency);
    int GetCurrentPadding(out uint padding);
    int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    int GetMixFormat(out IntPtr format);
    int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    int Start();
    int Stop();
    int Reset();
    int SetEventHandle(IntPtr handle);
    int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioCaptureClient
{
    int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong position, out ulong qpc);
    int ReleaseBuffer(uint frames);
    int GetNextPacketSize(out uint frames);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume
{
    int RegisterControlChangeNotify(IntPtr notify);
    int UnregisterControlChangeNotify(IntPtr notify);
    int GetChannelCount(out uint count);
    int SetMasterVolumeLevel(float level, ref Guid context);
    int SetMasterVolumeLevelScalar(float level, ref Guid context);
    int GetMasterVolumeLevel(out float level);
    int GetMasterVolumeLevelScalar(out float level);
    int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
    int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
    int GetChannelVolumeLevel(uint channel, out float level);
    int GetChannelVolumeLevelScalar(uint channel, out float level);
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints(int flow, int state, out IntPtr devices);
    int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumerator { }

[ComVisible(true)]
class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
{
    public readonly ManualResetEvent Done = new ManualResetEvent(false);
    public object Client;
    public int Result;

    public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
    {
        op.GetActivateResult(out Result, out Client);
        Done.Set();
    }
}

static class AudioCap
{
    const int ActivationTypeProcessLoopback = 1;
    const int ProcessLoopbackModeExcludeTree = 1;
    const int StreamFlagsLoopback = 0x00020000;
    const int StreamFlagsAutoConvertPcm = unchecked((int)0x80000000);
    const int StreamFlagsSrcDefaultQuality = 0x08000000;
    const uint BufferFlagsSilent = 2;
    const int SampleRate = 44100;
    const int BlockAlign = 4;  // 16 bits x 2 canales

    static volatile bool stopping;

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path,
        [MarshalAs(UnmanagedType.LPStruct)] Guid iid, IntPtr parameters,
        IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation op);

    [MTAThread]
    static int Main(string[] args)
    {
        int excludePid;
        if (args.Length < 1 || !int.TryParse(args[0], out excludePid))
        {
            Console.Error.WriteLine("error: uso: audiocap.exe <pid_excluido> [--mute]");
            return 2;
        }
        bool mute = Array.IndexOf(args, "--mute") >= 0;

        IAudioClient client;
        try
        {
            client = ActivateProcessLoopback(excludePid);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }

        IAudioEndpointVolume endpoint = null;
        bool restoreMute = false;
        Guid noContext = Guid.Empty;
        if (mute)
        {
            try
            {
                endpoint = DefaultEndpointVolume();
                bool wasMuted;
                endpoint.GetMute(out wasMuted);
                if (!wasMuted)
                {
                    endpoint.SetMute(true, ref noContext);
                    restoreMute = true;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("aviso: no se pudo silenciar el PC: " + ex.Message);
            }
        }

        // Al cerrarse stdin (AireCast terminó o murió) dejamos de capturar y restauramos.
        Thread watcher = new Thread(delegate ()
        {
            try { Console.OpenStandardInput().Read(new byte[1], 0, 1); } catch { }
            stopping = true;
        });
        watcher.IsBackground = true;
        watcher.Start();

        try
        {
            Console.Error.WriteLine("ok");
            Capture(client);
            return 0;
        }
        catch (IOException)
        {
            return 0;  // stdout cerrado: AireCast ya no lee
        }
        finally
        {
            if (restoreMute)
            {
                try { endpoint.SetMute(false, ref noContext); } catch { }
            }
        }
    }

    static IAudioClient ActivateProcessLoopback(int excludePid)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType, { TargetProcessId, ProcessLoopbackMode } }
        IntPtr parameters = Marshal.AllocHGlobal(12);
        Marshal.WriteInt32(parameters, 0, ActivationTypeProcessLoopback);
        Marshal.WriteInt32(parameters, 4, excludePid);
        Marshal.WriteInt32(parameters, 8, ProcessLoopbackModeExcludeTree);

        // PROPVARIANT de tipo VT_BLOB que apunta a los parámetros
        IntPtr variant = Marshal.AllocHGlobal(24);
        for (int i = 0; i < 24; i++) Marshal.WriteByte(variant, i, 0);
        Marshal.WriteInt16(variant, 0, 65);
        Marshal.WriteInt32(variant, 8, 12);
        Marshal.WriteIntPtr(variant, 16, parameters);

        ActivationHandler handler = new ActivationHandler();
        IActivateAudioInterfaceAsyncOperation op;
        ActivateAudioInterfaceAsync("VAD\\Process_Loopback", typeof(IAudioClient).GUID, variant, handler, out op);
        if (!handler.Done.WaitOne(5000))
            throw new Exception("Windows no respondió a la activación de la captura por proceso");
        if (handler.Result != 0)
            throw new Exception("la captura por proceso no está disponible (Windows 10 2004 o posterior), hr=0x" + handler.Result.ToString("x8"));

        IAudioClient client = (IAudioClient)handler.Client;
        IntPtr format = Marshal.AllocHGlobal(18);  // WAVEFORMATEX PCM 44100/16/2
        Marshal.WriteInt16(format, 0, 1);
        Marshal.WriteInt16(format, 2, 2);
        Marshal.WriteInt32(format, 4, SampleRate);
        Marshal.WriteInt32(format, 8, SampleRate * BlockAlign);
        Marshal.WriteInt16(format, 12, BlockAlign);
        Marshal.WriteInt16(format, 14, 16);
        Marshal.WriteInt16(format, 16, 0);
        int hr = client.Initialize(0, StreamFlagsLoopback | StreamFlagsAutoConvertPcm | StreamFlagsSrcDefaultQuality,
                                   2000000, 0, format, IntPtr.Zero);
        if (hr != 0) throw new Exception("no se pudo iniciar la captura, hr=0x" + hr.ToString("x8"));
        return client;
    }

    static IAudioEndpointVolume DefaultEndpointVolume()
    {
        IMMDevice device;
        ((IMMDeviceEnumerator)new MMDeviceEnumerator()).GetDefaultAudioEndpoint(0, 1, out device);
        Guid iid = typeof(IAudioEndpointVolume).GUID;
        object volume;
        device.Activate(ref iid, 23, IntPtr.Zero, out volume);
        return (IAudioEndpointVolume)volume;
    }

    static void Capture(IAudioClient client)
    {
        Guid iid = typeof(IAudioCaptureClient).GUID;
        object service;
        client.GetService(ref iid, out service);
        IAudioCaptureClient capture = (IAudioCaptureClient)service;
        Stream stdout = Console.OpenStandardOutput();
        byte[] buffer = new byte[SampleRate * BlockAlign];
        client.Start();
        try
        {
            while (!stopping)
            {
                Thread.Sleep(5);
                uint packet;
                capture.GetNextPacketSize(out packet);
                while (packet > 0)
                {
                    IntPtr data;
                    uint frames, flags;
                    ulong position, qpc;
                    capture.GetBuffer(out data, out frames, out flags, out position, out qpc);
                    int bytes = (int)frames * BlockAlign;
                    if (bytes > buffer.Length) buffer = new byte[bytes];
                    if ((flags & BufferFlagsSilent) != 0) Array.Clear(buffer, 0, bytes);
                    else Marshal.Copy(data, buffer, 0, bytes);
                    capture.ReleaseBuffer(frames);
                    stdout.Write(buffer, 0, bytes);
                    capture.GetNextPacketSize(out packet);
                }
                stdout.Flush();
            }
        }
        finally
        {
            client.Stop();
        }
    }
}
