using System.Net.Http;

namespace CrmErpDemo.Contracts.Http;

/// <summary>
/// HTTP response helpers shared by the CRM and ERP adapters. Replaces the
/// stock <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/> with an
/// exception whose message contains the operation context, the request
/// method+path, the status + reason phrase, and up to 500 chars of the
/// response body — so the NimBus WebApp's Failed-message view surfaces
/// actionable detail instead of just "Response status code does not
/// indicate success: 404 (Not Found)".
/// </summary>
public static class ResponseAssertions
{
    private const int MaxBodyChars = 500;

    /// <summary>
    /// Throws <see cref="HttpRequestException"/> with a rich message when
    /// the response is non-success. No-op on success.
    /// </summary>
    /// <param name="response">The HTTP response to inspect.</param>
    /// <param name="operation">
    /// Human-readable description of the operation (e.g.
    /// "Link CRM account {accountId} to ERP customer {erpCustomerId}").
    /// Lands at the start of the exception message.
    /// </param>
    /// <param name="apiName">
    /// Short tag identifying the upstream API ("CRM API", "ERP API"). Appears
    /// after the operation so the reader knows which side returned the error.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for the body read.</param>
    public static async Task EnsureSuccessOrThrowAsync(
        this HttpResponseMessage response,
        string operation,
        string apiName,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        string body = string.Empty;
        try { body = await response.Content.ReadAsStringAsync(cancellationToken); }
        catch { /* best-effort — never crash diagnostics over a body-read failure */ }
        if (body.Length > MaxBodyChars) body = body[..MaxBodyChars] + "…";

        var method = response.RequestMessage?.Method.Method ?? "?";
        var path = response.RequestMessage?.RequestUri?.PathAndQuery ?? "?";
        var status = (int)response.StatusCode;
        var reason = response.ReasonPhrase ?? response.StatusCode.ToString();
        var bodyPart = string.IsNullOrWhiteSpace(body) ? "" : $" Body: {body}";

        throw new HttpRequestException(
            $"{operation} failed: {apiName} {method} {path} → {status} {reason}.{bodyPart}",
            inner: null,
            statusCode: response.StatusCode);
    }
}
