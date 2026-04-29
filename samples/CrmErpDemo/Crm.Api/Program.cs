using Crm.Api;
using Crm.Api.Endpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using NimBus.Outbox.SqlServer;
using NimBus.SDK.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Only wire the real ServiceBus client if a connection string was provided.
// Without this guard, Aspire.Azure.Messaging.ServiceBus throws at startup when
// ConnectionStrings:servicebus is missing, Kestrel never binds, and the SPA
// sees ECONNREFUSED. With the guard: the API still comes up, saves to SQL
// succeed, and the only thing that can't happen is publishing to a real topic.
var hasServiceBus = !string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("servicebus"));
if (hasServiceBus)
{
    builder.AddAzureServiceBusClient("servicebus");
}

var crmConnectionString = builder.Configuration.GetConnectionString("crm")
    ?? throw new InvalidOperationException("ConnectionStrings:crm is required.");

builder.Services.AddDbContext<CrmDbContext>(opt => opt.UseSqlServer(crmConnectionString));

// Outbox staging lives in the same database as the entity tables.
// Adapter hosts the dispatcher that forwards these rows to Service Bus.
builder.Services.AddNimBusSqlServerOutbox(crmConnectionString);
if (hasServiceBus)
{
    builder.Services.AddNimBusPublisher("CrmEndpoint");
}
else
{
    // Fallback publisher that writes events to the outbox only. Without the
    // adapter's dispatcher + SB, events stay in the outbox — fine for a
    // demo smoke test of the web → API → DB path.
    builder.Services.AddSingleton<NimBus.SDK.IPublisherClient>(sp =>
    {
        var outbox = sp.GetRequiredService<NimBus.Core.Outbox.IOutbox>();
        var sender = new NimBus.Core.Outbox.OutboxSender(outbox);
        return new NimBus.SDK.PublisherClient(sender);
    });
}

// CORS for the Vite SPA.
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();
app.UseCors();
app.MapDefaultEndpoints();

// Ensure DB + outbox table exist for the demo.
// Retry so the API survives SQL-container startup timing; if init ultimately fails,
// log the error and keep running so Kestrel binds — endpoints will surface the error
// back to the web client via 500 instead of leaving the port unreachable (ECONNREFUSED).
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
var initSucceeded = false;
for (var attempt = 1; attempt <= 10 && !initSucceeded; attempt++)
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
        // EnsureCreatedAsync only creates the schema when it also creates the database.
        // Aspire's SQL Server integration pre-creates the 'crm' database empty, so
        // EnsureCreatedAsync becomes a no-op and Accounts/Contacts never get created.
        // Check for the specific Accounts table — HasTablesAsync returns true if ANY
        // table (e.g. the outbox one) exists, which is misleading here.
        var creator = (IRelationalDatabaseCreator)db.GetService<IDatabaseCreator>();
        if (!await creator.ExistsAsync())
            await creator.CreateAsync();
        var accountsExists = await db.Database.SqlQueryRaw<int>(
            "SELECT CASE WHEN OBJECT_ID(N'dbo.Accounts', N'U') IS NOT NULL THEN 1 ELSE 0 END AS Value")
            .FirstAsync();
        if (accountsExists == 0)
            await creator.CreateTablesAsync();

        // Idempotent column migrations: keep older dev DBs working as the model
        // evolves. Avoids forcing a manual DB drop on every demo iteration.
        await db.Database.ExecuteSqlRawAsync(@"
            IF COL_LENGTH('dbo.Accounts', 'Origin') IS NULL
                ALTER TABLE dbo.Accounts ADD Origin NVARCHAR(8) NOT NULL CONSTRAINT DF_Accounts_Origin DEFAULT '';
            IF COL_LENGTH('dbo.Contacts', 'Origin') IS NULL
                ALTER TABLE dbo.Contacts ADD Origin NVARCHAR(8) NOT NULL CONSTRAINT DF_Contacts_Origin DEFAULT '';
            IF COL_LENGTH('dbo.Accounts', 'IsDeleted') IS NULL
                ALTER TABLE dbo.Accounts ADD IsDeleted BIT NOT NULL CONSTRAINT DF_Accounts_IsDeleted DEFAULT 0;
            IF COL_LENGTH('dbo.Contacts', 'IsDeleted') IS NULL
                ALTER TABLE dbo.Contacts ADD IsDeleted BIT NOT NULL CONSTRAINT DF_Contacts_IsDeleted DEFAULT 0;");

        var outbox = (SqlServerOutbox)scope.ServiceProvider.GetRequiredService<NimBus.Core.Outbox.IOutbox>();
        await outbox.EnsureTableExistsAsync();
        initSucceeded = true;
    }
    catch (Exception ex)
    {
        startupLogger.LogWarning(ex, "Startup init attempt {Attempt} failed: {Message}", attempt, ex.Message);
        if (attempt < 10) await Task.Delay(TimeSpan.FromSeconds(3));
    }
}
if (!initSucceeded)
    startupLogger.LogError("Startup init did not complete after 10 attempts; continuing so Kestrel binds.");

app.MapAccountEndpoints();
app.MapContactEndpoints();

app.Run();
