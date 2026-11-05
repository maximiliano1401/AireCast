// SPDX-License-Identifier: GPL-3.0-or-later
using AireCast.Core.Discovery;
using AireCast.Core.Net;

namespace AireCast.Core.Pairing;

/// <summary>Emparejamiento con PIN: <see cref="BeginAsync"/> lo muestra en la TV y <see cref="FinishAsync"/> lo comprueba.</summary>
public sealed class AirPlayPairing : IDisposable
{
    private readonly AirPlayConnection _connection;
    private readonly AirPlayClient _client;
    private readonly bool _homeKit;
    private HomeKitPairSetup? _setup;
    private byte[] _message2 = [];

    private AirPlayPairing(AirPlayConnection connection, bool homeKit)
    {
        _connection = connection;
        _client = new AirPlayClient(connection);
        _homeKit = homeKit;
    }

    public static async Task<AirPlayPairing> BeginAsync(AirPlayDevice device, CancellationToken ct = default)
    {
        AirPlayConnection connection = await AirPlayConnection.ConnectAsync(device.Address, device.Port, ct);
        var pairing = new AirPlayPairing(connection, device.UsesHomeKit);
        try
        {
            await pairing._client.ShowPinAsync(pairing._homeKit, ct);
            if (pairing._homeKit)
            {
                pairing._setup = new HomeKitPairSetup();
                pairing._message2 = await pairing._client.PairSetupAsync(HomeKitPairSetup.Message1(), ct);
            }
            return pairing;
        }
        catch
        {
            pairing.Dispose();
            throw;
        }
    }

    /// <exception cref="PairingException">El PIN es incorrecto o la TV rechazó el emparejamiento.</exception>
    public async Task<Credentials> FinishAsync(string pin, CancellationToken ct = default)
    {
        pin = pin.Trim().PadLeft(4, '0');
        if (_setup is not null)
        {
            byte[] message4 = await _client.PairSetupAsync(_setup.Message3(_message2, pin), ct);
            return _setup.Finish(await _client.PairSetupAsync(_setup.Message5(message4), ct));
        }

        var legacy = new LegacyPairSetup();
        var challenge = await _client.PairSetupPinAsync(new() { ["method"] = "pin", ["user"] = legacy.User }, ct);
        if (challenge.GetValueOrDefault("pk") is not byte[] serverPublic || challenge.GetValueOrDefault("salt") is not byte[] salt)
            throw new PairingException("Respuesta de emparejamiento incompleta");
        var (publicKey, proof) = legacy.Respond(salt, serverPublic, pin);
        await _client.PairSetupPinAsync(new() { ["pk"] = publicKey, ["proof"] = proof }, ct);
        var (epk, tag) = legacy.EncryptPublicKey();
        await _client.PairSetupPinAsync(new() { ["epk"] = epk, ["authTag"] = tag }, ct);
        return legacy.Credentials;
    }

    public void Dispose() => _connection.Dispose();
}
