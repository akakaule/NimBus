using Azure.Core;

namespace NimBus.ServiceBus.Transport;

/// <summary>
/// Configuration shape for the Azure Service Bus transport provider. Consumed by
/// <c>AddServiceBusTransport</c> via the standard <c>IOptions</c> pipeline.
/// </summary>
/// <remarks>
/// Two authentication modes are supported, matching the underlying
/// <c>Azure.Messaging.ServiceBus.ServiceBusClient</c> constructor overloads:
/// <list type="bullet">
/// <item><description><see cref="ConnectionString"/> — full SAS connection string (typically used in development / Aspire).</description></item>
/// <item><description><see cref="FullyQualifiedNamespace"/> + <see cref="Credential"/> — token-based auth via Entra ID (production).</description></item>
/// </list>
/// Exactly one of the two modes must be supplied; option validation that enforces
/// this lands alongside the actual transport plumbing (issue #3 / #14 follow-up).
/// </remarks>
public sealed class ServiceBusTransportOptions
{
    /// <summary>
    /// SAS connection string for the Service Bus namespace. When supplied,
    /// <see cref="FullyQualifiedNamespace"/> and <see cref="Credential"/> are ignored.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Fully-qualified Service Bus namespace (e.g. <c>contoso.servicebus.windows.net</c>).
    /// Used together with <see cref="Credential"/> for token-based authentication.
    /// </summary>
    public string? FullyQualifiedNamespace { get; set; }

    /// <summary>
    /// Token credential used to authenticate to <see cref="FullyQualifiedNamespace"/>.
    /// Typically <c>DefaultAzureCredential</c> or <c>ManagedIdentityCredential</c>.
    /// </summary>
    public TokenCredential? Credential { get; set; }
}
