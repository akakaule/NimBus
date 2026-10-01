using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;

namespace NimBus.Extensions.Http;

/// <summary>
/// Adds a Microsoft Entra bearer token from an <see cref="AzureAccessTokenCache"/> to every request.
/// </summary>
/// <remarks>
/// Register it with <c>AddAzureBearerToken(...)</c> on an <c>IHttpClientBuilder</c>, which shares one
/// cache across the handler instances <c>IHttpClientFactory</c> creates.
/// </remarks>
public sealed class AzureBearerTokenHandler : DelegatingHandler
{
    private readonly AzureAccessTokenCache _tokens;

    /// <summary>
    /// Creates a handler with its own token cache for <paramref name="credential"/> and <paramref name="scopes"/>.
    /// </summary>
    /// <param name="credential">The credential that issues tokens.</param>
    /// <param name="scopes">The resource scopes, for example <c>https://contoso.crm.dynamics.com/.default</c>.</param>
    public AzureBearerTokenHandler(TokenCredential credential, params string[] scopes)
        : this(new AzureAccessTokenCache(credential, scopes))
    {
    }

    /// <summary>
    /// Creates a handler that takes tokens from a shared <paramref name="tokens"/> cache.
    /// </summary>
    /// <param name="tokens">The token cache.</param>
    public AzureBearerTokenHandler(AzureAccessTokenCache tokens)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var token = await _tokens.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
