// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace AireCast.Core.Audio;

/// <summary>
/// Afina el temporizador de Windows mientras se envía audio. Por defecto cada proceso despierta a ratos de
/// 15,6 ms: un <c>Thread.Sleep(2)</c> dura ~15 ms, y el audio, que son paquetes de 8 ms, sale en ráfagas
/// de dos. Con 1 ms despierta cada ~2,7 ms y los paquetes salen uno a uno, sin añadir ningún retraso.
/// En otros sistemas no hace nada.
/// </summary>
internal sealed class TimerResolution : IDisposable
{
    private const int ProcessPowerThrottling = 4;
    private const uint PowerThrottlingCurrentVersion = 1;
    private const uint IgnoreTimerResolution = 4;

    private readonly uint _milliseconds;

    private TimerResolution(uint milliseconds) => _milliseconds = milliseconds;

    /// <summary>Pide esa resolución; devuelve null si no hace falta o no se pudo.</summary>
    public static TimerResolution? Begin(uint milliseconds = 1)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            // Windows 11 ignora la petición con la ventana minimizada, que es justo cuando se duplica.
            var state = new PowerThrottlingState { Version = PowerThrottlingCurrentVersion, ControlMask = IgnoreTimerResolution, StateMask = 0 };
            SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>());
            return timeBeginPeriod(milliseconds) == 0 ? new TimerResolution(milliseconds) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public void Dispose() => timeEndPeriod(_milliseconds);

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState information, int size);
}
