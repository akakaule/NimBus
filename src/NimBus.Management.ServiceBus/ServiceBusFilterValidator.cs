using System;
using System.Text.RegularExpressions;

namespace NimBus.Management.ServiceBus;

/// <summary>
/// Allowlist guard for values that get interpolated into Service Bus
/// <c>SqlRuleFilter</c> / <c>SqlRuleAction</c> expressions and entity names
/// (topics, subscriptions, rules). Rejects anything outside
/// <c>[A-Za-z0-9._$-]{1,50}</c> so quotes, parentheses, semicolons, spaces
/// and other SQL-filter metacharacters can't be smuggled in via operator
/// input or an environment variable.
/// </summary>
/// <remarks>
/// <para>
/// The injection vector this closes: the WebApp's endpoint-management API
/// takes an operator-supplied endpoint id and flows it straight into
/// <c>$"user.To='{subscriptionName}'"</c> in <see cref="ServiceBusManagement.CreateRule"/>.
/// Without validation, a value like <c>x' OR 1=1 OR '</c> would terminate the
/// quoted string and widen the rule to match every message on the namespace,
/// letting a caller steal traffic intended for another endpoint.
/// </para>
/// <para>
/// Validation is enforced at the <see cref="ServiceBusManagement"/> public API
/// (chokepoint), so any current or future caller that reaches Service Bus
/// through it is covered regardless of how it sourced the name.
/// </para>
/// </remarks>
public static class ServiceBusFilterValidator
{
    public const int MaxNameLength = 50;

    // Characters Service Bus accepts in entity/rule names and in interpolated
    // filter value positions: ASCII letters, digits, period, hyphen,
    // underscore, plus `$` so Service Bus's own `$Default` rule-name prefix
    // is accepted. Anything else (quotes, parens, spaces, semicolons, etc.)
    // would either break SB's own name rules or — worse — terminate the
    // surrounding SQL-filter quoted string and enable injection. `$` is a
    // harmless literal inside a single-quoted SQL filter expression.
    private static readonly Regex AllowList = new(
        @"\A[A-Za-z0-9._$-]+\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Throws <see cref="ArgumentException"/> if <paramref name="value"/> is
    /// null/empty, longer than <see cref="MaxNameLength"/>, or contains
    /// characters outside <c>[A-Za-z0-9._$-]</c>.
    /// </summary>
    public static void ValidateName(string value, string paramName)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException("value must not be null or empty", paramName);
        }

        if (value.Length > MaxNameLength)
        {
            throw new ArgumentException(
                $"value '{value}' exceeds the {MaxNameLength}-character limit for Service Bus names",
                paramName);
        }

        if (!AllowList.IsMatch(value))
        {
            throw new ArgumentException(
                $"value '{value}' contains characters outside [A-Za-z0-9._$-]; cannot be safely interpolated into a Service Bus filter",
                paramName);
        }
    }
}
