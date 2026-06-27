#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NimBus.Extensions.Notifications.Tests;

[TestClass]
public class TemplateRendererTests
{
    [TestMethod]
    public void Render_SubstitutesAllKnownPlaceholders()
    {
        var notification = TestNotifications.Build(
            severity: NotificationSeverity.Error,
            title: "Boom",
            message: "It failed",
            eventId: "e-1",
            eventTypeId: "OrderPlaced",
            messageId: "m-1",
            correlationId: "c-1",
            errorDetails: "stack trace");

        const string template =
            "{Severity}|{Title}|{Message}|{EventId}|{EventTypeId}|{MessageId}|{CorrelationId}|{ErrorDetails}";

        var result = TemplateRenderer.Render(template, notification);

        Assert.AreEqual("Error|Boom|It failed|e-1|OrderPlaced|m-1|c-1|stack trace", result);
    }

    [TestMethod]
    public void Render_LeavesUnknownPlaceholdersLiteral()
    {
        var notification = TestNotifications.Build(title: "Hi");

        var result = TemplateRenderer.Render("{Title} {Unknown}", notification);

        Assert.AreEqual("Hi {Unknown}", result);
    }

    [TestMethod]
    public void Render_MissingValuesResolveToEmptyString()
    {
        var notification = TestNotifications.Build(errorDetails: null);

        var result = TemplateRenderer.Render("[{ErrorDetails}]", notification);

        Assert.AreEqual("[]", result);
    }
}
