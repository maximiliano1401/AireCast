// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Sockets;
using AireCast.Core.Audio;
using AireCast.Core.Discovery;
using AireCast.Core.Mirror;
using AireCast.Core.Net;
using AireCast.Core.Pairing;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media.Projection;
using Android.OS;

namespace AireCast.Android;

/// <summary>Lo que la pantalla y el servicio comparten durante un duplicado.</summary>
public static class MirrorBridge
{
    public static AirPlayDevice? Device { get; set; }
    public static Credentials? Credentials { get; set; }
    public static Func<bool, CancellationToken, Task<string>>? AskCode { get; set; }
    public static Action<string>? Status { get; set; }
    public static Action? Finished { get; set; }
}

/// <summary>
/// El duplicado. Android exige un servicio en primer plano de tipo mediaProjection, iniciado antes de
/// pedir la captura, y lo mantiene con la pantalla del teléfono apagada o la app en segundo plano.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaProjection)]
public sealed class MirrorService : Service
{
    public const string ResultCode = "resultCode";
    public const string ResultData = "resultData";
    public const string ActionStop = "com.max.airecast.STOP";

    private const string Channel = "airecast";
    private const int NotificationId = 1;
    private const int Width = 1280;
    private const int Height = 720;
    private const int Fps = 30;
    private const int Bitrate = 6_000_000;

    private CancellationTokenSource? _stop;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            _stop?.Cancel();
            return StartCommandResult.NotSticky;
        }

        ShowNotification();
        var manager = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
        var data = (Intent)intent!.GetParcelableExtra(ResultData)!;
        MediaProjection projection = manager.GetMediaProjection(intent.GetIntExtra(ResultCode, 0), data)!;
        projection.RegisterCallback(new ProjectionStopped(this), null);
        _stop = new CancellationTokenSource();
        _ = Task.Run(() => RunAsync(projection, _stop.Token));
        return StartCommandResult.NotSticky;
    }

    private void ShowNotification()
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(Channel, "Duplicado de pantalla", NotificationImportance.Low));
        Notification notification = new Notification.Builder(this, Channel)
            .SetContentTitle("AireCast")!
            .SetContentText("Compartiendo la pantalla")!
            .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)!
            .SetOngoing(true)!
            .Build()!;
        StartForeground(NotificationId, notification, ForegroundService.TypeMediaProjection);
    }

    private async Task RunAsync(MediaProjection projection, CancellationToken ct)
    {
        string result = "La transmisión ha terminado.";
        try
        {
            AirPlayDevice device = MirrorBridge.Device!;
            MirrorBridge.Status?.Invoke($"Conectando con «{device.Name}»…");
            using AirPlayConnection connection = await AirPlayConnection.ConnectAsync(device.Address, device.Port, ct);
            var client = new AirPlayClient(connection) { AskCode = MirrorBridge.AskCode };
            await client.VerifyAsync(MirrorBridge.Credentials!, ct);

            var pcm = new PcmBuffer();
            await using MirrorSession session = await MirrorSession.StartAsync(client, pcm, ct: ct);
            using SystemAudioRecorder? audio = session.HasAudio ? new SystemAudioRecorder(projection, pcm, ct) : null;
            int density = (int)Resources!.DisplayMetrics!.DensityDpi;
            using var encoder = new ScreenEncoder(projection, Width, Height, density, Fps, Bitrate, session, ct);
            MirrorBridge.Status?.Invoke($"Compartiendo la pantalla en «{device.Name}».");

            while (!ct.IsCancellationRequested && encoder.Failure is null)
                await Task.Delay(500, CancellationToken.None);
            if (encoder.Failure is { } failure)
                throw new IOException(failure.Message);
            result = $"Transmisión detenida: {encoder.Frames} fotogramas.";
        }
        catch (System.OperationCanceledException)
        {
            result = "Transmisión detenida.";
        }
        catch (Exception ex) when (ex is IOException or SocketException or PairingException or FormatException or Java.Lang.Exception)
        {
            result = $"Se detuvo la transmisión: {ex.Message}";
        }
        projection.Stop();
        MirrorBridge.Status?.Invoke(result);
        MirrorBridge.Finished?.Invoke();
        StopForeground(StopForegroundFlags.Remove);
        StopSelf();
    }

    /// <summary>El usuario detuvo la captura desde el sistema (la notificación o el aviso de Android).</summary>
    private sealed class ProjectionStopped(MirrorService service) : MediaProjection.Callback
    {
        public override void OnStop() => service._stop?.Cancel();
    }
}
