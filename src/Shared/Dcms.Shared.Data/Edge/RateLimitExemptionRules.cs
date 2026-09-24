using System.Net;
using System.Net.Sockets;

namespace Dcms.Shared.Data.Edge;

/// <summary>
/// What a valid rate-limit exemption is, and how an address is matched against the list. One
/// definition for the two places that need it: admin-api refuses bad input with it, the edge
/// matches with it — so what the console accepted is exactly what the edge enforces.
/// </summary>
public static class RateLimitExemptionRules
{
    /// <summary>
    /// The widest ranges accepted. Anything broader switches rate limiting off for a large part
    /// of the internet, which is not an exemption but the control removed — do that in config,
    /// on purpose, not with one line in a list.
    /// </summary>
    public const int MinIPv4Prefix = 8;
    public const int MinIPv6Prefix = 32;

    /// <summary>
    /// Turns an address or range as an operator would type it into the canonical CIDR stored:
    /// a bare address becomes /32 or /128, host bits are cleared ("10.1.2.3/16" is "10.1.0.0/16"),
    /// and an IPv4-mapped IPv6 address is treated as the IPv4 address it is.
    /// </summary>
    public static bool TryNormalize(string? input, out string cidr, out string? error)
    {
        cidr = string.Empty;
        error = null;
        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = "Enter an IP address or a range in CIDR form, such as 203.0.113.7 or 203.0.113.0/24.";
            return false;
        }

        var slash = text.IndexOf('/');
        if (!IPAddress.TryParse(slash < 0 ? text : text[..slash], out var address))
        {
            error = $"'{text}' is not an IP address.";
            return false;
        }
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = bits;
        if (slash >= 0 && (!int.TryParse(text[(slash + 1)..], out prefix) || prefix < 0 || prefix > bits))
        {
            error = $"'{text}': the prefix length must be a number from 0 to {bits}.";
            return false;
        }

        var min = bits == 32 ? MinIPv4Prefix : MinIPv6Prefix;
        if (prefix < min)
        {
            error = $"'{text}' is too broad: the widest range allowed is /{min}.";
            return false;
        }

        cidr = new IPNetwork(Mask(address, prefix), prefix).ToString();
        return true;
    }

    /// <summary>Parses stored rows; a row that no longer parses is skipped, never fatal.</summary>
    public static IPNetwork[] Parse(IEnumerable<string> cidrs) =>
        [.. cidrs.Select(c => IPNetwork.TryParse(c, out var n) ? n : (IPNetwork?)null)
                 .Where(n => n is not null)
                 .Select(n => n!.Value)];

    /// <summary>Whether a peer address falls inside any of the ranges.</summary>
    public static bool Matches(IReadOnlyList<IPNetwork> ranges, IPAddress? address)
    {
        if (address is null || ranges.Count == 0)
        {
            return false;
        }
        // Kestrel on a dual-stack socket reports IPv4 clients as ::ffff:a.b.c.d, which no IPv4
        // range contains.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        foreach (var range in ranges)
        {
            if (range.Contains(address))
            {
                return true;
            }
        }
        return false;
    }

    private static IPAddress Mask(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var keep = Math.Clamp(prefix - (i * 8), 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - keep));
        }
        return new IPAddress(bytes);
    }
}
