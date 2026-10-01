using System;
using System.Threading;
using Azure.Core;
using NimBus.Extensions.Http;

// Same namespace as AddHttpClient so the extension is found without an extra using.
#pragma warning disable IDE0130
namespace Microsoft.Extensions.DependencyInjection;
#pragma warning restore IDE0130

/// <summary>
/// Registers <see cref="AzureBearerTokenHandler"/> on an <see cref="IHttpClientBuilder"/>.
/// </summary>
public static class HttpClientBuilderExtensions
{
    /// <summary>
    /// Adds a cached Microsoft Entra bearer token for <paramref name="scopes"/> to every request,
    /// using the <see cref="TokenCredential"/> registered in the service collection.
    /// </summary>
    /// <param name="builder">The HTTP client builder.</param>
    /// <param name="scopes">The resource scopes, for example <c>https://contoso.crm.dynamics.com/.default</c>.</param>
    /// <returns>The builder, for chaining.</returns>
    public static IHttpClientBuilder AddAzureBearerToken(this IHttpClientBuilder builder, params string[] scopes)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ValidateScopes(scopes);

        // One cache per registration, created with the first handler and shared by every later one.
        AzureAccessTokenCache? tokens = null;
        return builder.AddHttpMessageHandler(sp =>
        {
            var cache = LazyInitializer.EnsureInitialized(
                ref tokens,
                () => new AzureAccessTokenCache(sp.GetRequiredService<TokenCredential>(), scopes));
            return new AzureBearerTokenHandler(cache);
        });
    }

    /// <summary>
    /// Adds a cached Microsoft Entra bearer token for <paramref name="scopes"/> to every request,
    /// using <paramref name="credential"/>.
    /// </summary>
    /// <param name="builder">The HTTP client builder.</param>
    /// <param name="credential">The credential that issues tokens.</param>
    /// <param name="scopes">The resource scopes, for example <c>https://api.businesscentral.dynamics.com/.default</c>.</param>
    /// <returns>The builder, for chaining.</returns>
    public static IHttpClientBuilder AddAzureBearerToken(this IHttpClientBuilder builder, TokenCredential credential, params string[] scopes)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var tokens = new AzureAccessTokenCache(credential, scopes);
        return builder.AddHttpMessageHandler(() => new AzureBearerTokenHandler(tokens));
    }

    private static void ValidateScopes(string[] scopes)
    {
        if (scopes is null || scopes.Length == 0 || Array.Exists(scopes, string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one non-empty scope is required.", nameof(scopes));
        }
    }
}
