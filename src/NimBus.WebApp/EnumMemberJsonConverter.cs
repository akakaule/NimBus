using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NimBus.WebApp;

/// <summary>
/// Serializes enums using their <see cref="EnumMemberAttribute"/> value, which is
/// the value the OpenAPI contract declares.
/// </summary>
/// <remarks>
/// <para>
/// NSwag generates every contract enum with the spec's own value on an
/// <c>[EnumMember]</c> attribute — <c>owner</c> for the role ladder, <c>Pending</c>
/// for the message statuses. System.Text.Json's <see cref="JsonStringEnumConverter"/>
/// ignores that attribute and writes the CLR name instead, so every enum whose spec
/// value is not identical to its C# member name went out over the wire misspelt:
/// the API answered <c>"Owner"</c> where api-spec.yaml, and therefore every generated
/// client, said <c>"owner"</c>. Thirteen of the seventeen contract enums were affected.
/// </para>
/// <para>
/// Honouring the attribute fixes those thirteen and cannot disturb the other four,
/// whose EnumMember values already match their member names exactly. It also keeps
/// the contract self-maintaining: change a value in api-spec.yaml and the generated
/// attribute carries it, rather than needing a matching change here.
/// </para>
/// <para>
/// Reading mirrors <see cref="EnumMemberModelBinder"/>, which has always treated the
/// EnumMember value as the wire form for query and route binding: an exact match on
/// either the EnumMember value or the member name, then a case-insensitive parse.
/// That tolerance means callers already sending the CLR name keep working.
/// </para>
/// </remarks>
public sealed class EnumMemberJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert.IsEnum && HasEnumMemberValues(typeToConvert);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(
            typeof(EnumMemberJsonConverter<>).MakeGenericType(typeToConvert))!;

    private static bool HasEnumMemberValues(Type enumType)
        => enumType.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Any(field => field.GetCustomAttribute<EnumMemberAttribute>()?.Value != null);
}

internal sealed class EnumMemberJsonConverter<T> : JsonConverter<T>
    where T : struct, Enum
{
    // Reflected once per closed generic: the factory is consulted per type and the
    // resulting converter is cached by JsonSerializerOptions.
    private static readonly Dictionary<T, string> WireValues = BuildWireValues();
    private static readonly Dictionary<string, T> Members = BuildMembers();

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var numeric))
        {
            // Ordinals appear in payloads written before the contract settled.
            return (T)Enum.ToObject(typeof(T), numeric);
        }

        var value = reader.GetString();
        if (value is null)
        {
            throw new JsonException($"Cannot convert null to {typeof(T).Name}.");
        }

        if (Members.TryGetValue(value, out var exact))
        {
            return exact;
        }

        if (Enum.TryParse<T>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new JsonException($"Invalid value '{value}' for {typeof(T).Name}.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        // An undeclared value (a flags combination, or an ordinal cast in) has no
        // contract spelling; fall back to what the default converter would write.
        writer.WriteStringValue(WireValues.TryGetValue(value, out var wire) ? wire : value.ToString());
    }

    private static Dictionary<T, string> BuildWireValues()
    {
        var map = new Dictionary<T, string>();
        foreach (var field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var attribute = field.GetCustomAttribute<EnumMemberAttribute>();
            if (attribute?.Value != null)
            {
                map.TryAdd((T)field.GetValue(null)!, attribute.Value);
            }
        }

        return map;
    }

    private static Dictionary<string, T> BuildMembers()
    {
        // EnumMember value before member name, declaration order, first match wins —
        // the same precedence EnumMemberModelBinder applies.
        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var value = (T)field.GetValue(null)!;
            var attribute = field.GetCustomAttribute<EnumMemberAttribute>();
            if (attribute?.Value != null)
            {
                map.TryAdd(attribute.Value, value);
            }

            map.TryAdd(field.Name, value);
        }

        return map;
    }
}
