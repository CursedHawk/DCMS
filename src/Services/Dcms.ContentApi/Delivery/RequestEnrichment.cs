using System.Text.RegularExpressions;

namespace Dcms.ContentApi.Delivery;

/// <summary>
/// The dimensions an analytics event carries that the browser cannot be trusted to
/// report — or does not know. A page can lie about its country; it cannot see its
/// own IP at all.
/// </summary>
public sealed record RequestFacts(string? Device, string? Browser, string? Os, string? Country);

/// <summary>
/// Resolves a country from the client's IP.
///
/// An interface rather than a hard dependency on a GeoIP database because the
/// answer differs per deployment: behind Cloudflare (or any edge that stamps a
/// country header) the work is already done, and a self-hosted edge needs a local
/// database file. <see cref="HeaderGeoIpResolver"/> covers the former; a MaxMind or
/// DB-IP backed implementation can be registered in its place without touching the
/// ingest path.
/// </summary>
public interface IGeoIpResolver
{
    /// <summary>ISO 3166-1 alpha-2, or null when the country is unknown.</summary>
    string? ResolveCountry(HttpContext context);
}

/// <summary>
/// Reads the country from a header set by the edge — <c>CF-IPCountry</c> by default
/// (Cloudflare), configurable via <c>Analytics:CountryHeader</c>.
///
/// Trusting a request header is only safe because the header is set by *our* edge
/// and content-api is not reachable except through it. If that ever stops being
/// true, this must go behind a real GeoIP lookup rather than gain an allow-list.
/// </summary>
public sealed class HeaderGeoIpResolver(IConfiguration configuration) : IGeoIpResolver
{
    private readonly string _header = configuration["Analytics:CountryHeader"] ?? "CF-IPCountry";

    public string? ResolveCountry(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(_header, out var values))
        {
            return null;
        }
        var value = values.ToString().Trim();
        // Cloudflare sends "XX" for anonymised/unknown clients and "T1" for Tor.
        if (value.Length != 2 || value is "XX" or "T1")
        {
            return null;
        }
        return value.ToUpperInvariant();
    }
}

/// <summary>
/// Classifies a User-Agent into device / browser / OS.
///
/// Deliberately a small set of substring rules rather than a UA-parsing library: the
/// dashboard groups by these values, so a coarse, stable bucketing ("Chrome", not
/// "Chrome 121.0.6167.85") is what is actually wanted, and it avoids a dependency
/// whose regex database needs updating to stay correct.
/// </summary>
public static class UserAgentFacts
{
    private static readonly Regex BotPattern = new(
        @"bot|crawler|spider|crawling|slurp|facebookexternalhit|preview|monitor|curl|wget|python-requests|headless",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static RequestFacts Parse(string? userAgent, string? country)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return new RequestFacts(null, null, null, country);
        }

        var ua = userAgent;
        if (BotPattern.IsMatch(ua))
        {
            // Bots are kept rather than dropped: "how much of this traffic is real"
            // is a question the dashboard should be able to answer, and silently
            // discarding them makes totals unexplainable.
            return new RequestFacts("bot", null, null, country);
        }

        var device = ua.Contains("iPad", StringComparison.OrdinalIgnoreCase)
                     || (ua.Contains("Android", StringComparison.OrdinalIgnoreCase)
                         && !ua.Contains("Mobile", StringComparison.OrdinalIgnoreCase))
            ? "tablet"
            : ua.Contains("Mobi", StringComparison.OrdinalIgnoreCase)
              || ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
                ? "mobile"
                : "desktop";

        // Order matters: every Chromium browser also says "Chrome", and Chrome says
        // "Safari". Most specific first.
        var browser = ua switch
        {
            _ when ua.Contains("Edg/", StringComparison.OrdinalIgnoreCase) => "Edge",
            _ when ua.Contains("OPR/", StringComparison.OrdinalIgnoreCase)
                   || ua.Contains("Opera", StringComparison.OrdinalIgnoreCase) => "Opera",
            _ when ua.Contains("SamsungBrowser", StringComparison.OrdinalIgnoreCase) => "Samsung Internet",
            _ when ua.Contains("Firefox", StringComparison.OrdinalIgnoreCase) => "Firefox",
            _ when ua.Contains("Chrome", StringComparison.OrdinalIgnoreCase) => "Chrome",
            _ when ua.Contains("Safari", StringComparison.OrdinalIgnoreCase) => "Safari",
            _ => null,
        };

        var os = ua switch
        {
            _ when ua.Contains("Windows", StringComparison.OrdinalIgnoreCase) => "Windows",
            _ when ua.Contains("Android", StringComparison.OrdinalIgnoreCase) => "Android",
            // iPadOS reports "Mac OS X" on desktop-mode Safari, so iOS must come first.
            _ when ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
                   || ua.Contains("iPad", StringComparison.OrdinalIgnoreCase) => "iOS",
            _ when ua.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase) => "macOS",
            _ when ua.Contains("CrOS", StringComparison.OrdinalIgnoreCase) => "ChromeOS",
            _ when ua.Contains("Linux", StringComparison.OrdinalIgnoreCase) => "Linux",
            _ => null,
        };

        return new RequestFacts(device, browser, os, country);
    }
}

/// <summary>Campaign parameters carried on the page URL the beacon reports.</summary>
public sealed record UtmFacts(string? Source, string? Medium, string? Campaign)
{
    public static readonly UtmFacts None = new(null, null, null);

    /// <summary>
    /// Pulls utm_source / utm_medium / utm_campaign out of a page path's query.
    /// The beacon reports the path it was on, so the campaign that brought the
    /// visitor is right there — it just was not being read.
    /// </summary>
    public static UtmFacts FromPath(string? path)
    {
        var q = path?.IndexOf('?') ?? -1;
        if (path is null || q < 0 || q == path.Length - 1)
        {
            return None;
        }
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(path[q..]);
        string? Get(string key) =>
            query.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
                ? Truncate(v.ToString().Trim(), 128)
                : null;
        return new UtmFacts(Get("utm_source"), Get("utm_medium"), Get("utm_campaign"));
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
