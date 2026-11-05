// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AireCast.Core.Discovery;
using AireCast.Core.Mirror;
using AireCast.Core.Pairing;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace AireCast.App;

/// <summary>Una fila de la lista de dispositivos.</summary>
public sealed record DeviceRow(AirPlayDevice Device, bool Paired)
{
    public string Name => Device.Name.Replace(' ', ' ');
    public string Details => $"{Device.Model} · {Device.Address}";
    public string State => !Device.SupportsVideo ? "Solo audio" : !Device.NeedsPairing ? "" : Paired ? "Emparejado" : "PIN";
    public bool HasState => State.Length > 0;
    public bool Ready => Device.SupportsVideo && (!Device.NeedsPairing || Paired);
}

public partial class MainWindow : Window
{
    private static readonly TimeSpan ScanTime = TimeSpan.FromSeconds(5);
    private static readonly Dictionary<string, string> ResolutionNames = new()
    {
        ["720p"] = "720p",
        ["1080p"] = "1080p",
        ["1440p"] = "1440p",
        ["2160p"] = "4K",
        ["native"] = "Nativa",
    };

    private const string AllResolutionsHint =
        "«Nativa» usa la de esta pantalla, sin escalar: es la más nítida. Por encima de 1080p (también la nativa si tu pantalla es mayor) hace falta un receptor que lo admita, como el Apple TV 4K.";
    private const string LegacyResolutionsHint = "Este Apple TV admite hasta 1080p.";

    private readonly CancellationTokenSource _closing = new();
    private AirPlayPairing? _pairing;
    private CancellationTokenSource? _mirror;
    private Task? _mirrorTask;
    private TaskCompletionSource<string>? _code;
    private bool _loadingSettings;

    public MainWindow()
    {
        InitializeComponent();
        LoadSettings();
        Opened += async (_, _) => await ScanAsync();
    }

    private DeviceRow? Selected => DeviceList.SelectedItem as DeviceRow;

    private bool Mirroring => _mirrorTask is { IsCompleted: false };

    private void LoadSettings()
    {
        _loadingSettings = true;
        AppSettings settings = AppSettings.Load();
        ShowResolutions(ResolutionNames.Keys, settings.Resolution, AllResolutionsHint);
        FpsBox.ItemsSource = VideoOptions.FpsChoices.ToList();
        FpsBox.SelectedItem = settings.Fps;
        _loadingSettings = false;
    }

    private void ShowResolutions(IEnumerable<string> resolutions, string selected, string hint)
    {
        var choices = resolutions.ToList();
        ResolutionBox.ItemsSource = choices.Select(resolution => ResolutionNames[resolution]).ToList();
        ResolutionBox.SelectedItem = ResolutionNames[choices.Contains(selected) ? selected : "1080p"];
        ResolutionHint.Text = hint;
    }

    /// <summary>Solo ofrece lo que admite el receptor elegido; la elección guardada se conserva para los demás.</summary>
    private void UpdateResolutions(DeviceRow? row)
    {
        _loadingSettings = true;
        string saved = AppSettings.Load().Resolution;
        if (row is { Device.UsesHomeKit: false })
            ShowResolutions(VideoOptions.LegacyResolutions, saved, LegacyResolutionsHint);
        else
            ShowResolutions(ResolutionNames.Keys, saved, AllResolutionsHint);
        _loadingSettings = false;
    }

    private string SelectedResolution => ResolutionNames.First(entry => entry.Value == (string?)ResolutionBox.SelectedItem).Key;

    private int SelectedFps => (int)FpsBox.SelectedItem!;

