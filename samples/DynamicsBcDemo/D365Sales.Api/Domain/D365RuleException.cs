namespace D365Sales.Api.Domain;

/// <summary>A user action Dynamics 365 refuses (closed opportunity, BC-owned field, no lines).</summary>
public sealed class D365RuleException(string message, int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// An error from the Dataverse-shaped API, returned in Dataverse's shape:
/// <c>{"error":{"code":"0x80060888","message":"..."}}</c>.
/// </summary>
public sealed class DataverseApiException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public string Code { get; } = code;
}
