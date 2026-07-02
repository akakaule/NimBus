#pragma warning disable CA1707, CA2007

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Agents;
using NimBus.Agents.Internal;

namespace NimBus.Agents.Tests;

[TestClass]
public class AgentLoopWorkerTests
{
    private sealed record Ping(string Value);

    private static HandoffCoordinates Coords(string eventId = "e1", string sessionId = "s1") =>
        new(eventId, sessionId, "m1", "Ping", "c1", "o1");

    private static AgentReceivedMessage Msg(string payload = "{\"value\":\"hi\"}", string sessionId = "s1") =>
        new("Ping", payload, Coords(sessionId: sessionId));

    private static AgentOptions Options(bool withOutput = false)
    {
        var o = new AgentOptions { AgentId = "test" }.Subscribe("Ping");
        if (withOutput)
            o.DefineOutput("out.v1", "{\"type\":\"object\"}", "Out");
        return o;
    }

    private static AgentLoopWorker<Ping> NewWorker(IAgentBusGateway bus, IAgentHandler<Ping> handler, AgentOptions? options = null) =>
        new(bus, handler, options ?? Options(), NullLogger<AgentLoopWorker<Ping>>.Instance);

    [TestMethod]
    public async Task ProcessNextAsync_HappyPath_PublishesThenSettlesComplete_SessionInherited()
    {
        var bus = new FakeGateway(Msg(sessionId: "session-xyz"));
        var handler = new DelegateHandler(_ => AgentResult.Complete(new PublishSpec("out.v1", "{\"ok\":true}")));
        var worker = NewWorker(bus, handler);

        var processed = await worker.ProcessNextAsync(CancellationToken.None);

        Assert.IsTrue(processed);
        Assert.AreEqual(1, handler.Calls);
        Assert.AreEqual(1, bus.Publishes.Count);
        Assert.AreEqual("out.v1", bus.Publishes[0].EventTypeId);
        Assert.AreEqual("session-xyz", bus.Publishes[0].SessionId, "Publish should inherit the received handoff session.");
        Assert.AreEqual(1, bus.Settles.Count);
        Assert.IsTrue(bus.Settles[0].Success, "Handoff should be settled complete.");
        Assert.IsTrue(bus.Calls.IndexOf("Publish") < bus.Calls.IndexOf("Settle"), "Publish must precede Settle.");
    }

    [TestMethod]
    public async Task ProcessNextAsync_NoMessage_ReturnsFalse_NoPublishOrSettle()
    {
        var bus = new FakeGateway(/* empty inbox */);
        var handler = new DelegateHandler(_ => AgentResult.Done());
        var worker = NewWorker(bus, handler);

        var processed = await worker.ProcessNextAsync(CancellationToken.None);

        Assert.IsFalse(processed);
        Assert.AreEqual(0, handler.Calls);
        Assert.AreEqual(0, bus.Publishes.Count);
        Assert.AreEqual(0, bus.Settles.Count);
    }

    [TestMethod]
    public async Task ProcessNextAsync_HandlerThrows_Propagates_NoSettle()
    {
        var bus = new FakeGateway(Msg());
        var handler = new DelegateHandler(_ => throw new InvalidOperationException("handler boom"));
        var worker = NewWorker(bus, handler);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => worker.ProcessNextAsync(CancellationToken.None));