    private void OnSettingChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || ResolutionBox.SelectedItem is null || FpsBox.SelectedItem is null)
            return;
        new AppSettings(SelectedResolution, SelectedFps).Save();
        if (Mirroring)
            SetStatus("Los cambios se aplicarán la próxima vez que compartas la pantalla.");
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void Log(string text)
    {
        string log = (LogBox.Text ?? "") + text + "\n";
        LogBox.Text = log.Length > 100000 ? log[^50000..] : log;
        LogBox.CaretIndex = LogBox.Text.Length;
    }

    private void LogFromAnyThread(string text) => Dispatcher.UIThread.Post(() => Log(text));

    // --- Búsqueda -----------------------------------------------------------------------------

    private async void OnScan(object? sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        ScanButton.IsEnabled = false;
        ScanButton.Content = "Buscando…";
        EmptyText.Text = "Asegúrate de que la TV está encendida y en la misma red Wi-Fi.";
        SetStatus("Buscando dispositivos en tu red…");
        try
        {
            string? selected = Selected?.Device.Id;
            var devices = await AirPlayBrowser.DiscoverAsync(ScanTime, _closing.Token);
            var rows = devices.Select(device => new DeviceRow(device, CredentialStore.Find(device.Identifiers) is not null)).ToList();
            DeviceList.ItemsSource = rows;
            DeviceList.SelectedItem = rows.FirstOrDefault(row => row.Device.Id == selected);
            EmptyText.Text = rows.Count == 0 ? "No hay dispositivos. Enciende la TV, comprueba que está en la misma red y vuelve a pulsar «Buscar»." : "";
            SetStatus(rows.Count == 1 ? "1 dispositivo disponible." : $"{rows.Count} dispositivos disponibles.");
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            SetStatus($"No se pudo buscar dispositivos: {ex.Message}");
        }
        ScanButton.IsEnabled = true;
        ScanButton.Content = "Buscar";
    }

    private async void OnDeviceSelected(object? sender, SelectionChangedEventArgs e)
    {
        DeviceRow? row = Selected;
        TargetText.Text = row is null ? "Elige un dispositivo" : row.Ready ? row.Name : $"{row.Name} · sin emparejar";
        ShareButton.IsEnabled = row is { Ready: true } && !Mirroring;
        UpdateResolutions(row);
        ClosePairing();
        if (row is { Device.NeedsPairing: true, Paired: false })
            await BeginPairingAsync(row);
    }

    // --- Emparejamiento -----------------------------------------------------------------------

    private async Task BeginPairingAsync(DeviceRow row)
    {
        PairPanel.IsVisible = true;
        PairTitle.Text = $"Emparejar «{row.Name}»";
        PinBox.Text = "";
        SetStatus($"Pidiendo el PIN a «{row.Name}»…");
        try
        {
            _pairing = await AirPlayPairing.BeginAsync(row.Device, _closing.Token);
            SetStatus($"Escribe el PIN de 4 dígitos que aparece en «{row.Name}».");
            PinBox.Focus();
        }
        catch (Exception ex) when (ex is IOException or SocketException or PairingException)
        {
            SetStatus($"No se pudo iniciar el emparejamiento: {ex.Message}");
        }
    }

    private void ClosePairing()
    {
        _pairing?.Dispose();
        _pairing = null;
        PairPanel.IsVisible = false;
    }

    private async void OnPair(object? sender, RoutedEventArgs e) => await FinishPairingAsync();

    private async void OnPinKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            await FinishPairingAsync();
    }

    private async Task FinishPairingAsync()
    {
        if (_pairing is null || Selected is not { } row)
            return;
        PinButton.IsEnabled = false;
        SetStatus($"Comprobando el PIN en «{row.Name}»…");
        try
        {
            Credentials credentials = await _pairing.FinishAsync(PinBox.Text ?? "", _closing.Token);
            CredentialStore.Save(row.Device.Id, credentials);
            ClosePairing();
            SetStatus($"«{row.Name}» emparejado. Ya puedes compartir la pantalla.");
            await ScanAsync();
        }
        catch (Exception ex) when (ex is IOException or SocketException or PairingException)
        {
            ClosePairing();
            SetStatus($"No se pudo emparejar: {ex.Message}. Vuelve a tocar la TV para pedir otro PIN.");
            DeviceList.SelectedItem = null;
        }
        finally
        {
            PinButton.IsEnabled = true;
        }
    }

    // --- Código en pantalla -------------------------------------------------------------------

    private Task<string> AskCodeAsync(bool retry, CancellationToken ct)
    {
        var code = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => code.TrySetCanceled(ct));
        _code = code;
        Dispatcher.UIThread.Post(() =>
        {
            CodePanel.IsVisible = true;
            CodeBox.Text = "";
            CodeText.Text = retry
                ? "El código no era correcto. Escribe el nuevo código de 4 dígitos que muestra la TV."
                : "Mira la TV y escribe el código de 4 dígitos que aparece.";
            SetStatus(retry ? "Código incorrecto. Escribe el nuevo código de la TV." : "Mira la TV y escribe el código que aparece.");
            CodeBox.Focus();
        });
        return code.Task;
    }

    private void OnCode(object? sender, RoutedEventArgs e) => SendCode();

    private void OnCodeKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            SendCode();
    }

    private void SendCode()
    {
        string code = (CodeBox.Text ?? "").Trim();
        if (code.Length != 4)
        {
            SetStatus("Escribe el código de 4 dígitos que aparece en la TV.");
            return;
        }
        CodePanel.IsVisible = false;
        SetStatus("Conectando con la TV…");
        _code?.TrySetResult(code);
    }

    // --- Duplicado ----------------------------------------------------------------------------

    private void OnShare(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } row || Mirroring)
            return;
        Credentials? credentials = CredentialStore.Find(row.Device.Identifiers);
        if (credentials is null)
        {
            SetStatus("Empareja la TV con su PIN para continuar.");
            return;
        }

        VideoOptions options = VideoOptions.Create(SelectedResolution, SelectedFps);
        ShareButton.IsEnabled = false;
        NowPlaying.IsVisible = true;
        NowPlayingTarget.Text = $"en «{row.Name}»";
        SetStatus($"Compartiendo la pantalla en «{row.Name}»…");
        _mirror = new CancellationTokenSource();
        _mirrorTask = RunMirrorAsync(row, credentials, options, _mirror);
    }

    private async Task RunMirrorAsync(DeviceRow row, Credentials credentials, VideoOptions options, CancellationTokenSource stop)
    {
        string result;
        try
        {
            var callbacks = new MirrorCallbacks
            {
                AskCode = AskCodeAsync,
                Log = LogFromAnyThread,
                Started = () => Dispatcher.UIThread.Post(() => SetStatus($"Compartiendo la pantalla en «{row.Name}».")),
            };
            await Task.Run(async () =>
            {
                await using IScreenSource source = ScreenCapture.Create(options);
                await MirrorController.RunAsync(row.Device, credentials, source, MirrorController.FindFfmpeg(), callbacks, ct: stop.Token);
            });
            result = "La transmisión ha terminado.";
        }
        catch (OperationCanceledException)
        {
            result = "Transmisión detenida.";
        }
        catch (Exception ex) when (ex is IOException or SocketException or PairingException or Win32Exception
            or PlatformNotSupportedException or FormatException)
        {
            result = $"Se detuvo la transmisión: {ex.Message}";
            Log(result);
        }
        CodePanel.IsVisible = false;
        NowPlaying.IsVisible = false;
        ShareButton.IsEnabled = Selected is { Ready: true };
        SetStatus(result);
        stop.Dispose();
    }

    private void OnStop(object? sender, RoutedEventArgs e)
    {
        SetStatus("Deteniendo…");
        _mirror?.Cancel();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!Mirroring)
        {
            _closing.Cancel();
            return;
        }
        // Se espera al TEARDOWN y a que se restaure el audio del PC, y después se cierra.
        e.Cancel = true;
        _mirror?.Cancel();
        await _mirrorTask!;
        Close();
    }
}
