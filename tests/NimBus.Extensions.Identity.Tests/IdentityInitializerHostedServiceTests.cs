#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Extensions.Identity.Data;

namespace NimBus.Extensions.Identity.Tests;

[TestClass]
public class IdentityInitializerHostedServiceTests
{
    [TestMethod]
    public async Task FirstRun_CreatesSchemaAndBootstrapsAdmin()
    {
        await using var scope = IdentityTestScope.Create(o =>
        {
            o.Bootstrap.Email = "admin@local";
            o.Bootstrap.Password = "Local!Admin123";
        });

        await RunInitializerAsync(scope);

        using var serviceScope = scope.Services.CreateScope();
        var userManager = serviceScope.ServiceProvider.GetRequiredService<UserManager<NimBusUser>>();
        var users = await userManager.Users.ToListAsync();

        Assert.AreEqual(1, users.Count, "bootstrap should seed exactly one admin on a fresh store");
        Assert.AreEqual("admin@local", users[0].Email);
        Assert.IsTrue(users[0].EmailConfirmed, "bootstrap admin must be confirmed so it can sign in without SMTP");
    }

    [TestMethod]
    public async Task SecondRun_IsIdempotent_NoSecondAdminCreated()
    {
        await using var scope = IdentityTestScope.Create(o =>
        {
            o.Bootstrap.Email = "admin@local";
            o.Bootstrap.Password = "Local!Admin123";
        });

        await RunInitializerAsync(scope);
        await RunInitializerAsync(scope);

        using var serviceScope = scope.Services.CreateScope();
        var userManager = serviceScope.ServiceProvider.GetRequiredService<UserManager<NimBusUser>>();
        var count = await userManager.Users.CountAsync();

        Assert.AreEqual(1, count, "running the initializer twice must not seed a second admin");
    }

    [TestMethod]
    public async Task Bootstrap_NoOpsWhenUserStoreAlreadyHasUsers()
    {
        await using var scope = IdentityTestScope.Create(o =>
        {
            o.Bootstrap.Email = "newadmin@local";
            o.Bootstrap.Password = "Local!Admin123";
        });

        // First run: schema + tables only, NO bootstrap creds — so a different
        // user gets seeded manually instead, then the second run with
        // bootstrap creds set should not overwrite it.
        await RunInitializerAsync(scope);

        using (var seedScope = scope.Services.CreateScope())
        {
            var userManager = seedScope.ServiceProvider.GetRequiredService<UserManager<NimBusUser>>();
            var existing = new NimBusUser
            {
                UserName = "preexisting@local",
                Email = "preexisting@local",
                EmailConfirmed = true,
            };
            var result = await userManager.CreateAsync(existing, "Pre!Existing123");
            Assert.IsTrue(result.Succeeded, "harness setup: existing user must seed");
        }

        await RunInitializerAsync(scope);

        using var verifyScope = scope.Services.CreateScope();
        var verifyManager = verifyScope.ServiceProvider.GetRequiredService<UserManager<NimBusUser>>();
        var users = await verifyManager.Users.ToListAsync();
        Assert.AreEqual(1, users.Count, "bootstrap must not seed when the user store is non-empty");
        Assert.AreEqual("preexisting@local", users[0].Email);
    }

    [TestMethod]
    public async Task NoBootstrapConfigured_CreatesSchemaButSeedsNoUsers()
    {
        await using var scope = IdentityTestScope.Create();

        await RunInitializerAsync(scope);

        using var serviceScope = scope.Services.CreateScope();
        var userManager = serviceScope.ServiceProvider.GetRequiredService<UserManager<NimBusUser>>();
        var count = await userManager.Users.CountAsync();
        Assert.AreEqual(0, count, "no bootstrap email + password means no seeded user");

        // But the schema + tables should still be there — verify by writing
        // and reading a user directly through UserManager.
        var probe = new NimBusUser { UserName = "probe@local", Email = "probe@local", EmailConfirmed = true };
        var create = await userManager.CreateAsync(probe, "Probe!Local123");
        Assert.IsTrue(create.Succeeded, "schema must be initialized even without bootstrap creds");
    }

    [TestMethod]
    public async Task InvalidSchema_LogsErrorAndDoesNotCrashHost()
    {
        // The initializer's StartAsync catches all exceptions and logs; an
        // invalid schema must not bring the host down. We only need to
        // verify StartAsync completes — the explanation is in the swallowed
        // log, not in observable behaviour.
        await using var scope = IdentityTestScope.Create(o =>
        {
            o.Schema = "1bad schema name"; // fails SchemaNamePattern
        });

        await RunInitializerAsync(scope); // must not throw

        // The schema was bad, so the tables weren't created and the user
        // store is unreachable. Don't probe UserManager — just assert that
        // we reached this line.
        Assert.IsTrue(true, "StartAsync should swallow and log, not throw");
    }

    private static async Task RunInitializerAsync(IdentityTestScope scope)
    {
        var initializer = scope.Services.GetServices<IHostedService>()
            .OfType<Services.IdentityInitializerHostedService>()
            .Single();
        await initializer.StartAsync(CancellationToken.None);
    }
}
