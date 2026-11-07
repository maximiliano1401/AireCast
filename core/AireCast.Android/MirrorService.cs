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

/// <summary>Un duplicado pedido por la interfaz: lo recoge el servicio, y <paramref name="Finished"/> avisa de cómo acabó.</summary>
public sealed record MirrorRequest(AirPlayDevice Device, Credentials? Credentials, VideoOptions Options, NetworkProfile Network,
    MirrorCallbacks Callbacks, TaskCompletionSource Finished);

/// <summary>
/// El duplicado. Android exige un servicio en primer plano de tipo mediaProjection, iniciado antes de
/// pedir la captura, y lo mantiene con la pantalla del teléfono apagada o la app en segundo plano.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaProjection)]
public sealed class MirrorService : Service
{
    public const string ResultData = "resultData";
    public const string ActionStop = "com.max.airecast.STOP";

    private const string Channel = "airecast";
    private const int NotificationId = 1;
    private const int StatsSeconds = 10;

    /// <summary>Lo que el servicio debe duplicar al arrancar.</summary>
    public static MirrorRequest? Request { get; set; }

    private CancellationTokenSource? _stop;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            if (_stop is null)
                StopSelf();
            _stop?.Cancel();
            return StartCommandResult.NotSticky;
        }

        MirrorRequest request = Request!;
        ShowNotification();
        var manager = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
        var data = (Intent)intent!.GetParcelableExtra(ResultData)!;
        MediaProjection projection = manager.GetMediaProjection((int)Result.Ok, data)!;
        projection.RegisterCallback(new ProjectionStopped(this), null);
        _stop = new CancellationTokenSource();
        _ = Task.Run(() => RunAsync(request, projection, _stop.Token));
        return StartCommandResult.NotSticky;
    }

    /// <summary>Al cerrar la app desde Recientes ya no queda botón de «Detener»: se detiene el duplicado.</summary>
    public override void OnTaskRemoved(Intent? rootIntent)
    {
        _stop?.Cancel();
        base.OnTaskRemoved(rootIntent);
    }

    private void ShowNotification()
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(Channel, "Duplicado de pantalla", NotificationImportance.Low));
        var stop = PendingIntent.GetService(this, 0, new Intent(this, typeof(MirrorService)).SetAction(ActionStop), PendingIntentFlags.Immutable);
        var stopAction = new Notification.Action.Builder(null as global::Android.Graphics.Drawables.Icon, "Detener", stop).Build();
        Notification notification = new Notification.Builder(this, Channel)
            .SetContentTitle("AireCast")!
            .SetContentText("Compartiendo la pantalla")!
            .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)!
            .SetOngoing(true)!
            .AddAction(stopAction)!
            .Build()!;
        StartForeground(NotificationId, notification, ForegroundService.TypeMediaProjection);
    }

    private async Task RunAsync(MirrorRequest request, MediaProjection projection, CancellationToken ct)
    {
        try
        {
            await MirrorAsync(request, projection, ct);
            request.Finished.TrySetResult();
        }
        catch (System.OperationCanceledException)
        {
            request.Finished.TrySetCanceled(ct);
        }
        catch (Java.Lang.Exception ex)
        {
            request.Finished.TrySetException(new IOException(ex.Message, ex));
        }
        catch (Exception ex) when (ex is IOException or SocketException or PairingException or FormatException)
        {
            request.Finished.TrySetException(ex);
        }
        projection.Stop();
        StopForeground(StopForegroundFlags.Remove);
        StopSelf();
    }

    private async Task MirrorAsync(MirrorRequest request, MediaProjection projection, CancellationToken ct)
    {
        AirPlayDevice device = request.Device;
        VideoOptions options = request.Options;
        using AirPlayConnection connection = await AirPlayConnection.ConnectAsync(device.Address, device.Port, ct);
        var client = new AirPlayClient(connection) { AskCode = request.Callbacks.AskCode };
        await client.AuthenticateAsync(device, request.Credentials, ct);

        var pcm = new PcmBuffer();
        await using MirrorSession session = await MirrorSession.StartAsync(client, pcm, network: request.Network, ct: ct);
        using SystemAudioRecorder? audio = session.HasAudio ? new SystemAudioRecorder(projection, pcm, ct) : null;
        int density = (int)Resources!.DisplayMetrics!.DensityDpi;
        using var encoder = new ScreenEncoder(projection, options.Width!.Value, options.Height!.Value, density, options.Fps,
            (int)(options.MaxBitrate * 1_000_000), session, ct);
        request.Callbacks.Started?.Invoke();

        long lastFrames = 0, lastBytes = 0;
        while (!ct.IsCancellationRequested && encoder.Failure is null)
        {
            await Task.Delay(TimeSpan.FromSeconds(StatsSeconds), ct).ContinueWith(_ => { }, CancellationToken.None);
            Report(request, $"vídeo {(encoder.Frames - lastFrames) / (double)StatsSeconds:F1} fps; audio {((audio?.Bytes ?? 0) - lastBytes) / StatsSeconds} B/s; reenviados {session.ResentAudioPackets}");
            lastFrames = encoder.Frames;
            lastBytes = audio?.Bytes ?? 0;
        }
        if (encoder.Failure is { } failure)
            throw new IOException(failure.Message);
        ct.ThrowIfCancellationRequested();
    }

    private static void Report(MirrorRequest request, string text)
    {
        global::Android.Util.Log.Info("AireCast", text);
        request.Callbacks.Log?.Invoke(text);
    }

    /// <summary>El usuario detuvo la captura desde el sistema (la notificación o el aviso de Android).</summary>
    private sealed class ProjectionStopped(MirrorService service) : MediaProjection.Callback
    {
        public override void OnStop() => service._stop?.Cancel();
    }
}