        Assert.AreEqual(0, bus.Publishes.Count, "Nothing should be published when the handler throws.");
        Assert.AreEqual(0, bus.Settles.Count, "The handoff must stay parked (no settle) when the handler throws.");
    }

    [TestMethod]
    public async Task ProcessNextAsync_HandlerFails_SettlesFailWithErrorText_NoPublish()
    {
        var bus = new FakeGateway(Msg());
        var handler = new DelegateHandler(_ => AgentResult.Fail("rejected by rule", "BusinessRule"));
        var worker = NewWorker(bus, handler);

        var processed = await worker.ProcessNextAsync(CancellationToken.None);

        Assert.IsTrue(processed);
        Assert.AreEqual(0, bus.Publishes.Count);
        Assert.AreEqual(1, bus.Settles.Count);
        Assert.IsFalse(bus.Settles[0].Success);
        Assert.AreEqual("rejected by rule", bus.Settles[0].ErrorText);
        Assert.AreEqual("BusinessRule", bus.Settles[0].ErrorType);
    }

    [TestMethod]
    public async Task ProcessNextAsync_PoisonPayload_SettlesFail_WithoutInvokingHandler()
    {
        var bus = new FakeGateway(Msg(payload: "this is not json"));
        var handler = new DelegateHandler(_ => AgentResult.Done());
        var worker = NewWorker(bus, handler);

        var processed = await worker.ProcessNextAsync(CancellationToken.None);

        Assert.IsTrue(processed, "A poison message is consumed (settled fail), so the loop reports work done.");
        Assert.AreEqual(0, handler.Calls, "The handler must not run for an undeserializable payload.");
        Assert.AreEqual(0, bus.Publishes.Count);
        Assert.AreEqual(1, bus.Settles.Count);
        Assert.IsFalse(bus.Settles[0].Success);
        Assert.AreEqual("DeserializeError", bus.Settles[0].ErrorType);
    }

    [TestMethod]
    public async Task ProcessNextAsync_DuplicateSettle400_IsSwallowed()
    {
        var bus = new FakeGateway(Msg()) { SettleThrowStatus = HttpStatusCode.BadRequest };
        var handler = new DelegateHandler(_ => AgentResult.Done());
        var worker = NewWorker(bus, handler);

        // A concurrent receive already settled the handoff -> settle 400 -> swallowed (no rethrow).
        var processed = await worker.ProcessNextAsync(CancellationToken.None);
        Assert.IsTrue(processed);
    }

    [TestMethod]
    public async Task ProcessNextAsync_DefinesOutputs_OnlyOnce_AcrossTwoMessages()
    {
        var bus = new FakeGateway(Msg(), Msg());
        var handler = new DelegateHandler(_ => AgentResult.Done());
        var worker = NewWorker(bus, handler, Options(withOutput: true));

        Assert.IsTrue(await worker.ProcessNextAsync(CancellationToken.None));
        Assert.IsTrue(await worker.ProcessNextAsync(CancellationToken.None));

        Assert.AreEqual(1, bus.Defines.Count, "Output schema must be defined only once across messages.");
        Assert.AreEqual("out.v1", bus.Defines[0]);
        Assert.AreEqual(2, bus.Settles.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_NoMessage_WaitsIdleBackoffBeforePollingAgain()
    {
        // Empty inbox: every receive yields nothing. A long idle backoff means that after the
        // first empty poll the loop parks in the delay instead of spinning to a second poll.
        var bus = new SignalGateway(signalSettleAt: int.MaxValue);
        var handler = new DelegateHandler(_ => AgentResult.Done());
        var options = Options();
        options.IdleBackoff = TimeSpan.FromSeconds(30);
        var worker = NewWorker(bus, handler, options);

        await worker.StartAsync(CancellationToken.None);
        await bus.FirstReceived;
        // Cancel while the loop is parked in IdleBackoff; the delay throws and the loop exits.
        await worker.StopAsync(CancellationToken.None);

        Assert.AreEqual(1, bus.ReceiveCount,
            "An idle agent must wait IdleBackoff between polls, not receive back-to-back.");
    }

    [TestMethod]
    public async Task ExecuteAsync_ProcessesQueuedMessages_WithoutWaitingIdleBackoffBetweenThem()
    {
        // Two queued messages then empty. With a 30s idle backoff, both must still settle promptly:
        // successful receives loop immediately and only an empty receive triggers the backoff.
        var bus = new SignalGateway(signalSettleAt: 2, Msg(), Msg());
        var handler = new DelegateHandler(_ => AgentResult.Done());
        var options = Options();
        options.IdleBackoff = TimeSpan.FromSeconds(30);
        var worker = NewWorker(bus, handler, options);

        await worker.StartAsync(CancellationToken.None);

        var winner = await Task.WhenAny(bus.SignalReached, Task.Delay(TimeSpan.FromSeconds(5)));
        await worker.StopAsync(CancellationToken.None);

        Assert.AreSame(bus.SignalReached, winner,
            "Both queued handoffs must settle without waiting IdleBackoff between successful receives.");
    }

    private sealed class DelegateHandler : IAgentHandler<Ping>
    {
        private readonly Func<AgentContext<Ping>, AgentResult> _impl;
        public DelegateHandler(Func<AgentContext<Ping>, AgentResult> impl) => _impl = impl;
        public int Calls { get; private set; }

        public Task<AgentResult> HandleAsync(AgentContext<Ping> context, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_impl(context));
        }
    }

    private sealed class FakeGateway : IAgentBusGateway
    {
        private readonly Queue<AgentReceivedMessage?> _inbox;

        public FakeGateway(params AgentReceivedMessage?[] inbox) => _inbox = new Queue<AgentReceivedMessage?>(inbox);

        public List<string> Calls { get; } = new();
        public List<string> Defines { get; } = new();
        public List<(string EventTypeId, string Payload, string? SessionId)> Publishes { get; } = new();
        public List<(bool Success, string? Result, string? ErrorText, string? ErrorType)> Settles { get; } = new();
        public HttpStatusCode? SettleThrowStatus { get; set; }

        public Task SubscribeAsync(string eventTypeId, CancellationToken ct) { Calls.Add("Subscribe"); return Task.CompletedTask; }

        public Task DefineEventTypeAsync(string eventTypeId, string jsonSchema, string? name, string? description, string? sessionKeyPath, CancellationToken ct)
        {
            Calls.Add("Define");
            Defines.Add(eventTypeId);
            return Task.CompletedTask;
        }

        public Task<AgentReceivedMessage?> ReceiveAsync(string? eventTypeId, int waitSeconds, CancellationToken ct)
        {
            Calls.Add("Receive");
            return Task.FromResult(_inbox.Count > 0 ? _inbox.Dequeue() : null);
        }

        public Task PublishAsync(string eventTypeId, string payloadJson, string? sessionId, CancellationToken ct)
        {
            Calls.Add("Publish");
            Publishes.Add((eventTypeId, payloadJson, sessionId));
            return Task.CompletedTask;
        }

        public Task SettleAsync(HandoffCoordinates coordinates, bool success, string? result, string? errorText, string? errorType, CancellationToken ct)
        {
            Calls.Add("Settle");
            if (SettleThrowStatus is { } code)
                throw new HttpRequestException("settle failed", null, code);
            Settles.Add((success, result, errorText, errorType));
            return Task.CompletedTask;
        }
    }

    // Gateway that signals via TaskCompletionSource so loop-level tests observe progress
    // deterministically (no wall-clock assertions). Counters use Interlocked; they are read only
    // after the worker has stopped.
    private sealed class SignalGateway : IAgentBusGateway
    {
        private readonly Queue<AgentReceivedMessage?> _inbox;
        private readonly int _signalSettleAt;
        private int _receiveCount;
        private int _settleCount;
        private readonly TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstReceive = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SignalGateway(int signalSettleAt, params AgentReceivedMessage?[] inbox)
        {
            _signalSettleAt = signalSettleAt;
            _inbox = new Queue<AgentReceivedMessage?>(inbox);
        }

        public int ReceiveCount => Volatile.Read(ref _receiveCount);
        public Task FirstReceived => _firstReceive.Task;
        public Task SignalReached => _signal.Task;

        public Task SubscribeAsync(string eventTypeId, CancellationToken ct) => Task.CompletedTask;

        public Task DefineEventTypeAsync(string eventTypeId, string jsonSchema, string? name, string? description, string? sessionKeyPath, CancellationToken ct)
            => Task.CompletedTask;

        public Task<AgentReceivedMessage?> ReceiveAsync(string? eventTypeId, int waitSeconds, CancellationToken ct)
        {
            Interlocked.Increment(ref _receiveCount);
            _firstReceive.TrySetResult();
            return Task.FromResult(_inbox.Count > 0 ? _inbox.Dequeue() : null);
        }

        public Task PublishAsync(string eventTypeId, string payloadJson, string? sessionId, CancellationToken ct) => Task.CompletedTask;

        public Task SettleAsync(HandoffCoordinates coordinates, bool success, string? result, string? errorText, string? errorType, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _settleCount) >= _signalSettleAt)
                _signal.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
