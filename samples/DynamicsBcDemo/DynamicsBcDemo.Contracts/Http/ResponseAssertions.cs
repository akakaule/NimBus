namespace DynamicsBcDemo.Contracts.Http;

/// <summary>
/// HTTP response helpers shared by the adapters. The failure text carries the operation, the
/// request method and path, the status and up to 500 characters of the response body, so the
/// Failed view in nimbus-ops shows something an operator can act on instead of a bare status code.
/// </summary>
public static class ResponseAssertions
{
    private const int MaxBodyChars = 500;

    /// <summary>
    /// Describes a non-success response: <c>{operation} failed: {api} {METHOD} {path} → {status} {reason}. Body: …</c>.
    /// </summary>
    public static async Task<string> DescribeFailureAsync(
        this HttpResponseMessage response,
        string operation,
        string apiName,
        CancellationToken cancellationToken)
    {
        string body = string.Empty;
        try { body = await response.Content.ReadAsStringAsync(cancellationToken); }
        catch (Exception) { /* best-effort: never fail diagnostics over a body read */ }
        if (body.Length > MaxBodyChars) body = body[..MaxBodyChars] + "…";

        var method = response.RequestMessage?.Method.Method ?? "?";
        var path = response.RequestMessage?.RequestUri?.PathAndQuery ?? "?";
        var status = (int)response.StatusCode;
        var reason = response.ReasonPhrase ?? response.StatusCode.ToString();
        var bodyPart = string.IsNullOrWhiteSpace(body) ? string.Empty : $" Body: {body}";

        return $"{operation} failed: {apiName} {method} {path} → {status} {reason}.{bodyPart}";
    }

    /// <summary>Throws an <see cref="HttpRequestException"/> with a descriptive message when the response is not a success.</summary>
    public static async Task EnsureSuccessOrThrowAsync(
        this HttpResponseMessage response,
        string operation,
        string apiName,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        var message = await response.DescribeFailureAsync(operation, apiName, cancellationToken);
        throw new HttpRequestException(message, inner: null, statusCode: response.StatusCode);
    }
}
