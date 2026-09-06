#pragma warning disable CA1707, CA2007

extern alias CrmApi;

using System.Net;
using System.Net.Http.Json;
using CrmApi::Crm.Api;
using CrmApi::Crm.Api.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.SDK;

namespace CrmErpDemo.AppHost.Tests;

[TestClass]
public sealed class ContactUpsertConcurrencyTests
{
    [TestMethod]
    [Timeout(120_000)]
    public async Task Concurrent_contact_upserts_create_one_contact_and_one_creation_audit()
    {
        var connection = Environment.GetEnvironmentVariable("NIMBUS_SQL_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            Assert.Inconclusive("NIMBUS_SQL_TEST_CONNECTION is required for the SQL concurrency regression.");
        }

        var sql = new SqlConnectionStringBuilder(connection)
        {
            InitialCatalog = $"crm_upsert_test_{Guid.NewGuid():N}",
        };
        var options = new DbContextOptionsBuilder<CrmDbContext>().UseSqlServer(sql.ConnectionString).Options;
        await using var verification = new CrmDbContext(options);
        try
        {
            await verification.Database.EnsureCreatedAsync();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddDbContext<CrmDbContext>(db => db.UseSqlServer(sql.ConnectionString));
            builder.Services.AddSingleton<IPublisherClient>(_ => throw new InvalidOperationException("Upserts must not publish."));
            await using var app = builder.Build();
            app.MapContactEndpoints();
            await app.StartAsync();
            using var client = app.GetTestClient();

            var id = Guid.NewGuid();
            var payload = new ContactUpsertRequest(null, "Concurrent", "Contact", null, null, "Partner");
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = Enumerable.Range(0, 16).Select(async _ =>
            {
                await start.Task;
                using var response = await client.PutAsJsonAsync($"/api/contacts/upsert/{id}", payload);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }).ToArray();
            start.SetResult();
            await Task.WhenAll(requests);

            Assert.AreEqual(1, await verification.Contacts.CountAsync(contact => contact.Id == id));
            Assert.AreEqual(1, await verification.Audits.CountAsync(audit => audit.EntityId == id && audit.Action == "Created"));
            Assert.AreEqual(0, await verification.Audits.CountAsync(audit => audit.EntityId == id && audit.Action == "Updated"));

            using var updated = await client.PutAsJsonAsync($"/api/contacts/upsert/{id}", payload with { FirstName = "Updated", Origin = "Erp" });
            Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
            var contact = await verification.Contacts.SingleAsync(contact => contact.Id == id);
            Assert.AreEqual("Updated", contact.FirstName);
            Assert.AreEqual("Partner", contact.Origin);
            Assert.AreEqual(1, await verification.Audits.CountAsync(audit => audit.EntityId == id && audit.Action == "Updated"));
        }
        finally
        {
            await verification.Database.EnsureDeletedAsync();
        }
    }
}
