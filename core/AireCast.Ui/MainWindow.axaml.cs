// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;

namespace AireCast.Ui;

/// <summary>La ventana del escritorio. En Android, <see cref="MainView"/> ocupa toda la pantalla.</summary>
public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!View.Mirroring)
        {
            await View.ShutdownAsync();
            return;
        }
        e.Cancel = true;
        await View.ShutdownAsync();
        Close();
    }
}
