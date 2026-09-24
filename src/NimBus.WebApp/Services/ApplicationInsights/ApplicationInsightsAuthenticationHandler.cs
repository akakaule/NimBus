using System.Net.Http.Headers;
using Azure.Core;

namespace NimBus.WebApp.Services.ApplicationInsights;

/// <summary>
/// Sends a Microsoft Entra bearer token with every Application Insights query API request. The
/// query API (<c>api.applicationinsights.io</c>) stopped accepting API keys on 2026-03-31. The
/// identity needs read access to the Application Insights component (the Reader role).
/// </summary>
/// <remarks>
/// Each request asks the credential for a token. Managed identity caches tokens in memory;
/// developer credentials such as the Azure CLI don't, which only costs latency when a local run
/// sets <c>AppInsights:ApplicationId</c>.
/// </remarks>
internal sealed class ApplicationInsightsAuthenticationHandler : DelegatingHandler
{
    /// <summary>The token scope of the Application Insights query API.</summary>
    internal const string Scope = "https://api.applicationinsights.io/.default";

    private static readonly TokenRequestContext TokenRequest = new(new[] { Scope });

    private readonly TokenCredential _credential;

    /// <summary>Creates the handler.</summary>
    /// <param name="credential">The credential that acquires query API tokens.</param>
    public ApplicationInsightsAuthenticationHandler(TokenCredential credential)
    {
        _credential = credential;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _credential.GetTokenAsync(TokenRequest, cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
