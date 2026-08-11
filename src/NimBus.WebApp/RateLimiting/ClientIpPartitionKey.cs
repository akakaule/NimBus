using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;

namespace NimBus.WebApp.RateLimiting;

/// <summary>
/// Derives the rate-limiting partition key for the login policy from the
/// client's IP address. The key is the client's <em>full canonical address</em>,
/// so one client always lands in exactly one bucket and two clients never share
/// one. See <c>docs/rate-limiting.md</c> for the trust model.
/// </summary>
internal static class ClientIpPartitionKey
{
    private const string ForwardedForHeader = "X-Forwarded-For";

    /// <summary>Returned when no address can be determined at all.</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// Resolves the canonical client address for <paramref name="context"/>.
    /// </summary>
    public static string Resolve(HttpContext context, RateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        var address = ResolveAddress(context, options);
        if (address is null)
        {
            return Unknown;
        }

        // Kestrel on a dual-stack socket surfaces an IPv4 client as
        // ::ffff:203.0.113.7. Without unmapping, one client occupies two buckets
        // depending on socket mode and its effective budget silently doubles.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
        {
            // fe80::1%12 and fe80::1%13 are the same peer; the scope id is local
            // routing information, not identity.
            address = new IPAddress(address.GetAddressBytes());
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6
            && options.Login.IPv6PrefixBits is > 0 and < 128)
        {
            return MaskToPrefix(address, options.Login.IPv6PrefixBits);
        }

        // IPAddress.ToString() canonicalises casing and zero-compression, so
        // 2001:0DB8:0000::0001 and 2001:db8::1 cannot become two buckets.
        return address.ToString();
    }

    private static IPAddress? ResolveAddress(HttpContext context, RateLimitOptions options)
    {
        var forwarded = ForwardedCandidate(context, options);
        if (forwarded is not null && TryParseAddress(forwarded, out var address))
        {
            return address;
        }

        return context.Connection.RemoteIpAddress;
    }

    private static string? ForwardedCandidate(HttpContext context, RateLimitOptions options)
    {
        if (!options.TrustForwardedForHeader)
        {
            // An untrusted forwarded address must not move the partition — any
            // caller could otherwise pick a fresh bucket per request.
            return null;
        }

        var header = context.Request.Headers[ForwardedForHeader].ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        // The LAST entry, not the first: Azure App Service appends the client
        // address it observed to any inbound X-Forwarded-For, so the rightmost
        // hop is the one the trusted proxy wrote. Reading the leftmost entry
        // would let a caller prepend an arbitrary partition key per request.
        var lastSeparator = header.LastIndexOf(',');
        var candidate = lastSeparator < 0 ? header : header[(lastSeparator + 1)..];
        candidate = candidate.Trim();
        return candidate.Length == 0 ? null : candidate;
    }

    /// <summary>
    /// Parses every shape a forwarded address can arrive in — bare IPv4/IPv6,
    /// <c>ipv4:port</c> and <c>[ipv6]:port</c> — with one call. Hand-rolled colon
    /// handling mangles bare IPv6 literals; <see cref="IPEndPoint.TryParse(string, out IPEndPoint)"/>
    /// treats a single colon as an IPv4 port separator and <c>]:</c> as a
    /// bracketed-endpoint port separator, which is exactly the disambiguation needed.
    /// </summary>
    private static bool TryParseAddress(string candidate, out IPAddress address)
    {
        if (IPEndPoint.TryParse(candidate, out var endpoint))
        {
            address = endpoint.Address;
            return true;
        }

        address = IPAddress.None;
        return false;
    }

    private static string MaskToPrefix(IPAddress address, int prefixBits)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bitsBefore = i * 8;
            if (bitsBefore >= prefixBits)
            {
                bytes[i] = 0;
            }
            else if (bitsBefore + 8 > prefixBits)
            {
                bytes[i] &= (byte)(0xFF << (bitsBefore + 8 - prefixBits));
            }
        }

        return $"{new IPAddress(bytes)}/{prefixBits}";
    }
}
