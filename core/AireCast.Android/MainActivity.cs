// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Sockets;
using AireCast.Core.Discovery;
using AireCast.Core.Pairing;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Media.Projection;
using Android.Net.Wifi;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;

namespace AireCast.Android;

/// <summary>Prototipo: busca receptores, los empareja y comparte la pantalla.</summary>
[Activity(Label = "AireCast", MainLauncher = true)]
public sealed class MainActivity : Activity
{
    private const int CaptureRequest = 1;
    private const int PermissionRequest = 2;
    private static readonly TimeSpan ScanTime = TimeSpan.FromSeconds(5);

    private readonly List<AirPlayDevice> _devices = [];
    private ArrayAdapter<string>? _adapter;
    private TextView? _status;
    private Button? _scan;
    private Button? _share;
    private Button? _stop;
    private AirPlayDevice? _selected;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        BuildInterface();
        RequestPermissions([global::Android.Manifest.Permission.RecordAudio, global::Android.Manifest.Permission.PostNotifications], PermissionRequest);
        MirrorBridge.Status = text => RunOnUiThread(() => _status!.Text = text);
        MirrorBridge.Finished = () => RunOnUiThread(() => SetMirroring(false));
        MirrorBridge.AskCode = AskCodeAsync;
        _ = ScanAsync();
    }

    private void BuildInterface()
    {
        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        layout.SetPadding(40, 120, 40, 40);
        layout.SetBackgroundColor(Color.ParseColor("#070B14"));

        var title = new TextView(this) { Text = "AireCast", TextSize = 26 };
        title.SetTextColor(Color.White);
        _status = new TextView(this) { Text = "", TextSize = 14 };
        _status.SetPadding(0, 20, 0, 20);
        _status.SetTextColor(Color.ParseColor("#A2AFC6"));
        _scan = new Button(this) { Text = "Buscar" };
        _scan.Click += async (_, _) => await ScanAsync();

        _adapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleListItemSingleChoice);
        var list = new ListView(this) { Adapter = _adapter, ChoiceMode = ChoiceMode.Single };
        list.ItemClick += (_, e) => Select(_devices[e.Position]);

        _share = new Button(this) { Text = "Compartir pantalla", Enabled = false };
        _share.Click += (_, _) => StartCapture();
        _stop = new Button(this) { Text = "Detener", Visibility = ViewStates.Gone };
        _stop.Click += (_, _) => StartService(new Intent(this, typeof(MirrorService)).SetAction(MirrorService.ActionStop));

        layout.AddView(title);
        layout.AddView(_status);
        layout.AddView(_scan);
        layout.AddView(list, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
        layout.AddView(_share);
        layout.AddView(_stop);
        SetContentView(layout);
    }

    private async Task ScanAsync()
    {
        _scan!.Enabled = false;
        _status!.Text = "Buscando dispositivos en tu red…";
        // Android descarta el multicast entrante si no se pide este permiso temporal.
        var wifi = (WifiManager)GetSystemService(WifiService)!;
        WifiManager.MulticastLock multicast = wifi.CreateMulticastLock("AireCast")!;
        multicast.Acquire();
        try
        {
            var devices = await AirPlayBrowser.DiscoverAsync(ScanTime);
            _devices.Clear();
            _devices.AddRange(devices.Where(device => device.SupportsVideo));
            _adapter!.Clear();
            _adapter.AddAll(_devices.Select(Describe).ToList());
            _status.Text = $"{_devices.Count} dispositivo(s).";
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            _status.Text = $"No se pudo buscar: {ex.Message}";
        }
        finally
        {
            multicast.Release();
            _scan.Enabled = true;
        }
    }

    private static string Describe(AirPlayDevice device)
    {
        string state = CredentialStore.Find(device.Identifiers) is not null ? "Emparejado" : device.NeedsPairing ? "PIN" : "";
        return $"{device.Name.Replace(' ', ' ')}  [{device.Model}]  {state}";
    }

    private async void Select(AirPlayDevice device)
    {
        _selected = device;
        Credentials? credentials = CredentialStore.Find(device.Identifiers);
        _share!.Enabled = credentials is not null || !device.NeedsPairing;
        if (credentials is null && device.NeedsPairing)
            await PairAsync(device);
    }

    private async Task PairAsync(AirPlayDevice device)
    {
        _status!.Text = "Pidiendo el PIN a la TV…";
        try
        {
            using AirPlayPairing pairing = await AirPlayPairing.BeginAsync(device);
            string pin = await AskTextAsync("Escribe el PIN que muestra la TV");
            Credentials credentials = await pairing.FinishAsync(pin);
            CredentialStore.Save(device.Id, credentials);
            _status.Text = "Emparejado. Ya puedes compartir la pantalla.";
            _share!.Enabled = true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or PairingException)
        {
            _status.Text = $"No se pudo emparejar: {ex.Message}";
        }
    }

    private Task<string> AskCodeAsync(bool retry, CancellationToken ct)
    {
        var code = new TaskCompletionSource<string>();
        ct.Register(() => code.TrySetCanceled(ct));
        RunOnUiThread(async () => code.TrySetResult(await AskTextAsync(retry ? "Código incorrecto. Escribe el nuevo código" : "Escribe el código que muestra la TV")));
        return code.Task;
    }

    private Task<string> AskTextAsync(string message)
    {
        var result = new TaskCompletionSource<string>();
        var input = new EditText(this) { InputType = InputTypes.ClassNumber };
        new AlertDialog.Builder(this)
            .SetTitle("AireCast")!
            .SetMessage(message)!
            .SetView(input)!
            .SetCancelable(false)!
            .SetPositiveButton("Aceptar", (_, _) => result.TrySetResult(input.Text ?? ""))!
            .Show();
        return result.Task;
    }

    private void StartCapture()
    {
        var manager = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
        StartActivityForResult(manager.CreateScreenCaptureIntent(), CaptureRequest);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != CaptureRequest || resultCode != Result.Ok || data is null || _selected is null)
            return;
        MirrorBridge.Device = _selected;
        MirrorBridge.Credentials = CredentialStore.Find(_selected.Identifiers);
        if (MirrorBridge.Credentials is null)
        {
            _status!.Text = "Empareja la TV antes de compartir.";
            return;
        }
        SetMirroring(true);
        var intent = new Intent(this, typeof(MirrorService));
        intent.PutExtra(MirrorService.ResultCode, (int)resultCode);
        intent.PutExtra(MirrorService.ResultData, data);
        StartForegroundService(intent);
    }

    private void SetMirroring(bool mirroring)
    {
        _share!.Visibility = mirroring ? ViewStates.Gone : ViewStates.Visible;
        _stop!.Visibility = mirroring ? ViewStates.Visible : ViewStates.Gone;
    }
}
