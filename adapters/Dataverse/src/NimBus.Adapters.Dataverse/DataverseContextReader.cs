using System.Text;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NimBus.Adapters.Dataverse.Contracts;

namespace NimBus.Adapters.Dataverse;

/// <summary>Parses the supported JSON execution-context subset without CLR type activation.</summary>
public sealed class DataverseContextReader(DataverseOptions options)
{
    /// <summary>Validate and normalize one context. Truncated contexts must not be published.</summary>
    public DataverseRecordEvent Read(string body, bool truncated = false)
    {
        options.Validate();
        if (truncated) throw new DataverseInputException("TruncatedContext");
        if (Encoding.UTF8.GetByteCount(body) > options.MaxBodyBytes) throw new DataverseInputException("ContextTooLarge");
        try
        {
            using var text = new StringReader(body);
            using var json = new JsonTextReader(text) { MaxDepth = 32, DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Decimal };
            var context = JObject.Load(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (json.Read()) throw new DataverseInputException("TrailingContent");
            return Normalize(context);
        }
        catch (JsonException) { throw new DataverseInputException("InvalidJson"); }
        catch (FormatException) { throw new DataverseInputException("InvalidValue"); }
        catch (InvalidCastException) { throw new DataverseInputException("InvalidValue"); }
        catch (OverflowException) { throw new DataverseInputException("InvalidValue"); }
        catch (ArgumentException) { throw new DataverseInputException("InvalidValue"); }
    }

    private DataverseRecordEvent Normalize(JObject context)
    {
        var organization = ReadGuid(context["OrganizationId"]);
        if (organization != options.OrganizationId) throw new DataverseInputException("UnexpectedOrganization");
        if ((int?)context["Stage"] != 40 || (int?)context["Mode"] != 1) throw new DataverseInputException("UnsupportedExecutionStage");
        var table = (string?)context["PrimaryEntityName"] ?? string.Empty;
        if (!options.Tables.TryGetValue(table, out var columns)) throw new DataverseInputException("TableNotAllowed");
        var operation = (string?)context["MessageName"];
        DataverseRecordEvent result = operation switch
        {
            "Create" => new DataverseRecordCreatedV1(),
            "Update" => new DataverseRecordUpdatedV1(),
            "Delete" => new DataverseRecordDeletedV1(),
            _ => throw new DataverseInputException("OperationNotSupported"),
        };
        result.OrganizationId = organization;
        result.RegistrationId = ReadGuid((context["OwningExtension"] as JObject)?["Id"]);
        result.OperationId = ReadGuid(context["OperationId"]);
        result.RecordId = ReadGuid(context["PrimaryEntityId"]);
        result.CorrelationId = ReadGuid(context["CorrelationId"]);
        result.Table = table;
        result.SessionId = $"dataverse:{organization:D}:{table}:{result.RecordId:D}";
        if (Encoding.UTF8.GetByteCount(result.SessionId) > 128)
            result.SessionId = $"dataverse:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.SessionId))).ToLowerInvariant()}";
        var target = ReadPairs(context["InputParameters"])["Target"] as JObject
            ?? throw new DataverseInputException("MissingTarget");
        if ((string?)target["LogicalName"] != table || ReadGuid(target["Id"]) != result.RecordId)
            throw new DataverseInputException("TargetIdentityMismatch");
        if (operation != "Delete") result.Attributes = Project(target, columns);
        if (operation != "Create") result.Before = ReadImage(context["PreEntityImages"], options.PreImageAlias, table, result.RecordId, columns);
        if (operation != "Delete") result.After = ReadImage(context["PostEntityImages"], options.PostImageAlias, table, result.RecordId, columns);
        return result;
    }

    private static JObject? ReadImage(JToken? images, string? alias, string table, Guid id, string[] columns)
    {
        if (string.IsNullOrWhiteSpace(alias)) return null;
        var image = ReadPairs(images)[alias] as JObject ?? throw new DataverseInputException("MissingRequiredImage");
        if ((string?)image["LogicalName"] != table || ReadGuid(image["Id"]) != id)
            throw new DataverseInputException("ImageIdentityMismatch");
        return Project(image, columns);
    }

    private static JObject Project(JObject entity, string[] columns)
    {
        var attributes = ReadPairs(entity["Attributes"]);
        var result = new JObject();
        foreach (var column in columns.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (attributes.TryGetValue(column, StringComparison.Ordinal, out var value)) result[column] = NormalizeValue(value);
        }
        return result;
    }

    private static JToken NormalizeValue(JToken value)
    {
        if (value is JValue scalar && scalar.Type is JTokenType.Null or JTokenType.String or JTokenType.Integer or JTokenType.Float or JTokenType.Boolean)
            return scalar.DeepClone();
        if (value is JArray choices)
        {
            if (choices.Any(choice => choice is not JObject item || (string?)item["__type"] != "OptionSetValue:http://schemas.microsoft.com/xrm/2011/Contracts" || item["Value"]?.Type != JTokenType.Integer))
                throw new DataverseInputException("UnsupportedAttributeType");
            return new JObject { ["kind"] = "choices", ["values"] = new JArray(choices.Select(choice => choice["Value"]!.DeepClone())) };
        }
        if (value is not JObject complex) throw new DataverseInputException("UnsupportedAttributeType");
        var type = (string?)complex["__type"];
        // Match the complete Dataverse contract namespace; no CLR type activation.
        switch (type)
        {
            case "Money:http://schemas.microsoft.com/xrm/2011/Contracts":
                if (complex["Value"]?.Type is not (JTokenType.Integer or JTokenType.Float)) throw new DataverseInputException("InvalidMoney");
                return new JObject { ["kind"] = "money", ["value"] = complex["Value"]!.DeepClone() };
            case "OptionSetValue:http://schemas.microsoft.com/xrm/2011/Contracts":
                if (complex["Value"]?.Type != JTokenType.Integer) throw new DataverseInputException("InvalidChoice");
                return new JObject { ["kind"] = "choice", ["value"] = complex["Value"]!.DeepClone() };
            case "EntityReference:http://schemas.microsoft.com/xrm/2011/Contracts":
                var name = (string?)complex["LogicalName"];
                if (string.IsNullOrWhiteSpace(name)) throw new DataverseInputException("InvalidLookup");
                return new JObject { ["kind"] = "lookup", ["table"] = name, ["id"] = ReadGuid(complex["Id"]).ToString("D") };
            default: throw new DataverseInputException("UnsupportedAttributeType");
        }
    }

    private static JObject ReadPairs(JToken? token)
    {
        if (token is not JArray pairs) throw new DataverseInputException("MissingCollection");
        var result = new JObject();
        foreach (var pair in pairs)
        {
            if (pair is not JObject entry || entry["key"]?.Type != JTokenType.String || !entry.ContainsKey("value"))
                throw new DataverseInputException("InvalidCollection");
            var key = (string)entry["key"]!;
            if (result.ContainsKey(key)) throw new DataverseInputException("DuplicateAttribute");
            result.Add(key, entry["value"]!.DeepClone());
        }
        return result;
    }

    private static Guid ReadGuid(JToken? value)
    {
        if (value?.Type != JTokenType.String || !Guid.TryParse((string?)value, out var id) || id == Guid.Empty)
            throw new DataverseInputException("MissingOrInvalidIdentity");
        return id;
    }
}
