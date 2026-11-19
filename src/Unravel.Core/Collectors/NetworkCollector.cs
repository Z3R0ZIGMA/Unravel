using System.Globalization;
using System.Net;
using Unravel.Core.Infrastructure;
using Unravel.Core.Models;

namespace Unravel.Core.Collectors;

/// <summary>Network adapters (MSFT_NetAdapter + IP configuration) and the current Wi-Fi link (netsh).</summary>
public sealed class NetworkCollector : CollectorBase<NetworkInfo>
{
    public override string Section => Sections.Network;

    protected override NetworkInfo Collect(CollectContext ctx, CollectorNotes notes, CancellationToken ct)
    {
        var adapters = TryQuery(ctx, notes, "network adapters", WmiNamespaces.StandardCimv2, "MSFT_NetAdapter",
        [
            "Name", "InterfaceDescription", "InterfaceIndex", "PermanentAddress", "Speed", "MediaConnectState",
            "NdisPhysicalMedium", "Virtual", "HardwareInterface",
        ]);
        ct.ThrowIfCancellationRequested();

        var configs = TryQuery(ctx, notes, "IP configuration", WmiNamespaces.Cimv2, "Win32_NetworkAdapterConfiguration",
                ["InterfaceIndex", "IPAddress", "DefaultIPGateway", "DNSServerSearchOrder", "DHCPEnabled", "MACAddress", "Description"],
                "IPEnabled = TRUE")
            .Where(c => c.Int("InterfaceIndex") is not null)
            .GroupBy(c => c.Int("InterfaceIndex")!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var list = adapters.Select(a =>
        {
            var index = a.Int("InterfaceIndex");
            var config = index is { } i ? configs.GetValueOrDefault(i) : null;
            var addresses = Strings(config?["IPAddress"]);
            var isVirtual = a.Bool("Virtual") == true || a.Bool("HardwareInterface") == false;
            return new NetworkAdapterInfo
            {
                Name = a.RawString("Name"),
                Description = a.RawString("InterfaceDescription"),
                Kind = NetworkRules.Kind(a.Int("NdisPhysicalMedium"), isVirtual, a.RawString("InterfaceDescription")),
                MacAddress = NetworkRules.Mac(a.RawString("PermanentAddress") ?? config?.RawString("MACAddress")),
                Connected = a.Int("MediaConnectState") == 1,
                // Speed is bits/s; a disconnected adapter reports its maximum, so it is only shown when connected.
                LinkSpeedBps = a.Int("MediaConnectState") == 1 && a.ULong("Speed") is > 0 and < ulong.MaxValue and var s ? s : null,
                IPv4 = addresses.Where(x => IPAddress.TryParse(x, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToList(),
                IPv6 = addresses.Where(x => x.Contains(':', StringComparison.Ordinal)).ToList(),
                Gateways = Strings(config?["DefaultIPGateway"]),
                DnsServers = Strings(config?["DNSServerSearchOrder"]),
                Dhcp = config?.Bool("DHCPEnabled"),
                IsVirtual = isVirtual,
            };
        })
        // Physical adapters first, connected first.
        .OrderBy(a => a.IsVirtual)
        .ThenByDescending(a => a.Connected)
        .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

        if (list.Count == 0 && configs.Count == 0)
        {
            throw new InvalidOperationException(notes.Warnings.LastOrDefault() ?? "WMI returned no network adapters");
        }

        WifiInfo? wifi = null;
        if (list.Any(a => a.Kind == "Wi-Fi"))
        {
            var output = ctx.Source.RunCommand("netsh", "wlan show interfaces");
            wifi = output is null ? null : NetworkRules.ParseWlan(output);
        }

        return new NetworkInfo { Adapters = list, Wifi = wifi };
    }

    private static List<string> Strings(object? value) => value switch
    {
        string s => [s],
        object[] items => items.OfType<string>().Where(s => s.Length > 0).ToList(),
        _ => [],
    };
}

public static class NetworkRules
{
    /// <summary>NdisPhysicalMedium: 9 = 802.11 (Wi-Fi), 14 = 802.3 (Ethernet), 10 = Bluetooth, 8 = WWAN.</summary>
    public static string Kind(int? medium, bool isVirtual, string? description)
    {
        if (isVirtual)
        {
            return "Virtual";
        }

        return medium switch
        {
            9 => "Wi-Fi",
            14 => "Ethernet",
            10 => "Bluetooth",
            8 or 12 => "Mobile broadband",
            _ when description?.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) == true
                   || description?.Contains("Wireless", StringComparison.OrdinalIgnoreCase) == true => "Wi-Fi",
            _ when description?.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) == true => "Bluetooth",
            0 when description?.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) == true => "Ethernet",
            _ => "Other",
        };
    }

    /// <summary>"00-1A-2B-3C-4D-5E" or "001A2B3C4D5E" to "00:1A:2B:3C:4D:5E".</summary>
    public static string? Mac(string? mac)
    {
        if (mac is null)
        {
            return null;
        }

        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return hex.Length == 12 ? string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))) : mac;
    }

    /// <summary>"1.2 Gbps", "100 Mbps".</summary>
    public static string LinkSpeed(ulong bps) => bps >= 1_000_000_000
        ? $"{(bps / 1e9).ToString("0.#", CultureInfo.CurrentCulture)} Gbps"
        : $"{(bps / 1e6).ToString("0", CultureInfo.CurrentCulture)} Mbps";

    /// <summary>
    /// Parses `netsh wlan show interfaces` (first interface). The labels are English; on other display languages only
    /// the SSID and the percentage signal are recognised.
    /// </summary>
    public static WifiInfo ParseWlan(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? signal = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(" : ", StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 3)..].Trim();
            if (values.ContainsKey(key))
            {
                // A second interface starts: only the first is reported.
                if (key.Equals("Name", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                continue;
            }

            values[key] = value;
            if (signal is null && value.EndsWith('%') && int.TryParse(value.TrimEnd('%'), out _))
            {
                signal = value;
            }
        }

        string? Get(string key) => values.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        var note = output.Contains("location", StringComparison.OrdinalIgnoreCase) && Get("SSID") is null
            ? "Windows hides the Wi-Fi name until location access is allowed for desktop apps (Settings > Privacy & security > Location)."
            : null;

        return new WifiInfo
        {
            Ssid = Get("SSID"),
            SignalPercent = signal is not null && int.TryParse(signal.TrimEnd('%'), out var s) ? s : null,
            RadioType = Get("Radio type"),
            Band = Get("Band"),
            Channel = Get("Channel"),
            ReceiveRateMbps = Get("Receive rate (Mbps)"),
            TransmitRateMbps = Get("Transmit rate (Mbps)"),
            State = Get("State"),
            Note = note,
        };
    }
}
