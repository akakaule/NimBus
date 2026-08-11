using System.Xml.Linq;
using Microsoft.AspNetCore.Http.Features;
using NimBus.ServiceBusEmulator.Broker;
using NimBus.ServiceBusEmulator.Protocol;
using NimBus.ServiceBusEmulator.Storage;

namespace NimBus.ServiceBusEmulator.Admin;

internal static class AdminEndpoints
{
    public static void MapServiceBusAdmin(
        this WebApplication app,
        BrokerNamespace broker,
        Guid instanceId,
        TopologyJournal topologyJournal)
    {
        app.Use(async (context, next) =>
        {
            context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = 1024 * 1024;
            if (!context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase) &&
                !context.Request.Headers.Authorization.ToString().StartsWith("SharedAccessSignature ", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context).ConfigureAwait(false);
        });

        app.MapGet("/health", () => Results.Ok(new { status = "ok", instance = instanceId }));
        app.MapMethods("/{**entityPath}", ["GET", "PUT", "DELETE"], context =>
            HandleAsync(context, broker, topologyJournal));
    }

    private static async Task HandleAsync(HttpContext context, BrokerNamespace broker, TopologyJournal topologyJournal)
    {
        EmulatorDiagnostics.Write("HTTP admin", $"{context.Request.Method} {context.Request.Path}");
        var responseBody = context.Response.Body;
        await using var bufferedBody = new MemoryStream();
        context.Response.Body = bufferedBody;
        try
        {
            try
            {
                await DispatchAsync(context, broker).ConfigureAwait(false);
                if (context.Response.StatusCode < 400 &&
                    (context.Request.Method == HttpMethods.Put || context.Request.Method == HttpMethods.Delete))
                {
                    await topologyJournal.SaveAsync(broker, context.RequestAborted).ConfigureAwait(false);
                }
            }
            catch (BadHttpRequestException exception)
            {
                await WriteErrorAsync(context, exception.StatusCode, "BadRequest", exception.Message).ConfigureAwait(false);
            }
            catch (KeyNotFoundException exception)
            {
                await WriteErrorAsync(context, StatusCodes.Status404NotFound, "MessagingEntityNotFound", exception.Message).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                await WriteErrorAsync(context, StatusCodes.Status409Conflict, "MessagingEntityAlreadyExists", exception.Message).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is FormatException or System.Xml.XmlException)
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "BadRequest", exception.Message).ConfigureAwait(false);
            }
        }
        finally
        {
            context.Response.Body = responseBody;
            context.Response.ContentLength = bufferedBody.Length;
            bufferedBody.Position = 0;
            await bufferedBody.CopyToAsync(responseBody, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static async Task DispatchAsync(HttpContext context, BrokerNamespace broker)
    {
        var path = context.Request.Path.Value?.Trim('/') ?? string.Empty;
        if (context.Request.Method == HttpMethods.Get && path.Equals("$Resources/topics", StringComparison.OrdinalIgnoreCase))
        {
            var enrich = IsEnriched(context);
            await WriteXmlAsync(context, AdminXml.Feed(Page(context, broker.GetTopics()).Select(topic =>
                AdminXml.TopicEntry(topic, enrich ? broker.GetTopicRuntimeProperties(topic.Name) : null)))).ConfigureAwait(false);
            return;
        }

        if (context.Request.Method == HttpMethods.Get && path.Equals("$Resources/queues", StringComparison.OrdinalIgnoreCase))
        {
            await WriteXmlAsync(context, AdminXml.Feed([])).ConfigureAwait(false);
            return;
        }

        if (context.Request.Method == HttpMethods.Get && path.Equals("$namespaceinfo", StringComparison.OrdinalIgnoreCase))
        {
            await WriteXmlAsync(context, AdminXml.Feed([])).ConfigureAwait(false);
            return;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 1)
        {
            await TopicAsync(context, broker, segments[0]).ConfigureAwait(false);
            return;
        }

        if (segments.Length >= 2 && segments[1].Equals("Subscriptions", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Length == 2)
            {
                await SubscriptionListAsync(context, broker, segments[0]).ConfigureAwait(false);
                return;
            }

            if (segments.Length == 3)
            {
                await SubscriptionAsync(context, broker, segments[0], segments[2]).ConfigureAwait(false);
                return;
            }

            if (segments.Length >= 4 && segments[3].Equals("Rules", StringComparison.OrdinalIgnoreCase))
            {
                if (segments.Length == 4)
                {
                    await RuleListAsync(context, broker, segments[0], segments[2]).ConfigureAwait(false);
                    return;
                }

                if (segments.Length == 5)
                {
                    await RuleAsync(context, broker, segments[0], segments[2], segments[4]).ConfigureAwait(false);
                    return;
                }
            }
        }

        await WriteErrorAsync(context, StatusCodes.Status501NotImplemented, "NotSupported", "The requested route is outside Spec 027.").ConfigureAwait(false);
    }

    private static async Task TopicAsync(HttpContext context, BrokerNamespace broker, string topicName)
    {
        if (context.Request.Method == HttpMethods.Get)
        {
            var definition = broker.GetTopicDefinition(topicName);
            await WriteXmlAsync(context, AdminXml.TopicEntry(definition, IsEnriched(context) ? broker.GetTopicRuntimeProperties(topicName) : null)).ConfigureAwait(false);
        }
        else if (context.Request.Method == HttpMethods.Put)
        {
            var definition = AdminXml.ParseTopic(topicName, await AdminXml.ReadAsync(context.Request.Body, context.RequestAborted).ConfigureAwait(false));
            if (context.Request.Headers.IfMatch.Count > 0)
            {
                broker.UpdateTopic(definition);
                await WriteXmlAsync(context, AdminXml.TopicEntry(definition)).ConfigureAwait(false);
            }
            else
            {
                broker.CreateTopic(definition);
                context.Response.StatusCode = StatusCodes.Status201Created;
                await WriteXmlAsync(context, AdminXml.TopicEntry(definition)).ConfigureAwait(false);
            }
        }
        else
        {
            broker.DeleteTopic(topicName);
            context.Response.StatusCode = StatusCodes.Status200OK;
        }
    }

    private static async Task SubscriptionListAsync(HttpContext context, BrokerNamespace broker, string topicName)
    {
        EnsureGet(context);
        var enrich = IsEnriched(context);
        await WriteXmlAsync(context, AdminXml.Feed(Page(context, broker.GetSubscriptions(topicName)).Select(subscription =>
            AdminXml.SubscriptionEntry(topicName, subscription,
                enrich ? broker.GetSubscriptionRuntimeProperties(topicName, subscription.Name) : null)))).ConfigureAwait(false);
    }

    private static async Task SubscriptionAsync(HttpContext context, BrokerNamespace broker, string topicName, string subscriptionName)
    {
        if (context.Request.Method == HttpMethods.Get)
        {
            var definition = broker.GetSubscriptionDefinition(topicName, subscriptionName);
            await WriteXmlAsync(context, AdminXml.SubscriptionEntry(topicName, definition,
                IsEnriched(context) ? broker.GetSubscriptionRuntimeProperties(topicName, subscriptionName) : null)).ConfigureAwait(false);
        }
        else if (context.Request.Method == HttpMethods.Put)
        {
            var definition = AdminXml.ParseSubscription(subscriptionName, await AdminXml.ReadAsync(context.Request.Body, context.RequestAborted).ConfigureAwait(false));
            if (context.Request.Headers.IfMatch.Count > 0)
            {
                broker.UpdateSubscription(topicName, definition);
                await WriteXmlAsync(context, AdminXml.SubscriptionEntry(topicName, definition)).ConfigureAwait(false);
            }
            else
            {
                broker.CreateSubscription(topicName, definition);
                context.Response.StatusCode = StatusCodes.Status201Created;
                await WriteXmlAsync(context, AdminXml.SubscriptionEntry(topicName, definition)).ConfigureAwait(false);
            }
        }
        else
        {
            broker.DeleteSubscription(topicName, subscriptionName);
            context.Response.StatusCode = StatusCodes.Status200OK;
        }
    }

    private static async Task RuleListAsync(HttpContext context, BrokerNamespace broker, string topicName, string subscriptionName)
    {
        EnsureGet(context);
        await WriteXmlAsync(context, AdminXml.Feed(Page(context, broker.GetRules(topicName, subscriptionName)).Select(AdminXml.RuleEntry))).ConfigureAwait(false);
    }

    private static async Task RuleAsync(HttpContext context, BrokerNamespace broker, string topicName, string subscriptionName, string ruleName)
    {
        if (context.Request.Method == HttpMethods.Get)
        {
            await WriteXmlAsync(context, AdminXml.RuleEntry(broker.GetRule(topicName, subscriptionName, ruleName))).ConfigureAwait(false);
        }
        else if (context.Request.Method == HttpMethods.Put)
        {
            var definition = AdminXml.ParseRule(ruleName, await AdminXml.ReadAsync(context.Request.Body, context.RequestAborted).ConfigureAwait(false));
            broker.CreateRule(topicName, subscriptionName, definition);
            context.Response.StatusCode = StatusCodes.Status201Created;
            await WriteXmlAsync(context, AdminXml.RuleEntry(definition)).ConfigureAwait(false);
        }
        else
        {
            broker.DeleteRule(topicName, subscriptionName, ruleName);
            context.Response.StatusCode = StatusCodes.Status200OK;
        }
    }

    private static IEnumerable<T> Page<T>(HttpContext context, IReadOnlyList<T> values)
    {
        var skip = int.TryParse(context.Request.Query["$skip"], out var parsedSkip) ? parsedSkip : 0;
        var top = int.TryParse(context.Request.Query["$top"], out var parsedTop) ? parsedTop : 100;
        return values.Skip(Math.Max(0, skip)).Take(Math.Clamp(top, 1, 100));
    }

    private static bool IsEnriched(HttpContext context) =>
        bool.TryParse(context.Request.Query["enrich"], out var enrich) && enrich;

    private static void EnsureGet(HttpContext context)
    {
        if (context.Request.Method != HttpMethods.Get)
        {
            throw new BadHttpRequestException("This collection route supports GET only.", StatusCodes.Status405MethodNotAllowed);
        }
    }

    private static async Task WriteXmlAsync(HttpContext context, XDocument document)
    {
        context.Response.ContentType = "application/atom+xml; charset=utf-8";
        await context.Response.WriteAsync(document.ToString(SaveOptions.DisableFormatting), context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(HttpContext context, int status, string code, string detail)
    {
        context.Response.StatusCode = status;
        await WriteXmlAsync(context, AdminXml.Error(code, detail)).ConfigureAwait(false);
    }
}
