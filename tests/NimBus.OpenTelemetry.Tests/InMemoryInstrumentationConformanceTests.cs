#pragma warning disable CA1707, CA2007
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Diagnostics;
using NimBus.Core.Messages;
using NimBus.Testing;
using NimBus.Testing.Conformance;

namespace NimBus.OpenTelemetry.Tests;

[TestClass]
public sealed class InMemoryInstrumentationConformanceTests : InstrumentationConformanceTests
{
    protected override string MessagingSystem => Core.Diagnostics.MessagingSystem.InMemory;

    protected override async Task<ActivityContext> PublishAsync(IMessage message)
    {
        // Wire the publisher leg: instrumented sender wrapping the in-memory bus.
        // The publish span ends as soon as Send returns, so to capture its
        // ActivityContext we listen on the source and grab the span out as it stops.
        ActivityContext captured = default;
        var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == NimBusInstrumentation.PublisherActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                if (a.OperationName == "publish endpoint-1")
                    captured = a.Context;
            },
        };
        ActivitySource.AddActivityListener(listener);

        try
        {
            var bus = new InMemoryMessageBus();
            var instrumented = NimBusOpenTelemetryDecorators.InstrumentSender(bus, MessagingSystem);
            await instrumented.Send(message);
        }
        finally
        {
            listener.Dispose();
        }

        return captured;
    }
}
