// SPDX-License-Identifier: GPL-3.0-or-later
using AireCast.Ui;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media.Projection;
using Android.OS;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace AireCast.Android;

[Application]
public sealed class AireCastApplication : AvaloniaAndroidApplication<Ui.App>
{
    public AireCastApplication(nint handle, JniHandleOwnership transfer) : base(handle, transfer)
    {
    }

    public override void OnCreate()
    {
        Ui.App.Platform = new AndroidPlatform(this);
        base.OnCreate();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).WithInterFont();
}

/// <summary>
/// Aloja la interfaz común y pide los permisos que solo una actividad puede pedir. Declara todos los cambios
/// de configuración: si Android la recreara, la pantalla nueva no sabría que hay un duplicado en marcha.
/// </summary>
[Activity(Label = "AireCast", Theme = "@style/AireCastTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTask,
    WindowSoftInputMode = global::Android.Views.SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.SmallestScreenSize
        | ConfigChanges.ScreenLayout | ConfigChanges.UiMode | ConfigChanges.Density | ConfigChanges.FontScale
        | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Locale | ConfigChanges.Navigation)]
public sealed class MainActivity : AvaloniaMainActivity
{
    private const int CaptureRequest = 1;
    private const int PermissionRequest = 2;

    private TaskCompletionSource<Intent>? _capture;

    /// <summary>La actividad en marcha, que es quien puede pedir la captura.</summary>
    public static MainActivity? Current { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Current = this;
        RequestPermissions([global::Android.Manifest.Permission.RecordAudio, global::Android.Manifest.Permission.PostNotifications], PermissionRequest);
    }

    protected override void OnDestroy()
    {
        if (Current == this)
            Current = null;
        base.OnDestroy();
    }

    /// <summary>Pide a Android permiso para capturar la pantalla. Se cancela si el usuario lo rechaza.</summary>
    public Task<Intent> RequestCaptureAsync()
    {
        var capture = new TaskCompletionSource<Intent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _capture = capture;
        RunOnUiThread(() =>
        {
            var manager = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
            StartActivityForResult(manager.CreateScreenCaptureIntent(), CaptureRequest);
        });
        return capture.Task;
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != CaptureRequest)
            return;
        if (resultCode == Result.Ok && data is not null)
            _capture?.TrySetResult(data);
        else
            _capture?.TrySetCanceled();
    }
}
