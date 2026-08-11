#pragma warning disable CA1707, CA2007

using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.ServiceBus.Provisioning;

namespace NimBus.ServiceBusEmulator.Tests;

[TestClass]
public sealed class SdkSmokeTests
{
    [TestMethod]
    [TestCategory("CommonFidelity")]
    [Timeout(60_000)]
    public async Task Stock_sdk_admin_and_data_planes_share_one_port()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var entityName = $"sdk-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        var topicOptions = new CreateTopicOptions(entityName)
        {
            SupportOrdering = true,
            EnableBatchedOperations = true,
        };
        var createdTopic = await admin.CreateTopicAsync(topicOptions, timeout.Token);
        Assert.IsTrue(createdTopic.Value.SupportOrdering);

        var createdSubscription = await admin.CreateSubscriptionAsync(
            new CreateSubscriptionOptions(entityName, "consumer")
            {
                LockDuration = TimeSpan.FromSeconds(30),
                MaxDeliveryCount = 3,
            }, timeout.Token);
        Assert.AreEqual(3, createdSubscription.Value.MaxDeliveryCount);
        Assert.IsTrue(await admin.RuleExistsAsync(entityName, "consumer", "$Default", timeout.Token));

        var client = new ServiceBusClient(emulator.ConnectionString);
        ServiceBusSender sender = client.CreateSender(entityName);
        ServiceBusReceiver receiver = client.CreateReceiver(entityName, "consumer");
        await sender.SendMessageAsync(new ServiceBusMessage("hello") { MessageId = "m1" }, timeout.Token)
            .WaitAsync(TimeSpan.FromSeconds(10));
        ServiceBusReceivedMessage? peeked = await receiver.PeekMessageAsync(cancellationToken: timeout.Token);
        Assert.IsNotNull(peeked);
        Assert.AreEqual("m1", peeked.MessageId);
        ServiceBusReceivedMessage? received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10))
            .WaitAsync(TimeSpan.FromSeconds(15));

        Assert.IsNotNull(received);
        Assert.AreEqual("hello", received.Body.ToString());
        Assert.AreEqual("m1", received.MessageId);
        Assert.AreEqual(1, received.DeliveryCount);
        Assert.IsTrue(Guid.TryParse(received.LockToken, out var lockToken));
        Assert.AreEqual(16, lockToken.ToByteArray().Length);
        await receiver.CompleteMessageAsync(received);
        await admin.DeleteRuleAsync(entityName, "consumer", "$Default", timeout.Token);
        Assert.IsFalse(await admin.RuleExistsAsync(entityName, "consumer", "$Default", timeout.Token));
        await admin.DeleteTopicAsync(entityName, timeout.Token);
    }

    [TestMethod]
    [TestCategory("CommonFidelity")]
    [Timeout(60_000)]
    public async Task Stock_sdk_session_receiver_preserves_fifo_under_an_explicit_lock()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var entityName = $"session-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        await admin.CreateTopicAsync(entityName, timeout.Token);
        await admin.CreateSubscriptionAsync(
            new CreateSubscriptionOptions(entityName, "consumer") { RequiresSession = true },
            timeout.Token);

        var client = new ServiceBusClient(emulator.ConnectionString);
        ServiceBusSender sender = client.CreateSender(entityName);
        await sender.SendMessagesAsync(
            [
                new ServiceBusMessage("first") { SessionId = "S" },
                new ServiceBusMessage("second") { SessionId = "S" },
            ],
            timeout.Token);

        ServiceBusSessionReceiver receiver = await client.AcceptSessionAsync(
            entityName,
            "consumer",
            "S",
            cancellationToken: timeout.Token);
        await receiver.SetSessionStateAsync(BinaryData.FromString("state"), timeout.Token);
        BinaryData? state = await receiver.GetSessionStateAsync(timeout.Token);
        Assert.AreEqual("state", state?.ToString());
        // ReceiveMessagesAsync may return fewer than maxMessages (the SDK only
        // waits a short batch window after the first message), so accumulate
        // until both arrive; the outer timeout token bounds the loop.
        var messages = new List<ServiceBusReceivedMessage>();
        while (messages.Count < 2)
        {
            messages.AddRange(await receiver.ReceiveMessagesAsync(2 - messages.Count, TimeSpan.FromSeconds(5), timeout.Token));
        }
        Assert.HasCount(2, messages);
        Assert.AreEqual("first", messages[0].Body.ToString());
        Assert.AreEqual("second", messages[1].Body.ToString());
        await receiver.CompleteMessageAsync(messages[0], timeout.Token);
        await receiver.CompleteMessageAsync(messages[1], timeout.Token);
        await admin.DeleteTopicAsync(entityName, timeout.Token);
    }

    [TestMethod]
    [TestCategory("CommonFidelity")]
    [Timeout(60_000)]
    public async Task Stock_sdk_contended_explicit_session_reports_session_cannot_be_locked()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var entityName = $"contended-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        await admin.CreateTopicAsync(entityName, timeout.Token);
        await admin.CreateSubscriptionAsync(
            new CreateSubscriptionOptions(entityName, "consumer") { RequiresSession = true },
            timeout.Token);

        await using var client = new ServiceBusClient(
            emulator.ConnectionString,
            new ServiceBusClientOptions
            {
                RetryOptions = new ServiceBusRetryOptions
                {
                    MaxRetries = 0,
                    TryTimeout = TimeSpan.FromSeconds(2),
                },
            });
        await client.CreateSender(entityName).SendMessageAsync(
            new ServiceBusMessage("held") { SessionId = "S" },
            timeout.Token);
        await using ServiceBusSessionReceiver first = await client.AcceptSessionAsync(
            entityName,
            "consumer",
            "S",
            cancellationToken: timeout.Token);

        var exception = await Assert.ThrowsExactlyAsync<ServiceBusException>(() =>
            client.AcceptSessionAsync(entityName, "consumer", "S", cancellationToken: timeout.Token));
        Assert.AreEqual(ServiceBusFailureReason.SessionCannotBeLocked, exception.Reason);
    }

    [TestMethod]
    [TestCategory("CommonFidelity")]
    [Timeout(60_000)]
    public async Task Stock_sdk_stale_session_receiver_cannot_change_the_current_owners_state()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var entityName = $"ownership-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        await admin.CreateTopicAsync(entityName, timeout.Token);
        await admin.CreateSubscriptionAsync(
            new CreateSubscriptionOptions(entityName, "consumer")
            {
                RequiresSession = true,
                LockDuration = TimeSpan.FromSeconds(5),
            },
            timeout.Token);

        await using var client = new ServiceBusClient(emulator.ConnectionString);
        await client.CreateSender(entityName).SendMessageAsync(
            new ServiceBusMessage("held") { SessionId = "S" },
            timeout.Token);
        await using ServiceBusSessionReceiver stale = await client.AcceptSessionAsync(
            entityName,
            "consumer",
            "S",
            cancellationToken: timeout.Token);
        await stale.SetSessionStateAsync(BinaryData.FromString("stale"), timeout.Token);
        await Task.Delay(TimeSpan.FromSeconds(6), timeout.Token);
        await using ServiceBusSessionReceiver current = await client.AcceptSessionAsync(
            entityName,
            "consumer",
            "S",
            cancellationToken: timeout.Token);

        var exception = await Assert.ThrowsExactlyAsync<ServiceBusException>(() =>
            stale.SetSessionStateAsync(BinaryData.FromString("overwritten"), timeout.Token));
        Assert.AreEqual(ServiceBusFailureReason.SessionLockLost, exception.Reason);
        Assert.AreEqual("stale", (await current.GetSessionStateAsync(timeout.Token))?.ToString());
    }

    [TestMethod]
    [TestCategory("CommonFidelity")]
    [Timeout(60_000)]
    public async Task Stock_sdk_schedules_and_cancels_messages()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var entityName = $"schedule-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        await admin.CreateTopicAsync(entityName, timeout.Token);
        await admin.CreateSubscriptionAsync(entityName, "consumer", timeout.Token);

        var client = new ServiceBusClient(emulator.ConnectionString);
        ServiceBusSender sender = client.CreateSender(entityName);
        ServiceBusReceiver receiver = client.CreateReceiver(entityName, "consumer");
        var due = DateTimeOffset.UtcNow.AddMilliseconds(500);
        long cancelled = await sender.ScheduleMessageAsync(new ServiceBusMessage("cancelled"), due, timeout.Token);
        await sender.CancelScheduledMessageAsync(cancelled, timeout.Token);
        await sender.ScheduleMessageAsync(new ServiceBusMessage("delivered"), due, timeout.Token);

        ServiceBusReceivedMessage? received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5), timeout.Token);
        Assert.IsNotNull(received);
        Assert.AreEqual("delivered", received.Body.ToString());
        await receiver.CompleteMessageAsync(received, timeout.Token);
        Assert.IsNull(await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(500), timeout.Token));
        await admin.DeleteTopicAsync(entityName, timeout.Token);
    }

    [TestMethod]
    [TestCategory("EmulatorOnly")]
    [Timeout(30_000)]
    public async Task Admin_plane_exposes_only_health_without_authentication()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        using var client = new HttpClient();
        using HttpResponseMessage health = await client.GetAsync(new Uri(emulator.HttpEndpoint, "health"));
        using HttpResponseMessage admin = await client.GetAsync(new Uri(emulator.HttpEndpoint, "$Resources/topics?api-version=2021-05"));
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, admin.StatusCode);
    }

    [TestMethod]
    [TestCategory("EmulatorOnly")]
    [Timeout(60_000)]
    public async Task Stock_sdk_provisioner_second_apply_has_zero_mutations()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var mutations = new List<string>();
        var provisioner = new ServiceBusTopologyProvisioner(
            emulator.ConnectionString,
            static () => new TestPlatform(new TestEndpoint("orders")),
            mutations.Add);

        await provisioner.ApplyAsync(timeout.Token);
        Assert.IsNotEmpty(mutations);
        mutations.Clear();

        await provisioner.ApplyAsync(timeout.Token);

        Assert.HasCount(0, mutations);
    }

    [TestMethod]
    [TestCategory("EmulatorOnly")]
    [Timeout(60_000)]
    public async Task Process_restart_replays_topology_but_not_messages()
    {
        var journalPath = Path.Combine(Path.GetTempPath(), $"nimbus-sbemulator-restart-{Guid.NewGuid():N}", "topology.json");
        var entityName = $"restart-{Guid.NewGuid():N}";
        await using (var first = await EmulatorProcess.StartAsync(journalPath, deleteJournalOnDispose: false))
        {
            var admin = new ServiceBusAdministrationClient(first.ConnectionString);
            await admin.CreateTopicAsync(entityName);
            await admin.CreateSubscriptionAsync(entityName, "consumer");
            await using var client = new ServiceBusClient(first.ConnectionString);
            await client.CreateSender(entityName).SendMessageAsync(new ServiceBusMessage("volatile"));
        }

        await using (var second = await EmulatorProcess.StartAsync(journalPath, deleteJournalOnDispose: true))
        {
            var admin = new ServiceBusAdministrationClient(second.ConnectionString);
            Assert.IsTrue(await admin.TopicExistsAsync(entityName));
            Assert.IsTrue(await admin.SubscriptionExistsAsync(entityName, "consumer"));
            await using var client = new ServiceBusClient(second.ConnectionString);
            var receiver = client.CreateReceiver(entityName, "consumer");
            Assert.IsNull(await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(500)));
            await client.CreateSender(entityName).SendMessageAsync(new ServiceBusMessage("after-restart"));
            var received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(received);
            Assert.AreEqual("after-restart", received.Body.ToString());
        }
    }

    [TestMethod]
    [TestCategory("CommonFidelity")]
    [Timeout(60_000)]
    public async Task Auto_forward_pause_accumulates_backlog_and_resume_drains_it()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        var source = $"source-{Guid.NewGuid():N}";
        var target = $"target-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        await admin.CreateTopicAsync(source);
        await admin.CreateTopicAsync(target);
        await admin.CreateSubscriptionAsync(target, "consumer");
        await admin.CreateSubscriptionAsync(new CreateSubscriptionOptions(source, "forward") { ForwardTo = target });

        SubscriptionProperties paused = (await admin.GetSubscriptionAsync(source, "forward")).Value;
        paused.ForwardTo = string.Empty;
        paused.Status = EntityStatus.ReceiveDisabled;
        await admin.UpdateSubscriptionAsync(paused);

        await using var client = new ServiceBusClient(emulator.ConnectionString);
        var sender = client.CreateSender(source);
        await sender.SendMessageAsync(new ServiceBusMessage("backlog") { SessionId = "S" });
        SubscriptionRuntimeProperties backlog = (await admin.GetSubscriptionRuntimePropertiesAsync(source, "forward")).Value;
        Assert.AreEqual(1, backlog.ActiveMessageCount);

        SubscriptionProperties resumed = (await admin.GetSubscriptionAsync(source, "forward")).Value;
        resumed.Status = EntityStatus.Active;
        resumed.ForwardTo = target;
        await admin.UpdateSubscriptionAsync(resumed);

        var receiver = client.CreateReceiver(target, "consumer");
        ServiceBusReceivedMessage? received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        Assert.IsNotNull(received);
        Assert.AreEqual("backlog", received.Body.ToString());
        await receiver.CompleteMessageAsync(received);
        await admin.DeleteTopicAsync(source);
        await admin.DeleteTopicAsync(target);
    }

    [TestMethod]
    [TestCategory("EmulatorOnly")]
    [Timeout(120_000)]
    public async Task Session_processor_activates_two_hundred_sessions_on_one_client()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        var entityName = $"sessions-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        await admin.CreateTopicAsync(entityName);
        await admin.CreateSubscriptionAsync(new CreateSubscriptionOptions(entityName, "consumer") { RequiresSession = true });
        await using var client = new ServiceBusClient(emulator.ConnectionString);
        var processor = client.CreateSessionProcessor(
            entityName,
            "consumer",
            new ServiceBusSessionProcessorOptions
            {
                AutoCompleteMessages = false,
                MaxConcurrentSessions = 200,
                MaxConcurrentCallsPerSession = 1,
                SessionIdleTimeout = TimeSpan.FromSeconds(2),
            });
        var sessions = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new ConcurrentQueue<Exception>();
        processor.ProcessMessageAsync += async args =>
        {
            sessions.TryAdd(args.Message.SessionId, 0);
            await args.CompleteMessageAsync(args.Message);
            if (sessions.Count == 200)
            {
                completed.TrySetResult();
            }
        };
        processor.ProcessErrorAsync += args =>
        {
            errors.Enqueue(args.Exception);
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync();
        var messages = Enumerable.Range(0, 200)
            .Select(index => new ServiceBusMessage(index.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                SessionId = $"S-{index}",
            })
            .ToArray();
        await client.CreateSender(entityName).SendMessagesAsync(messages);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await processor.StopProcessingAsync();

        Assert.HasCount(200, sessions);
        Assert.HasCount(0, errors);
        await admin.DeleteTopicAsync(entityName);
    }

    [TestMethod]
    [TestCategory("CommonFidelity")]
    [Timeout(60_000)]
    public async Task Broker_assigns_a_message_id_when_the_sender_omits_it()
    {
        // NimBus's ResponseService.CreateResponse relies on the broker assigning the
        // outgoing response its own id when the sender leaves MessageId unset (the
        // response to a CloudEvents-ingested message without a native MessageId).
        // Without this, such responses poison-loop the Resolver with
        // InvalidMessageException("MessageId is not defined.").
        await using var emulator = await EmulatorProcess.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var entityName = $"noid-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        await admin.CreateTopicAsync(entityName, timeout.Token);
        await admin.CreateSubscriptionAsync(new CreateSubscriptionOptions(entityName, "consumer"), timeout.Token);

        await using var client = new ServiceBusClient(emulator.ConnectionString);
        await client.CreateSender(entityName).SendMessageAsync(new ServiceBusMessage("no-id"), timeout.Token);

        ServiceBusReceiver receiver = client.CreateReceiver(entityName, "consumer");
        ServiceBusReceivedMessage? received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10), timeout.Token);
        Assert.IsNotNull(received);
        Assert.IsFalse(string.IsNullOrEmpty(received.MessageId));
        await receiver.CompleteMessageAsync(received, timeout.Token);
        await admin.DeleteTopicAsync(entityName, timeout.Token);
    }

    [TestMethod]
    [TestCategory("CommonFidelity")]
    [Timeout(60_000)]
    public async Task Abandon_increments_delivery_count_and_max_delivery_moves_to_dlq()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        var entityName = $"delivery-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        await admin.CreateTopicAsync(entityName);
        await admin.CreateSubscriptionAsync(
            new CreateSubscriptionOptions(entityName, "consumer") { MaxDeliveryCount = 3 });
        await using var client = new ServiceBusClient(emulator.ConnectionString);
        await client.CreateSender(entityName).SendMessageAsync(new ServiceBusMessage("retry"));
        var receiver = client.CreateReceiver(entityName, "consumer");

        for (var expectedDelivery = 1; expectedDelivery <= 3; expectedDelivery++)
        {
            ServiceBusReceivedMessage? received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(received);
            Assert.AreEqual(expectedDelivery, received.DeliveryCount);
            await receiver.AbandonMessageAsync(received);
        }

        SubscriptionRuntimeProperties runtime = (await admin.GetSubscriptionRuntimePropertiesAsync(entityName, "consumer")).Value;
        Assert.AreEqual(0, runtime.ActiveMessageCount);
        Assert.AreEqual(1, runtime.DeadLetterMessageCount);
        Assert.AreEqual(1, runtime.TotalMessageCount);
        await admin.DeleteTopicAsync(entityName);
    }

    private sealed class EmulatorProcess : IAsyncDisposable
    {
        private readonly Process? _process;
        private readonly string? _journalPath;
        private readonly bool _deleteJournalOnDispose;

        private EmulatorProcess(
            Process? process,
            int port,
            string? connectionString = null,
            string? journalPath = null,
            bool deleteJournalOnDispose = true)
        {
            _process = process;
            _journalPath = journalPath;
            _deleteJournalOnDispose = deleteJournalOnDispose;
            ConnectionString = connectionString ?? $"Endpoint=sb://127.0.0.1:{port};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=;UseDevelopmentEmulator=true";
            var endpoint = ConnectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Single(part => part.StartsWith("Endpoint=", StringComparison.OrdinalIgnoreCase))["Endpoint=".Length..];
            HttpEndpoint = new UriBuilder(new Uri(endpoint)) { Scheme = Uri.UriSchemeHttp }.Uri;
        }

        public string ConnectionString { get; }

        public Uri HttpEndpoint { get; }

        public static async Task<EmulatorProcess> StartAsync(string? journalPath = null, bool deleteJournalOnDispose = true)
        {
            if (System.Environment.GetEnvironmentVariable("NIMBUS_SBEMULATOR_TEST_CS") is { Length: > 0 } existing)
            {
                return new EmulatorProcess(null, 0, existing);
            }

            var port = GetAvailablePort();
            var project = FindProject();
            journalPath ??= Path.Combine(Path.GetTempPath(), $"nimbus-sbemulator-test-{Guid.NewGuid():N}", "topology.json");
            var startInfo = new ProcessStartInfo("dotnet")
            {
                Arguments = $"run --no-build --configuration \"{GetBuildConfiguration()}\" --project \"{project}\" -- --port {port}",
                WorkingDirectory = Path.GetDirectoryName(project),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.Environment["NIMBUS_SBEMULATOR_TOPOLOGY_PATH"] = journalPath;
            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the emulator process.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var instance = new EmulatorProcess(
                process,
                port,
                journalPath: journalPath,
                deleteJournalOnDispose: deleteJournalOnDispose);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException($"The emulator exited during startup with code {process.ExitCode}.");
                }

                try
                {
                    using var response = await client.GetAsync($"http://127.0.0.1:{port}/health");
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        return instance;
                    }
                }
                catch (HttpRequestException)
                {
                }
                catch (TaskCanceledException)
                {
                }

                await Task.Delay(50);
            }

            await instance.DisposeAsync();
            throw new TimeoutException("The emulator did not become healthy.");
        }

        private static string GetBuildConfiguration()
        {
            return new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
                ?? throw new InvalidOperationException("Could not determine the active test build configuration.");
        }

        public ValueTask DisposeAsync()
        {
            if (_process is null)
            {
                return ValueTask.CompletedTask;
            }

            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5_000);
            }

            _process.Dispose();
            if (_deleteJournalOnDispose && _journalPath is not null &&
                Path.GetDirectoryName(_journalPath) is { } directory && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }

        private static int GetAvailablePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static string FindProject()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "src", "NimBus.ServiceBusEmulator", "NimBus.ServiceBusEmulator.csproj");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not locate the emulator project.");
        }
    }

    private sealed class TestPlatform : Platform
    {
        public TestPlatform(params IEndpoint[] endpoints)
        {
            foreach (var endpoint in endpoints)
            {
                AddEndpoint(endpoint);
            }
        }
    }

    private sealed class TestEndpoint(string id) : IEndpoint
    {
        public string Id { get; } = id;
        public string Name => Id;
        public string Description => string.Empty;
        public string Namespace => "Tests";
        public string SecurityGroupName => string.Empty;
        public ISystem System => null!;
        public IEnumerable<IEventType> EventTypesProduced => [];
        public IEnumerable<IEventType> EventTypesConsumed => [];
        public IEnumerable<IRoleAssignment> RoleAssignments => [];
    }
}
