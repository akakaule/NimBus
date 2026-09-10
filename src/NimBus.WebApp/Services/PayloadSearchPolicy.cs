using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Serialization;
using NimBus.Core.Endpoints;
using NimBus.Core.Messages.PII;

namespace NimBus.WebApp.Services;

internal static class PayloadSearchPolicy
{
    internal static string[]? GetNonSensitiveReceivedTypes(IEndpoint endpoint)
    {
        var events = endpoint.EventTypesConsumed.ToArray();
        if (events.Length == 0) return null;
        var resolver = new DefaultContractResolver();
        foreach (var eventType in events)
        {
            if (!IsNonSensitive(eventType.GetEventClassType(), resolver, new HashSet<Type>()))
                return null;
        }

        return events.Select(e => e.Id).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool IsNonSensitive(Type? type, DefaultContractResolver resolver, HashSet<Type> visited)
    {
        if (type == null || type == typeof(object) || type.IsDefined(typeof(SensitiveAttribute), inherit: true))
            return false;
        if (!visited.Add(type)) return true;

        var contract = resolver.ResolveContract(type);
        // Custom serialization and open JSON shapes cannot be classified from their CLR members.
        if (contract.Converter != null) return false;
        if (contract is JsonPrimitiveContract || contract is JsonStringContract) return true;
        if (contract is JsonArrayContract array)
            return array.ItemConverter == null && IsNonSensitive(array.CollectionItemType, resolver, visited);
        if (contract is JsonDictionaryContract dictionary)
            return dictionary.ItemConverter == null && dictionary.DictionaryKeyType == typeof(string)
                && IsNonSensitive(dictionary.DictionaryValueType, resolver, visited);
        if (contract is not JsonObjectContract obj || type.IsAbstract || type.IsInterface
            || obj.ExtensionDataGetter != null || obj.ExtensionDataSetter != null || obj.ItemConverter != null)
            return false;

        return obj.Properties.Where(p => !p.Ignored).All(p =>
            p.Converter == null && p.ItemConverter == null
            && !(p.AttributeProvider?.GetAttributes(typeof(SensitiveAttribute), inherit: true).Any() ?? false)
            && IsNonSensitive(p.PropertyType, resolver, visited));
    }
}
