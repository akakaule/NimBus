var builder = DistributedApplication.CreateBuilder(args);

// Fully on-premise NimBus deployment — no Azure resources are referenced or
// transitively pulled in. Both required RabbitMQ plugins
// (rabbitmq_consistent_hash_exchange, rabbitmq_delayed_message_exchange) are
// pre-loaded by the custom image built from Dockerfile.rabbitmq next to this
// AppHost.
//
// The two plugins are hard prerequisites of NimBus.Transport.RabbitMQ:
//  - consistent-hash exchange: per-key ordering across N partition queues.
//  - delayed-message exchange: ScheduleMessage(...) support for orchestration
//    timeouts.
// The transport's startup health check fails loud with a remediation hint if
// either plugin is missing, so a misconfigured local broker surfaces fast.
var rabbitmq = builder.AddRabbitMQ("rabbitmq")
    .WithImage("rabbitmq", "4-management")
    .WithBindMount(
        source: Path.Combine(AppContext.BaseDirectory, "enabled_plugins"),
        target: "/etc/rabbitmq/enabled_plugins",
        isReadOnly: true)
    .WithManagementPlugin();

// SQL Server container hosts the NimBus message store (audit trail, parked
// messages, session state, outbox).
var sql = builder.AddSqlServer("sql");
var nimbusDb = sql.AddDatabase("nimbus");

builder.AddProject<Projects.RabbitMqOnPrem_Publisher>("publisher")
    .WithReference(rabbitmq)
    .WithReference(nimbusDb)
    .WaitFor(rabbitmq)
    .WaitFor(nimbusDb)
    .WithExternalHttpEndpoints();

builder.AddProject<Projects.RabbitMqOnPrem_Subscriber>("subscriber")
    .WithReference(rabbitmq)
    .WithReference(nimbusDb)
    .WaitFor(rabbitmq)
    .WaitFor(nimbusDb);

builder.Build().Run();
