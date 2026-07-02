using Newtonsoft.Json.Linq;

namespace NimBus.MessageStore.Abstractions;

/// <summary>Structural equality for JSON Schema strings, ignoring whitespace/key order.</summary>
public static class SchemaJson
{
    public static bool Equal(string a, string b)
    {
        if (a == b) return true;
        if (a == null || b == null) return false;
        return JToken.DeepEquals(JToken.Parse(a), JToken.Parse(b));
    }
}
