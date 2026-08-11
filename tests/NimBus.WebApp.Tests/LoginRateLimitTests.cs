#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.RateLimiting;

namespace NimBus.WebApp.Tests;

/// <summary>
/// AC-8: the per-client-IP login limiter, which fills the gap the per-account
/// Identity lockout cannot — one password tried once each across many accounts.
/// <para>
/// The route-to-policy binding is already proved by
/// <see cref="RateLimitEndpointMetadataTests"/>; this class stubs the login
/// endpoint so limiter behaviour is isolated from Identity's SQL dependency and
/// from [ValidateAntiForgeryToken], and therefore runs in CI with no database.
/// Enforcement cases shrink only PermitLimit and keep the production 300-second
/// window, so a millisecond-scale burst cannot straddle a window edge.
/// </para>
/// </summary>
[TestClass]
public class LoginRateLimitTests
{
    private const string ClientIpHeader = "X-Test-Ip";

    [TestMethod]
    public async Task Spraying_distinct_accounts_from_one_address_is_throttled()
    {
        // AC-8. Every request targets a DIFFERENT email, so no account
        // accumulates more than one failure: the per-IP throttle fires strictly
        // before any account approaches its 5-failure lockout.
        const int permits = 4;
        await using var host = await Host(new() { ["RateLimiting:Login:PermitLimit"] = permits.ToString(CultureInfo.InvariantCulture) });

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < permits + 1; i++)
        {
            statuses.Add(await host.PostLoginAsync("203.0.113.7", email: $"victim-{i}@example.com"));
        }

