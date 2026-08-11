#pragma warning disable CA1707, CA2007
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.RateLimiting;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Verifies how a login-policy partition key is derived from the client address.
/// These cases are the specification's verification: the parse table in the
/// plan states what should happen, these prove it. One client must always land
/// in exactly one bucket, and two clients must never share one.
/// </summary>
[TestClass]
public class ClientIpPartitionKeyTests
{
    private static DefaultHttpContext Context(string? remoteIp, string? forwardedFor)
    {
        var ctx = new DefaultHttpContext();
        if (remoteIp is not null)
        {
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        }

        if (forwardedFor is not null)
        {
            ctx.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        return ctx;
    }

    private static RateLimitOptions Options(bool trustForwardedFor, int ipv6PrefixBits = 128)
    {
        var options = new RateLimitOptions { TrustForwardedForHeader = trustForwardedFor };
        options.Login.IPv6PrefixBits = ipv6PrefixBits;
        return options;
    }

    [TestMethod]
    public void Forwarded_header_is_ignored_when_not_trusted()
    {
        var key = ClientIpPartitionKey.Resolve(
            Context("203.0.113.7", "198.51.100.9"),
            Options(trustForwardedFor: false));

        Assert.AreEqual(
            "203.0.113.7",
            key,
            "With TrustForwardedForHeader off the header must not move the partition — otherwise any caller can pick their own bucket.");
    }

    [TestMethod]
    public void Trusted_header_supplies_a_plain_ipv4_address()
    {
        var key = ClientIpPartitionKey.Resolve(
            Context("10.0.0.1", "203.0.113.7"),
            Options(trustForwardedFor: true));

        Assert.AreEqual("203.0.113.7", key);
    }

    [TestMethod]
    public void Trusted_header_strips_the_port_App_Service_appends()
    {
        var key = ClientIpPartitionKey.Resolve(
            Context("10.0.0.1", "203.0.113.7:41234"),
            Options(trustForwardedFor: true));

        Assert.AreEqual("203.0.113.7", key, "Azure App Service appends the client address as ip:port.");
    }

    [TestMethod]
    public void Trusted_header_supplies_a_bare_ipv6_address()
    {
        var key = ClientIpPartitionKey.Resolve(
            Context("10.0.0.1", "2001:db8::1"),
            Options(trustForwardedFor: true));

        Assert.AreEqual("2001:db8::1", key);
    }

    [TestMethod]
    public void Trusted_header_supplies_a_bracketed_ipv6_endpoint()
    {
        var key = ClientIpPartitionKey.Resolve(
            Context("10.0.0.1", "[2001:db8::1]:41234"),
            Options(trustForwardedFor: true));

        Assert.AreEqual("2001:db8::1", key);
    }

    [TestMethod]
    public void Ipv6_spellings_canonicalise_to_one_bucket()
    {
        var key = ClientIpPartitionKey.Resolve(
            Context("10.0.0.1", "2001:0DB8:0000:0000:0000:0000:0000:0001"),
            Options(trustForwardedFor: true));

        Assert.AreEqual(
            "2001:db8::1",
            key,
            "Casing and zero-compression must not produce a second bucket for one address.");
    }

    [TestMethod]
    public void Distinct_ipv6_addresses_in_one_slash64_are_distinct_buckets()
    {
        var one = ClientIpPartitionKey.Resolve(Context("10.0.0.1", "2001:db8::1"), Options(trustForwardedFor: true));
        var two = ClientIpPartitionKey.Resolve(Context("10.0.0.1", "2001:db8::2"), Options(trustForwardedFor: true));

        Assert.AreEqual("2001:db8::2", two);
        Assert.AreNotEqual(one, two, "The default is per-client-IP, so two addresses in one /64 must not share a bucket.");
    }

    [TestMethod]
    public void Ipv4_mapped_ipv6_is_unmapped()
    {
        var mapped = ClientIpPartitionKey.Resolve(Context("10.0.0.1", "::ffff:203.0.113.7"), Options(trustForwardedFor: true));
        var plain = ClientIpPartitionKey.Resolve(Context("10.0.0.1", "203.0.113.7"), Options(trustForwardedFor: true));

        Assert.AreEqual("203.0.113.7", mapped, "A dual-stack socket surfaces IPv4 clients as ::ffff:… — one client, one bucket.");
        Assert.AreEqual(plain, mapped);
    }

    [TestMethod]
    public void Last_forwarded_hop_wins()
    {
        var key = ClientIpPartitionKey.Resolve(
            Context("10.0.0.1", "198.51.100.9, [2001:db8:1::2]:9000"),
            Options(trustForwardedFor: true));

        Assert.AreEqual(
            "2001:db8:1::2",
            key,
            "The trusted proxy appends the address it observed; reading the leftmost entry lets a caller prepend an arbitrary partition.");
    }

    [TestMethod]
    public void Unparseable_header_falls_back_to_the_connection_address()
    {
        var garbage = ClientIpPartitionKey.Resolve(Context("203.0.113.7", "not-an-ip"), Options(trustForwardedFor: true));
        var empty = ClientIpPartitionKey.Resolve(Context("203.0.113.7", ""), Options(trustForwardedFor: true));

        Assert.AreEqual("203.0.113.7", garbage);
        Assert.AreEqual("203.0.113.7", empty);
    }

    [TestMethod]
    public void No_address_at_all_yields_unknown()
    {
        var key = ClientIpPartitionKey.Resolve(Context(remoteIp: null, forwardedFor: null), Options(trustForwardedFor: false));

        Assert.AreEqual("unknown", key);
    }

    [TestMethod]
    public void Ipv6_prefix_knob_buckets_by_prefix_when_set_below_128()
    {
        var one = ClientIpPartitionKey.Resolve(
            Context("10.0.0.1", "2001:db8::1"),
            Options(trustForwardedFor: true, ipv6PrefixBits: 64));
        var two = ClientIpPartitionKey.Resolve(
            Context("10.0.0.1", "2001:db8::2"),
            Options(trustForwardedFor: true, ipv6PrefixBits: 64));

        Assert.AreEqual(one, two, "With IPv6PrefixBits = 64 an entire /64 shares one bucket.");
        Assert.AreEqual("2001:db8::/64", one);
    }

    [TestMethod]
    public void Ipv6_prefix_knob_does_not_affect_ipv4()
    {
        var key = ClientIpPartitionKey.Resolve(
            Context("10.0.0.1", "203.0.113.7"),
            Options(trustForwardedFor: true, ipv6PrefixBits: 64));

        Assert.AreEqual("203.0.113.7", key);
    }
}
