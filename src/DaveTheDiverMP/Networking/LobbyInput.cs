using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DaveTheDiverMP;

internal static class LobbyInput
{
    internal static bool TryValidate(
        SessionRole role,
        string address,
        string portText,
        out string normalizedAddress,
        out int port,
        out string error)
    {
        normalizedAddress = string.IsNullOrWhiteSpace(address) ? "127.0.0.1" : address.Trim();
        port = 0;
        error = string.Empty;
        if (role is not SessionRole.Host and not SessionRole.Client)
        {
            error = "Choose Host or Join";
            return false;
        }
        if (!int.TryParse(portText, out port) || port is < 1024 or > 65535)
        {
            error = "Port must be 1024-65535";
            return false;
        }
        if (role == SessionRole.Host)
            return true;
        if (!IPAddress.TryParse((address ?? string.Empty).Trim(), out var parsed) ||
            parsed.AddressFamily != AddressFamily.InterNetwork ||
            !IsUsableRemote(parsed))
        {
            error = "Enter a usable IPv4 address";
            return false;
        }
        normalizedAddress = parsed.ToString();
        return true;
    }

    internal static string Status(
        SessionRole role,
        bool running,
        bool connected,
        string remoteName)
    {
        if (role == SessionRole.Offline || !running)
            return "Offline";
        if (connected)
            return $"Connected: {Protocol.NormalizePlayerName(remoteName)}";
        return role == SessionRole.Host ? "Hosting - waiting for player" : "Connecting...";
    }

    internal static string FindLanAddress()
    {
        string fallback = null;
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up ||
                    network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel ||
                    IsVirtual(network))
                    continue;

                var properties = network.GetIPProperties();
                foreach (var entry in properties.UnicastAddresses)
                {
                    var address = entry.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork ||
                        IPAddress.IsLoopback(address) || IsLinkLocal(address))
                        continue;
                    fallback ??= address.ToString();
                    if (IsPrivate(address))
                        return address.ToString();
                }
            }
        }
        catch (NetworkInformationException)
        {
        }
        return fallback ?? "unknown";
    }

    private static bool IsUsableRemote(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return !address.Equals(IPAddress.Any) &&
            !address.Equals(IPAddress.Broadcast) &&
            bytes[0] != 0 && bytes[0] is < 224 or > 239;
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
            bytes[0] == 192 && bytes[1] == 168 ||
            bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
    }

    private static bool IsVirtual(NetworkInterface network)
    {
        var identity = $"{network.Name} {network.Description}".ToLowerInvariant();
        return identity.Contains("virtual") || identity.Contains("hyper-v") ||
            identity.Contains("vpn") || identity.Contains("tunnel") ||
            identity.Contains("wireguard") || identity.Contains("tailscale") ||
            identity.Contains("hamachi");
    }

    internal static void SelfTest()
    {
        if (!TryValidate(SessionRole.Host, string.Empty, "27777", out _, out var port, out _) ||
            port != 27777)
            throw new InvalidOperationException("Lobby host validation failed");
        if (!TryValidate(SessionRole.Client, " 127.0.0.1 ", "27777", out var address, out _, out _) ||
            address != "127.0.0.1")
            throw new InvalidOperationException("Lobby client validation failed");
        if (TryValidate(SessionRole.Client, "localhost", "27777", out _, out _, out _) ||
            TryValidate(SessionRole.Client, "0.0.0.0", "27777", out _, out _, out _) ||
            TryValidate(SessionRole.Client, "255.255.255.255", "27777", out _, out _, out _) ||
            TryValidate(SessionRole.Client, "239.1.2.3", "27777", out _, out _, out _) ||
            TryValidate(SessionRole.Host, string.Empty, "80", out _, out _, out _))
            throw new InvalidOperationException("Lobby accepted invalid endpoint");
        if (Status(SessionRole.Host, true, true, "Peer") != "Connected: Peer")
            throw new InvalidOperationException("Lobby status failed");
    }
}
