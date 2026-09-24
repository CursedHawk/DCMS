using System.Net;
using Dcms.Edge.Protection;
using Dcms.Shared.Data.Edge;
using Microsoft.AspNetCore.Http;

namespace Dcms.UnitTests.Edge;

/// <summary>
/// The rate-limit exemption list: what the console accepts, and what the edge then lets through.
/// Both directions matter -- too strict and an operator cannot list their load generator; too
/// loose and a typo takes a DoS control off a large part of the internet.
/// </summary>
public class RateLimitExemptionTests
{
    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7/32")]
    [InlineData(" 203.0.113.7 ", "203.0.113.7/32")]
    [InlineData("203.0.113.0/24", "203.0.113.0/24")]
    [InlineData("203.0.113.99/24", "203.0.113.0/24")]   // host bits cleared
    [InlineData("10.0.0.0/8", "10.0.0.0/8")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7/32")] // mapped IPv6 is the IPv4 address
    [InlineData("2001:db8::1", "2001:db8::1/128")]
    [InlineData("2001:db8:abcd::5/48", "2001:db8:abcd::/48")]
    public void Normalizes_addresses_and_ranges(string input, string expected)
    {
        RateLimitExemptionRules.TryNormalize(input, out var cidr, out var error).Should().BeTrue(error);
        cidr.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-ip")]
    [InlineData("203.0.113.7/33")]
    [InlineData("203.0.113.7/x")]
    [InlineData("0.0.0.0/0")]       // everything: the control removed, not an exemption
    [InlineData("10.0.0.0/7")]
    [InlineData("2001:db8::/31")]
    public void Refuses_invalid_or_too_broad(string input)
    {
        RateLimitExemptionRules.TryNormalize(input, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("203.0.113.7", true)]
    [InlineData("::ffff:203.0.113.7", true)]   // how Kestrel reports IPv4 on a dual-stack socket
    [InlineData("198.51.100.20", true)]
    [InlineData("198.51.101.20", false)]
    [InlineData("203.0.113.8", false)]
    [InlineData("2001:db8:1::42", true)]
    [InlineData("2001:db8:2::42", false)]
    public void Matches_the_peer_against_the_list(string peer, bool exempt)
    {
        var ranges = RateLimitExemptionRules.Parse(["203.0.113.7/32", "198.51.100.0/24", "2001:db8:1::/48", "garbage"]);
        RateLimitExemptionRules.Matches(ranges, IPAddress.Parse(peer)).Should().Be(exempt);
    }

    [Fact]
    public void Edge_marks_an_exempt_peer_and_strips_a_forged_header()
    {
        var exemptions = new RateLimitExemptions();
        exemptions.Replace(RateLimitExemptionRules.Parse(["203.0.113.0/24"]));

        var exempt = Request("203.0.113.9", forged: false);
        exemptions.Mark(exempt);
        RateLimitExemptions.IsMarked(exempt).Should().BeTrue();
        exempt.Request.Headers[RateLimitExemptions.Header].ToString().Should().Be("1");

        // A stranger claiming exemption downstream gets neither the mark nor the header.
        var forged = Request("192.0.2.50", forged: true);
        exemptions.Mark(forged);
        RateLimitExemptions.IsMarked(forged).Should().BeFalse();
        forged.Request.Headers.ContainsKey(RateLimitExemptions.Header).Should().BeFalse();
    }

    private static DefaultHttpContext Request(string peer, bool forged)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        if (forged)
        {
            context.Request.Headers[RateLimitExemptions.Header] = "1";
        }
        return context;
    }
}
