// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AireCast.Core.Discovery;
using AireCast.Core.Mirror;
using AireCast.Core.Pairing;
using AireCast.Ui;

namespace AireCast.App;

/// <summary>Windows y Linux: la pantalla se captura y se codifica con FFmpeg.</summary>
internal sealed class DesktopPlatform : IMirrorPlatform
{
    public IReadOnlyList<string> Resolutions { get; } = VideoOptions.Resolutions.Keys.ToList();

    public string? ShareNote => null;

    public bool CanChooseEncoder => true;

    public IDisposable BeginDiscovery() => new NoLock();

    public async Task MirrorAsync(AirPlayDevice device, Credentials? credentials, VideoOptions options, NetworkProfile network,
        MirrorCallbacks callbacks, CancellationToken ct)
    {
        await using IScreenSource source = ScreenCapture.Create(options);
        await MirrorController.RunAsync(device, credentials, source, MirrorController.FindFfmpeg(), callbacks, network: network, ct: ct);
    }

    private sealed class NoLock : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
