namespace BusinessCentral.Api.Domain;

/// <summary>
/// A request BC refuses on business grounds (missing salesperson, blocked item, unknown
/// customer). The API maps it to a BC-style error body with <see cref="Code"/> and
/// <see cref="StatusCode"/>, e.g. <c>422 {"error":{"code":"Application_SalespersonNotFound",...}}</c>.
/// </summary>
public sealed class BcBusinessRuleException(string code, string message, int statusCode = StatusCodes.Status422UnprocessableEntity)
    : Exception(message)
{
    public string Code { get; } = code;

    public int StatusCode { get; } = statusCode;
}

/// <summary>BC-style error body: <c>{"error":{"code":"...","message":"..."}}</c>.</summary>
public sealed record BcErrorBody(BcError Error)
{
    public static BcErrorBody Of(string code, string message) => new(new BcError(code, message));
}

public sealed record BcError(string Code, string Message);
