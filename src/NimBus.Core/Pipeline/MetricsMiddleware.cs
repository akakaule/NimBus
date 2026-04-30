using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;

namespace NimBus.Core.Pipeline;

/// <summary>
/// Middleware that records message processing metrics: duration histogram, success/failure counters.
/// Metrics are published via System.Diagnostics.Metrics and collected by OpenTelemetry.
/// </summary>
public sealed class MetricsMiddleware : IMessagePipelineBehavior
{
    private static readonly Meter s_meter = new("NimBus.Pipeline");

    private static readonly Histogram<double> s_duration = s_meter.CreateHistogram<double>(
        "nimbus.pipeline.duration", "ms", "Time spent processing a message through the pipeline");

    private static readonly Counter<long> s_processed = s_meter.CreateCounter<long>(
        "nimbus.pipeline.processed", "messages", "Total messages processed");

    private static readonly Counter<long> s_failed = s_meter.CreateCounter<long>(
        "nimbus.pipeline.failed", "messages", "Total messages that failed processing");

    public async Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var tags = new TagList
        {
            { "messaging.event_type", context.EventTypeId ?? "unknown" },
            { "messaging.message_type", context.MessageType.ToString() },
        };

        try
        {
            await next(context, cancellationToken);
            sw.Stop();

            s_duration.Record(sw.Elapsed.TotalMilliseconds, tags);
            s_processed.Add(1, tags);
            // Stash on the context so ResponseService can carry it onto the
            // outgoing response and the Resolver can persist per-message timings.
            context.ProcessingTimeMs = sw.ElapsedMilliseconds;
        }
        catch (Exception)
        {
            sw.Stop();

            s_duration.Record(sw.Elapsed.TotalMilliseconds, tags);
            s_failed.Add(1, tags);
            context.ProcessingTimeMs = sw.ElapsedMilliseconds;

            throw;
        }
    }
}
