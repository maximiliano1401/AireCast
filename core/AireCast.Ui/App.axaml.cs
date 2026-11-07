// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AireCast.Ui;

public partial class App : Application
{
    /// <summary>Lo propio del sistema. Cada ejecutable lo asigna antes de arrancar Avalonia.</summary>
    public static IMirrorPlatform Platform { get; set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        else if (ApplicationLifetime is IActivityApplicationLifetime activity)
            activity.MainViewFactory = () => new MainView();
        base.OnFrameworkInitializationCompleted();
    }
}
