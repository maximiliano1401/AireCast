// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;

namespace AireCast.Core.Discovery;

/// <summary>Registros mDNS recibidos, y cómo convertirlos en dispositivos. Admite recibir y consultar a la vez.</summary>
public sealed class RecordSet
{
    private readonly object _lock = new();
    private readonly HashSet<PtrRecord> _pointers = [];
    private readonly Dictionary<string, SrvRecord> _services = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TxtRecord> _texts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPAddress> _addresses = new(StringComparer.OrdinalIgnoreCase);

    public void Add(IEnumerable<DnsRecord> records)
    {
        lock (_lock)
        {
            foreach (DnsRecord record in records)
            {
                switch (record)
                {
                    case PtrRecord pointer:
                        _pointers.Add(pointer);
                        break;
                    case SrvRecord service:
                        _services[service.Name] = service;
                        break;
                    case TxtRecord text:
                        _texts[text.Name] = text;
                        break;
                    case AddressRecord { Address.AddressFamily: AddressFamily.InterNetwork } address:
                        _addresses[address.Name] = address.Address;
                        break;
                }
            }
        }
    }

    /// <summary>Las preguntas de los tipos de servicio y de lo que aún falta de cada instancia encontrada.</summary>
    public List<(string Name, DnsType Type)> PendingQuestions(IEnumerable<string> serviceTypes)
    {
        var questions = serviceTypes.Select(type => (type, DnsType.Ptr)).ToList();
        lock (_lock)
        {
            foreach (PtrRecord pointer in _pointers)
            {
                if (!_services.TryGetValue(pointer.Target, out SrvRecord? service))
                    questions.Add((pointer.Target, DnsType.Srv));
                else if (!_addresses.ContainsKey(service.Target))
                    questions.Add((service.Target, DnsType.A));
                if (!_texts.ContainsKey(pointer.Target))
                    questions.Add((pointer.Target, DnsType.Txt));
            }
        }
        return questions;
    }

    /// <summary>Un dispositivo por cada servicio AirPlay completo, con los datos de RAOP y DMAP de su misma dirección.</summary>
    public List<AirPlayDevice> Devices(string airPlayType, string raopType, string dmapType)
    {
        lock (_lock)
        {
            var devices = new List<AirPlayDevice>();
            foreach (PtrRecord pointer in _pointers.Where(p => Is(p, airPlayType)))
            {
                if (!_services.TryGetValue(pointer.Target, out SrvRecord? service)
                    || !_addresses.TryGetValue(service.Target, out IPAddress? address)
                    || !_texts.TryGetValue(pointer.Target, out TxtRecord? text))
                    continue;

                var identifiers = new List<string>();
                identifiers.AddRange(Instances(dmapType, address));
                if (text.Values.TryGetValue("deviceid", out string? deviceId))
                    identifiers.Add(deviceId);
                if (identifiers.Count == 0)
                    continue;

                devices.Add(new AirPlayDevice
                {
                    Id = identifiers[0],
                    Identifiers = identifiers,
                    Name = InstanceName(pointer.Target, airPlayType),
                    Address = address,
                    Port = service.Port,
                    Properties = text.Values,
                    HasRaop = Instances(raopType, address).Any(),
                });
            }
            return devices.OrderBy(d => d.Name, StringComparer.CurrentCulture).ToList();
        }
    }

    private static bool Is(PtrRecord pointer, string type) => pointer.Name.Equals(type, StringComparison.OrdinalIgnoreCase);

    /// <summary>Nombres de las instancias de ese tipo de servicio que hay en esa dirección.</summary>
    private IEnumerable<string> Instances(string type, IPAddress address) =>
        _pointers.Where(p => Is(p, type)
                && _services.TryGetValue(p.Target, out SrvRecord? service)
                && _addresses.TryGetValue(service.Target, out IPAddress? found) && found.Equals(address))
            .Select(p => InstanceName(p.Target, type));

    private static string InstanceName(string instance, string type) =>
        instance.EndsWith("." + type, StringComparison.OrdinalIgnoreCase) ? instance[..^(type.Length + 1)] : instance;
}
