// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Linq;
using AireCast.Core.Audio;
using Avalonia;

namespace AireCast.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // La captura de audio de Linux es este mismo ejecutable, como proceso aparte (ver LinuxAudioCap).
        if (args.FirstOrDefault() == LinuxAudioCap.Argument)
            return LinuxAudioCap.Run(args[1..]);
        Ui.App.Platform = new DesktopPlatform();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Ui.App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
