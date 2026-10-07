using System.Net;
using System.Net.Sockets;

namespace Dcms.Shared.Hosting;

/// <summary>
/// Outbound connections to the public internet only, for fetching a URL that came from outside —
/// a plugin importing a file whose address a third-party API handed it.
///
/// <para>The check is in the connect callback, not on the URL: it runs for every connection the
/// client opens, so a redirect to an internal host, or a name that resolves to one (DNS
/// rebinding), is refused the same as a literal internal address. Checking the URL string alone
/// would let <c>https://vault:8200</c>, a metadata address or a 302 into the compose network
/// through.</para>
/// </summary>
public static class PublicEgress
{
    /// <summary>
    /// A handler whose connections reach public addresses only. <paramref name="allowPrivate"/> is
    /// for tests, whose stub servers listen on loopback.
    /// </summary>
    public static SocketsHttpHandler Handler(bool allowPrivate) => new()
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 3,
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var target = addresses.FirstOrDefault(a => allowPrivate || IsPublic(a))
                ?? throw new HttpRequestException(
                    $"Refusing to connect to {context.DnsEndPoint.Host}: it does not resolve to a public address.");

            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    /// <summary>Routable on the public internet: not loopback, private, link-local, CGNAT, multicast or unspecified.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None))
        {
            return false;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var first = address.GetAddressBytes()[0];
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                     || (first & 0xFE) == 0xFC); // fc00::/7, unique local
        }
        var b = address.GetAddressBytes();
        return !(b[0] == 0                                   // 0.0.0.0/8
                 || b[0] == 10                               // 10/8
                 || (b[0] == 172 && b[1] is >= 16 and <= 31) // 172.16/12
                 || (b[0] == 192 && b[1] == 168)             // 192.168/16
                 || (b[0] == 169 && b[1] == 254)             // link-local, cloud metadata
                 || (b[0] == 100 && b[1] is >= 64 and <= 127) // 100.64/10, CGNAT
                 || b[0] >= 224);                            // multicast, reserved
    }
}
