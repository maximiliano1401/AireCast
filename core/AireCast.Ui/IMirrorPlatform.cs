// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AireCast.Core.Discovery;
using AireCast.Core.Mirror;
using AireCast.Core.Pairing;

namespace AireCast.Ui;

/// <summary>Lo que cada sistema hace a su manera: la interfaz es común, la captura de pantalla no.</summary>
public interface IMirrorPlatform
{
    /// <summary>Las resoluciones que se ofrecen, de las de <see cref="VideoOptions.Resolutions"/>.</summary>
    IReadOnlyList<string> Resolutions { get; }

    /// <summary>Un aviso bajo el botón de compartir, si este sistema tiene algo que advertir.</summary>
    string? ShareNote { get; }

    /// <summary>Prepara la búsqueda de receptores; se libera al terminarla.</summary>
    IDisposable BeginDiscovery();

    /// <summary>Duplica la pantalla hasta que se cancela o falla.</summary>
    Task MirrorAsync(AirPlayDevice device, Credentials? credentials, VideoOptions options, NetworkProfile network,
        MirrorCallbacks callbacks, CancellationToken ct);
}
