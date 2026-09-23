#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Events.Customers;
using NimBus.Events.Orders;
using NimBus.SDK;
using NimBus.Testing;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.Simulation;

namespace NimBus.WebApp.Tests.Simulation;

/// <summary>
/// Each failure mode, driven through the production handler composition
/// (<see cref="SimulatedEndpointHost.BuildMessageHandler"/>) over the in-memory transport, and
/// asserted on the Resolver response a real handler failing the same way produces.
/// </summary>
[TestClass]
public sealed class SimulatedHandlerBehaviorTests
{
    private static readonly string[] BillingTypes =
    {
        nameof(OrderPlaced), nameof(OrderDeliveryDetailsCaptured), nameof(PlaceCustomerOnCreditHold),
    };

    [TestMethod]
    public async Task Healthy_completes_with_a_resolution_response()
    {
        var harness = new Harness();

        var result = await harness.DeliverAsync(new SimulatedFailure());

        Assert.IsNull(result.Exception);
        Assert.IsTrue(result.Context.IsCompleted);
        CollectionAssert.AreEqual(new[] { MessageType.ResolutionResponse }, harness.ResponseTypes());
        Assert.AreEqual(SimulatedDeliveryOutcome.Completed, harness.Feed.Deliveries.Single().Outcome);
    }

    [TestMethod]
    public async Task Random_at_100_percent_errors_blocks_and_schedules_a_retry()
    {
        var harness = new Harness();

        var result = await harness.DeliverAsync(new SimulatedFailure { Mode = SimulatedFailureMode.Random, Rate = 100, ExceptionMessage = "boom" });

        Assert.IsTrue(await result.Context.IsSessionBlocked());
        CollectionAssert.AreEquivalent(new[] { MessageType.ErrorResponse, MessageType.RetryRequest }, harness.ResponseTypes());
        var delivery = harness.Feed.Deliveries.Single();
        Assert.AreEqual(SimulatedDeliveryOutcome.Threw, delivery.Outcome);
        Assert.AreEqual("boom", delivery.Error);
        Assert.AreEqual(1, delivery.Attempt);
    }

    [TestMethod]
    public async Task Random_after_the_last_retry_errors_without_scheduling_another()
    {
        var harness = new Harness();

        await harness.DeliverAsync(new SimulatedFailure { Mode = SimulatedFailureMode.Random, Rate = 100 }, retryCount: SimulationLimits.SimulatorMaxRetries);

        CollectionAssert.AreEqual(new[] { MessageType.ErrorResponse }, harness.ResponseTypes(),
            "The simulator's fixed policy allows 3 retries; the 4th attempt's failure is final.");
        Assert.AreEqual(SimulationLimits.SimulatorMaxRetries + 1, harness.Feed.Deliveries.Single().Attempt);
    }

    [TestMethod]
    public async Task Transient_fails_its_attempts_then_completes()
    {
        var failure = new SimulatedFailure { Mode = SimulatedFailureMode.Transient, FailAttempts = 2 };

        for (var retryCount = 0; retryCount < 2; retryCount++)
        {
            var failing = new Harness();
            await failing.DeliverAsync(failure, retryCount: retryCount);
            CollectionAssert.AreEquivalent(new[] { MessageType.ErrorResponse, MessageType.RetryRequest }, failing.ResponseTypes(), $"retryCount {retryCount}");
        }

        var succeeding = new Harness();
        var result = await succeeding.DeliverAsync(failure, retryCount: 2);
        Assert.IsNull(result.Exception);
        CollectionAssert.AreEqual(new[] { MessageType.ResolutionResponse }, succeeding.ResponseTypes());
    }

    [TestMethod]
    public async Task Poison_is_dead_lettered_by_the_simulator_classifier()
    {
        var harness = new Harness();

        var result = await harness.DeliverAsync(new SimulatedFailure { Mode = SimulatedFailureMode.Poison });

        Assert.IsTrue(result.Context.IsDeadLettered);
        Assert.IsFalse(harness.ResponseTypes().Contains(MessageType.RetryRequest), "Poison must not consume retry budget.");
        Assert.AreEqual(SimulatedDeliveryOutcome.Poisoned, harness.Feed.Deliveries.Single().Outcome);
    }

    [TestMethod]
    public async Task NoHandler_produces_an_unsupported_response()
    {
        var harness = new Harness();

        var result = await harness.DeliverAsync(new SimulatedFailure { Mode = SimulatedFailureMode.NoHandler });

        Assert.IsTrue(result.Context.IsCompleted);
        CollectionAssert.AreEqual(new[] { MessageType.UnsupportedResponse }, harness.ResponseTypes());
        Assert.AreEqual(SimulatedDeliveryOutcome.Unsupported, harness.Feed.Deliveries.Single().Outcome);
    }

    [TestMethod]
    public async Task Slow_delays_between_the_latency_bounds()
    {
        var harness = new Harness();

        await harness.DeliverAsync(new SimulatedFailure { Mode = SimulatedFailureMode.Slow, LatencyMinMs = 150, LatencyMaxMs = 200 });

        Assert.IsTrue(harness.Feed.Deliveries.Single().LatencyMs >= 140);
        CollectionAssert.AreEqual(new[] { MessageType.ResolutionResponse }, harness.ResponseTypes());
    }

    [TestMethod]
    public async Task Event_type_scope_leaves_other_types_healthy()
    {
        var failure = new SimulatedFailure { Mode = SimulatedFailureMode.Poison, EventTypeIds = new[] { nameof(OrderDeliveryDetailsCaptured) } };

        var outOfScope = new Harness();
        var result = await outOfScope.DeliverAsync(failure);
        Assert.IsTrue(result.Context.IsCompleted, "OrderPlaced is outside the scope and must complete.");

        var inScope = new Harness();
        var scoped = await inScope.DeliverAsync(failure, eventType: new EventType(typeof(OrderDeliveryDetailsCaptured)));
        Assert.IsTrue(scoped.Context.IsDeadLettered);
    }

