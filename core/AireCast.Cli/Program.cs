// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using AireCast.Core.Discovery;

return args switch
{
    ["discover", .. var rest] => await Discover(rest),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("Uso: airecast discover [segundos]");
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
