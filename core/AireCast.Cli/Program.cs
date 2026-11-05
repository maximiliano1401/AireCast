// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using AireCast.Core.Discovery;
using AireCast.Core.Net;
using AireCast.Core.Pairing;

return args switch
{
    ["discover", .. var rest] => await Discover(rest),
    ["verify", var target] => await Verify(target),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("Uso: airecast discover [segundos]");
    Console.Error.WriteLine("     airecast verify <nombre, id o dirección>");
    return 2;
}

static async Task<int> Discover(string[] args)
{
    double seconds = args.Length > 0 ? double.Parse(args[0], CultureInfo.InvariantCulture) : 5;
    var devices = await AirPlayBrowser.DiscoverAsync(TimeSpan.FromSeconds(seconds));
    foreach (AirPlayDevice device in devices)
    {
        Console.WriteLine($"{device.Name}  [{device.Model}]");
        Console.WriteLine($"  id: {device.Id}  dirección: {device.Address}:{device.Port}");
        Console.WriteLine($"  vídeo: {device.SupportsVideo}  audio: {device.HasRaop}  emparejar: {device.NeedsPairing}");
    }
    Console.WriteLine($"{devices.Count} dispositivo(s)");
    return 0;
}

static async Task<int> Verify(string target)
{
    var devices = await AirPlayBrowser.DiscoverAsync(TimeSpan.FromSeconds(4));
    AirPlayDevice? device = devices.FirstOrDefault(d => d.Identifiers.Contains(target, StringComparer.OrdinalIgnoreCase)
        || d.Address.ToString() == target
        || d.Name.Replace('\u00a0', ' ').Contains(target, StringComparison.OrdinalIgnoreCase)); // Apple usa espacios duros
    if (device is null)
    {
        Console.Error.WriteLine($"No se encontró «{target}»");
        return 1;
    }

    using var connection = await AirPlayConnection.ConnectAsync(device.Address, device.Port);
    var client = new AirPlayClient(connection);
    PrintInfo("GET /info", await client.GetInfoAsync());

    Credentials? credentials = CredentialStore.Find(device.Identifiers);
    if (credentials is null)
    {
        Console.Error.WriteLine($"No hay credenciales para «{device.Name}» en {CredentialStore.DefaultPath}: empareja primero");
        return 1;
    }
    Console.WriteLine($"Credenciales: {credentials.Kind}");
    await client.VerifyAsync(credentials);
    Console.WriteLine("pair-verify: correcto");
    if (credentials.Kind == PairingKind.HomeKit)
        PrintInfo("GET /info por el canal cifrado", await client.GetInfoAsync());
    return 0;
}

static void PrintInfo(string title, ReceiverInfo info)
{
    Console.WriteLine($"{title}: {info.Name} [{info.Model}] versión {info.SourceVersion}");
    foreach (ReceiverDisplay display in info.Displays)
        Console.WriteLine($"  pantalla {display.Width}x{display.Height}, máximo {display.MaxWidth}x{display.MaxHeight} a {display.MaxFps} fps");
}