    [TestMethod]
    public async Task Session_glob_scopes_the_mode()
    {
        var failure = new SimulatedFailure { Mode = SimulatedFailureMode.Poison, SessionPattern = "sim-*-00?" };

        var matching = new Harness();
        Assert.IsTrue((await matching.DeliverAsync(failure, sessionId: "sim-StorefrontEndpoint-007")).Context.IsDeadLettered);

        var other = new Harness();
        Assert.IsTrue((await other.DeliverAsync(failure, sessionId: "sim-StorefrontEndpoint-017")).Context.IsCompleted);
    }

    [TestMethod]
    public async Task Mode_reverts_to_healthy_after_its_duration()
    {
        var clock = new ManualClock();
        var failure = new SimulatedFailure { Mode = SimulatedFailureMode.Poison, RevertAfterMinutes = 5 };

        var before = new Harness(clock);
        Assert.IsTrue((await before.DeliverAsync(failure)).Context.IsDeadLettered);

        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.AreEqual(SimulatedFailureMode.Poison, before.Behavior.EffectiveMode);

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual(SimulatedFailureMode.Healthy, before.Behavior.EffectiveMode);
        var after = await before.DeliverAsync(failure: null);
        Assert.IsTrue(after.Context.IsCompleted);
    }

    [TestMethod]
    public async Task Random_rate_is_honoured_within_tolerance_on_a_seeded_run()
    {
        // The behavior alone, concurrently: the pipeline around it is covered above, and
        // 200 sequential deliveries through it would spend ~10 s in the per-delivery delay.
        var feed = new RecordingFeedSink();
        var behavior = new SimulatedHandlerBehavior(SimulationTestPlatform.Billing, feed, random: new Random(1234));
        behavior.Update(new SimulatedFailure { Mode = SimulatedFailureMode.Random, Rate = 30 }, DateTimeOffset.UtcNow);

        await Task.WhenAll(Enumerable.Range(0, 200).Select(async i =>
        {
            var context = new InMemoryMessageContext(
                new Message { EventTypeId = nameof(OrderPlaced), SessionId = $"sim-rate-{i}", MessageId = i.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                new InMemorySessionState());
            try
            {
                await behavior.HandleAsync(context, CancellationToken.None);
            }
            catch (SimulatedTransientException)
            {
                // Expected for roughly 30 % of deliveries.
            }
        }));

        var threw = feed.Deliveries.Count(d => d.Outcome == SimulatedDeliveryOutcome.Threw);
        Assert.IsTrue(threw is >= 40 and <= 80, $"Expected about 60 of 200 to fail at 30 %, got {threw}.");
    }

    [TestMethod]
    public async Task Stopping_token_cancels_a_slow_handler_without_a_failure_response()
    {
        using var stopping = new CancellationTokenSource();
        var harness = new Harness(stopping: stopping.Token);
        harness.Behavior.Update(new SimulatedFailure { Mode = SimulatedFailureMode.Slow, LatencyMinMs = 10_000, LatencyMaxMs = 10_000 }, DateTimeOffset.UtcNow);

        var delivery = harness.DeliverAsync(failure: null);
        await Task.Delay(100);
        await stopping.CancelAsync();
        var result = await delivery.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsInstanceOfType<OperationCanceledException>(result.Exception);
        Assert.AreEqual(0, harness.ResponseTypes().Length, "A stop is cooperative shutdown, not a handler failure.");
    }

    private sealed class Harness
    {
        private readonly InMemoryMessageBus _publish = new();
        private readonly InMemoryMessageBus _responses = new();
        private readonly IMessageHandler _handler;
        private readonly FakeEventPayloadGenerator _generator = new();

        public Harness(TimeProvider? clock = null, Random? random = null, CancellationToken stopping = default)
        {
            Behavior = new SimulatedHandlerBehavior(SimulationTestPlatform.Billing, Feed, clock, random ?? new Random(7));
            _handler = SimulatedEndpointHost.BuildMessageHandler(
                SimulationTestPlatform.Billing, BillingTypes, Behavior, _responses, NullLoggerFactory.Instance, stopping);
        }

        public RecordingFeedSink Feed { get; } = new();

        public SimulatedHandlerBehavior Behavior { get; }

        public MessageType[] ResponseTypes() => _responses.SentMessages.Select(m => m.MessageType).ToArray();

        public async Task<InMemoryDeliveryResult> DeliverAsync(
            SimulatedFailure? failure,
            int? retryCount = null,
            string? sessionId = null,
            IEventType? eventType = null)
        {
            if (failure is not null)
                Behavior.Update(failure, Behavior.AppliedAt == default ? DateTimeOffset.UtcNow : Behavior.AppliedAt);

            eventType ??= new EventType(typeof(OrderPlaced));
            var @event = new FakePayloadSimulationEventFactory(_generator).Create(eventType);
            await new PublisherClient(_publish).Publish(@event, sessionId ?? $"sim-{Guid.NewGuid():N}", "sim-corr", Guid.NewGuid().ToString());
            var message = (Message)_publish.SentMessages[^1];
            message.To = SimulationTestPlatform.Billing;
            message.RetryCount = retryCount;
            // What the Service Bus wire carries on a first hop (MessageHelper / MessageContext
            // default the originating id to "self"); a bare in-memory Message leaves it null.
            message.OriginatingMessageId ??= NimBus.Core.Messages.Constants.Self;
            message.EventId ??= Guid.NewGuid().ToString();

            var results = await _publish.DeliverAllWithResults(_handler);
            return results.Single();
        }
    }
}
