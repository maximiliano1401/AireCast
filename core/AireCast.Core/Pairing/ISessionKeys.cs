// SPDX-License-Identifier: GPL-3.0-or-later
namespace AireCast.Core.Pairing;

/// <summary>El secreto de una sesión HomeKit: de él salen las claves del canal RTSP, los eventos y los datos.</summary>
public interface ISessionKeys
{
    byte[] DeriveKey(string salt, string info);
}
