using System.Net;
using System.Net.Sockets;
using Berth.Engine.Models;

namespace Berth.Presentation;

/// <summary>Formats a container's ports for the Containers page.</summary>
public static class PortFormat
{
    /// <summary>
    /// One entry per distinct port: published ports first, by public then private port, then unpublished ones by
    /// private port. A published TCP port reads "8080:80" and links to http://localhost:8080 when bound on every
    /// address, loopback or no address, or to its own address otherwise. The same port bound on both IPv4 and IPv6
    /// shows once. Other protocols show their type ("5353:53/udp") and have no link; an unpublished port reads "80/tcp".
    /// </summary>
    public static IReadOnlyList<PortLink> Links(IReadOnlyList<PortMapping> ports) =>
    [
        .. ports
            .OrderBy(p => p.PublicPort is null)
            .ThenBy(p => p.PublicPort)
            .ThenBy(p => p.PrivatePort)
            .ThenBy(p => p.Type, StringComparer.Ordinal)
            .Select(Link)
            .Distinct(),
    ];

    private static PortLink Link(PortMapping port)
    {
        if (port.PublicPort is not { } publicPort)
        {
            return new PortLink($"{port.PrivatePort}/{port.Type}", null);
        }

        if (!string.Equals(port.Type, "tcp", StringComparison.OrdinalIgnoreCase))
        {
            return new PortLink($"{publicPort}:{port.PrivatePort}/{port.Type}", null);
        }

        return new PortLink($"{publicPort}:{port.PrivatePort}", Url(port.Ip, publicPort));
    }

    private static Uri? Url(string? ip, int port)
    {
        return Uri.TryCreate($"http://{Host(ip)}:{port}", UriKind.Absolute, out var url) ? url : null;
    }

    private static string Host(string? ip)
    {
        if (string.IsNullOrEmpty(ip) || !IPAddress.TryParse(ip, out var address))
        {
            return string.IsNullOrEmpty(ip) ? "localhost" : ip;
        }

        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback))
        {
            return "localhost";
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip;
    }
}