        Assert.IsTrue(
            statuses.Take(permits).All(s => s != HttpStatusCode.TooManyRequests),
            "Requests under the allowance must pass through.");
        Assert.AreEqual(
            HttpStatusCode.TooManyRequests,
            statuses[^1],
            "The spraying attempt must be throttled by source address, which per-account lockout can never do.");
        Assert.AreEqual(permits, host.AttemptedEmails.Count, "Only the admitted requests reach the endpoint.");
        Assert.AreEqual(permits, host.AttemptedEmails.Distinct().Count(), "No account was tried twice, so no account neared its lockout threshold.");
    }

    [TestMethod]
    public async Task Two_callers_behind_one_address_share_a_bucket()
    {
        const int permits = 3;
        await using var host = await Host(new() { ["RateLimiting:Login:PermitLimit"] = permits.ToString(CultureInfo.InvariantCulture) });

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < permits + 1; i++)
        {
            statuses.Add(await host.PostLoginAsync("198.51.100.4", email: $"operator-{i}@example.com"));
        }

        Assert.AreEqual(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    [TestMethod]
    public async Task Distinct_addresses_get_independent_budgets()
    {
        const int permits = 3;
        await using var host = await Host(new() { ["RateLimiting:Login:PermitLimit"] = permits.ToString(CultureInfo.InvariantCulture) });

        foreach (var ip in new[] { "203.0.113.10", "203.0.113.11" })
        {
            for (var i = 0; i < permits; i++)
            {
                Assert.AreNotEqual(
                    HttpStatusCode.TooManyRequests,
                    await host.PostLoginAsync(ip),
                    $"{ip} request {i + 1} must not be throttled — each address has its own budget.");
            }
        }
    }

    [TestMethod]
    public async Task Untrusted_forwarded_addresses_cannot_move_the_bucket()
    {
        const int permits = 3;
        await using var host = await Host(new() { ["RateLimiting:Login:PermitLimit"] = permits.ToString(CultureInfo.InvariantCulture) });

        HttpStatusCode last = default;
        for (var i = 0; i < permits + 1; i++)
        {
            last = await host.PostLoginAsync("203.0.113.20", forwardedFor: $"198.51.100.{i}");
        }

        Assert.AreEqual(
            HttpStatusCode.TooManyRequests,
            last,
            "With TrustForwardedForHeader off a spoofed header must not buy a fresh budget per request.");
    }

    [TestMethod]
    public async Task A_prepended_forwarded_hop_cannot_move_the_bucket()
    {
        const int permits = 3;
        await using var host = await Host(new()
        {
            ["RateLimiting:Login:PermitLimit"] = permits.ToString(CultureInfo.InvariantCulture),
            ["RateLimiting:TrustForwardedForHeader"] = "true",
        });

        HttpStatusCode last = default;
        for (var i = 0; i < permits + 1; i++)
        {
            // The attacker controls everything except the last hop, which the
            // trusted proxy appends.
            last = await host.PostLoginAsync("10.0.0.1", forwardedFor: $"198.51.100.{i}, 203.0.113.30");
        }

        Assert.AreEqual(HttpStatusCode.TooManyRequests, last, "Only the last forwarded hop may be read.");
    }

    [TestMethod]
    public async Task Ipv6_addresses_in_one_slash64_are_independent_buckets()
    {
        const int permits = 3;
        await using var host = await Host(new()
        {
            ["RateLimiting:Login:PermitLimit"] = permits.ToString(CultureInfo.InvariantCulture),
            ["RateLimiting:TrustForwardedForHeader"] = "true",
        });

        foreach (var ip in new[] { "2001:db8::1", "2001:db8::2" })
        {
            for (var i = 0; i < permits; i++)
            {
                Assert.AreNotEqual(
                    HttpStatusCode.TooManyRequests,
                    await host.PostLoginAsync("10.0.0.1", forwardedFor: ip),
                    "The shipped default is per-client-IP, so two addresses in one /64 do not share a budget.");
            }
        }
    }

    [TestMethod]
    public async Task Ipv6_spellings_of_one_address_share_a_bucket()
    {
        const int permits = 3;
        await using var host = await Host(new()
        {
            ["RateLimiting:Login:PermitLimit"] = permits.ToString(CultureInfo.InvariantCulture),
            ["RateLimiting:TrustForwardedForHeader"] = "true",
        });

        string[] spellings = ["2001:db8::1", "2001:0DB8:0000:0000:0000:0000:0000:0001", "[2001:db8::1]:41234"];

        HttpStatusCode last = default;
        for (var i = 0; i < permits + 1; i++)
        {
            last = await host.PostLoginAsync("10.0.0.1", forwardedFor: spellings[i % spellings.Length]);
        }

        Assert.AreEqual(HttpStatusCode.TooManyRequests, last, "Three spellings of one address are one bucket.");
    }

    [TestMethod]
    public async Task Ipv6_prefix_knob_merges_a_slash64_into_one_bucket()
    {
        const int permits = 3;
        await using var host = await Host(new()
        {
            ["RateLimiting:Login:PermitLimit"] = permits.ToString(CultureInfo.InvariantCulture),
            ["RateLimiting:TrustForwardedForHeader"] = "true",
            ["RateLimiting:Login:IPv6PrefixBits"] = "64",
        });

        HttpStatusCode last = default;
        for (var i = 0; i < permits + 1; i++)
        {
            last = await host.PostLoginAsync("10.0.0.1", forwardedFor: $"2001:db8::{i + 1}");
        }

        Assert.AreEqual(
            HttpStatusCode.TooManyRequests,
            last,
            "IPv6PrefixBits = 64 is the operator's lever against an attacker rotating addresses inside a routed /64.");
    }

    [TestMethod]
    public async Task Shared_corporate_egress_is_not_throttled_at_the_shipped_default()
    {
        // The §5 scenario the default is sized from: 8 operators behind one
        // NAT'd address, 3 POSTs each (two typos then success) = 24 of 50.
        await using var host = await Host([]);

        for (var operatorIndex = 0; operatorIndex < 8; operatorIndex++)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                Assert.AreNotEqual(
                    HttpStatusCode.TooManyRequests,
                    await host.PostLoginAsync("198.51.100.77", email: $"operator-{operatorIndex}@example.com"),
                    "24 requests from one shared egress must stay well inside the 50-permit default.");
            }
        }
    }

    [TestMethod]
    public async Task Seventeen_distinct_clients_are_not_throttled_at_the_shipped_default()
    {
        // 17 × 3 = 51 requests, which would exceed a single 50-permit bucket —
        // proof that they are 17 buckets of 3, not one bucket of 51. Run over
        // IPv4 and over addresses inside one IPv6 /64.
        await using var host = await Host(new() { ["RateLimiting:TrustForwardedForHeader"] = "true" });

        for (var client = 0; client < 17; client++)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                Assert.AreNotEqual(HttpStatusCode.TooManyRequests, await host.PostLoginAsync($"203.0.113.{client + 1}"));
                Assert.AreNotEqual(
                    HttpStatusCode.TooManyRequests,
                    await host.PostLoginAsync("10.0.0.1", forwardedFor: $"2001:db8:1::{client + 1:x}"));
            }
        }
    }

    private static Task<LoginHost> Host(Dictionary<string, string?> settings) => LoginHost.CreateAsync(settings);

    private sealed class LoginHost : IAsyncDisposable
    {
        private readonly IHost _host;

        private LoginHost(IHost host) => _host = host;

        public List<string> AttemptedEmails { get; } = [];

        public static async Task<LoginHost> CreateAsync(Dictionary<string, string?> settings)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            LoginHost? instance = null;

            var host = await new HostBuilder()
                .ConfigureWebHost(web =>
                {
                    web.UseTestServer();
                    web.ConfigureServices(services =>
                    {
                        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Error));
                        services.AddRouting();
                        services.AddNimBusRateLimiting(configuration);
                    });
                    web.Configure(app =>
                    {
                        // Placed before UseRateLimiter so the partition key sees
                        // the address the test intends.
                        app.Use(async (context, next) =>
                        {
                            if (context.Request.Headers.TryGetValue(ClientIpHeader, out var ip))
                            {
                                context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip.ToString());
                            }

                            await next(context);
                        });

                        app.UseRouting();
                        app.UseRateLimiter();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapPost("/account/login", (HttpContext context) =>
                            {
                                instance!.AttemptedEmails.Add(context.Request.Query["email"].ToString());
                                return Results.Ok();
                            }).RequireRateLimiting(RateLimitPolicyNames.Login);
                        });
                    });
                })
                .StartAsync();

            instance = new LoginHost(host);
            return instance;
        }

        public async Task<HttpStatusCode> PostLoginAsync(string clientIp, string? forwardedFor = null, string email = "victim@example.com")
        {
            using var client = _host.GetTestServer().CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/account/login?email={Uri.EscapeDataString(email)}");
            request.Headers.Add(ClientIpHeader, clientIp);
            if (forwardedFor is not null)
            {
                request.Headers.Add("X-Forwarded-For", forwardedFor);
            }

            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
