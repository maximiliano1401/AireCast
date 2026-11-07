// SPDX-License-Identifier: GPL-3.0-or-later
using AireCast.Core.Discovery;
using AireCast.Core.Mirror;
using AireCast.Core.Pairing;
using AireCast.Ui;
using Android.Content;
using Android.Net.Wifi;

namespace AireCast.Android;

/// <summary>Android: MediaProjection y MediaCodec, dentro de <see cref="MirrorService"/>.</summary>
internal sealed class AndroidPlatform(Context context) : IMirrorPlatform
{
    // MediaCodec no llega a todas las resoluciones en todos los teléfonos, y la nativa es vertical.
    public IReadOnlyList<string> Resolutions { get; } = ["720p", "1080p"];

    public string? ShareNote => "El teléfono seguirá sonando. Algunas apps no dejan capturar su audio: en la TV no se oirán.";

    public IDisposable BeginDiscovery()
    {
        // Android descarta el multicast entrante si no se pide este permiso temporal.
        var wifi = (WifiManager)context.GetSystemService(Context.WifiService)!;
        WifiManager.MulticastLock multicast = wifi.CreateMulticastLock("AireCast")!;
        multicast.Acquire();
        return new Released(multicast.Release);
    }

    public async Task MirrorAsync(AirPlayDevice device, Credentials credentials, VideoOptions options, NetworkProfile network,
        MirrorCallbacks callbacks, CancellationToken ct)
    {
        MainActivity activity = MainActivity.Current ?? throw new IOException("La app no está en pantalla.");
        Intent capture = await activity.RequestCaptureAsync();
        ct.ThrowIfCancellationRequested();

        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MirrorService.Request = new MirrorRequest(device, credentials, options, network, callbacks, finished);
        var start = new Intent(context, typeof(MirrorService));
        start.PutExtra(MirrorService.ResultData, capture);
        context.StartForegroundService(start);

        using CancellationTokenRegistration stop = ct.Register(() =>
            context.StartService(new Intent(context, typeof(MirrorService)).SetAction(MirrorService.ActionStop)));
        await finished.Task;
    }

    private sealed class Released(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
