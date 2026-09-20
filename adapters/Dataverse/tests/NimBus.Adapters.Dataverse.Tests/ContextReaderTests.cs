#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using NimBus.Adapters.Dataverse.Contracts;

namespace NimBus.Adapters.Dataverse.Tests;

[TestClass]
public sealed class ContextReaderTests
{
    private static readonly Guid Organization = Guid.Parse("11111111-1111-1111-1111-111111111111");

    internal static DataverseOptions Options() => new()
    {
        OrganizationId = Organization,
        Tables = new(StringComparer.Ordinal) { ["account"] = ["name", "emailaddress1", "creditlimit", "parentaccountid"] },
    };

    internal static JObject Context(string operation = "Update") => JObject.Parse("""
    {
      "OrganizationId":"11111111-1111-1111-1111-111111111111",
      "OwningExtension":{"Id":"22222222-2222-2222-2222-222222222222"},
      "OperationId":"33333333-3333-3333-3333-333333333333",
      "CorrelationId":"44444444-4444-4444-4444-444444444444",
      "PrimaryEntityName":"account",
      "PrimaryEntityId":"55555555-5555-5555-5555-555555555555",
      "Stage":40,"Mode":1,"MessageName":"Update",
      "InputParameters":[{"key":"Target","value":{
        "__type":"Entity:http://schemas.microsoft.com/xrm/2011/Contracts",
        "LogicalName":"account","Id":"55555555-5555-5555-5555-555555555555",
        "Attributes":[{"key":"name","value":"Contoso"},{"key":"emailaddress1","value":null},{"key":"secret","value":"excluded"}]
      }}]
    }
    """).WithOperation(operation);

    [TestMethod]
    public void Update_preserves_null_and_projects_only_configured_attributes()
    {
        var result = new DataverseContextReader(Options()).Read(Context().ToString());
        Assert.IsInstanceOfType<DataverseRecordUpdatedV1>(result);
        Assert.AreEqual("Contoso", (string?)result.Attributes["name"]);
        Assert.AreEqual(JTokenType.Null, result.Attributes["emailaddress1"]!.Type);
        Assert.IsNull(result.Attributes["secret"]);
        Assert.AreEqual(2, result.Attributes.Count);
        Assert.AreEqual($"dataverse:{Organization:D}:account:55555555-5555-5555-5555-555555555555", result.GetSessionId());
    }

    [TestMethod]
    public void Create_and_delete_have_distinct_versioned_contracts()
    {
        var reader = new DataverseContextReader(Options());
        Assert.IsInstanceOfType<DataverseRecordCreatedV1>(reader.Read(Context("Create").ToString()));
        var deleted = reader.Read(Context("Delete").ToString());
        Assert.IsInstanceOfType<DataverseRecordDeletedV1>(deleted);
        Assert.AreEqual(0, deleted.Attributes.Count);
    }

    [TestMethod]
    public void Foreign_organization_and_missing_operation_identity_are_rejected()
    {
        var reader = new DataverseContextReader(Options());
        var context = Context();
        context["OrganizationId"] = Guid.NewGuid();
        Assert.ThrowsExactly<DataverseInputException>(() => reader.Read(context.ToString()));
        context = Context();
        context.Remove("OperationId");
        Assert.ThrowsExactly<DataverseInputException>(() => reader.Read(context.ToString()));
    }

    [TestMethod]
    public void Truncated_oversized_and_malformed_input_is_rejected()
    {
        var reader = new DataverseContextReader(Options());
        Assert.ThrowsExactly<DataverseInputException>(() => reader.Read(Context().ToString(), true));
        Assert.ThrowsExactly<DataverseInputException>(() => reader.Read(new string('x', 192 * 1024 + 1)));
        Assert.ThrowsExactly<DataverseInputException>(() => reader.Read("{"));
        Assert.ThrowsExactly<DataverseInputException>(() => reader.Read(Context() + "{}"));
    }

    [TestMethod]
    public void Unknown_table_stage_and_duplicate_keys_are_rejected()
    {
        var reader = new DataverseContextReader(Options());
        var context = Context();
        context["PrimaryEntityName"] = "contact";
        Assert.ThrowsExactly<DataverseInputException>(() => reader.Read(context.ToString()));
        context = Context();
        context["Stage"] = 20;
        Assert.ThrowsExactly<DataverseInputException>(() => reader.Read(context.ToString()));
        Assert.ThrowsExactly<DataverseInputException>(() => reader.Read("{\"Stage\":40,\"Stage\":20}"));
    }

    [TestMethod]
    public void Missing_required_image_is_rejected()
    {
        var options = Options();
        options.PreImageAlias = "Before";
        Assert.ThrowsExactly<DataverseInputException>(() => new DataverseContextReader(options).Read(Context().ToString()));
    }

    [TestMethod]
    public void Typed_values_are_normalized_without_instantiating_source_types()
    {
        var context = Context();
        var attributes = (JArray)context["InputParameters"]![0]!["value"]!["Attributes"]!;
        attributes.Add(JObject.Parse("""{"key":"creditlimit","value":{"__type":"Money:http://schemas.microsoft.com/xrm/2011/Contracts","Value":123.45}}"""));
        attributes.Add(JObject.Parse("""{"key":"parentaccountid","value":{"__type":"EntityReference:http://schemas.microsoft.com/xrm/2011/Contracts","LogicalName":"account","Id":"66666666-6666-6666-6666-666666666666","Name":"not forwarded"}}"""));
        var record = new DataverseContextReader(Options()).Read(context.ToString());
        Assert.AreEqual("money", (string?)record.Attributes["creditlimit"]!["kind"]);
        Assert.AreEqual(123.45m, (decimal)record.Attributes["creditlimit"]!["value"]!);
        Assert.AreEqual("lookup", (string?)record.Attributes["parentaccountid"]!["kind"]);
        Assert.IsNull(record.Attributes["parentaccountid"]!["Name"]);
        attributes.Add(JObject.Parse("""{"key":"emailaddress1","value":"duplicate"}"""));
        Assert.ThrowsExactly<DataverseInputException>(() => new DataverseContextReader(Options()).Read(context.ToString()));
    }

    [TestMethod]
    public void Unknown_attribute_objects_are_rejected_and_long_sessions_are_bounded()
    {
        var context = Context();
        context["InputParameters"]![0]!["value"]!["Attributes"]![0]!["value"] = new JObject { ["$type"] = "System.IO.FileInfo", ["path"] = "secret" };
        Assert.ThrowsExactly<DataverseInputException>(() => new DataverseContextReader(Options()).Read(context.ToString()));
        context = Context();
        var table = new string('a', 100);
        context["PrimaryEntityName"] = table;
        context["InputParameters"]![0]!["value"]!["LogicalName"] = table;
        var options = Options();
        options.Tables = new(StringComparer.Ordinal) { [table] = ["name"] };
        Assert.IsTrue(new DataverseContextReader(options).Read(context.ToString()).SessionId.Length <= 128);
    }

    [TestMethod]
    public void Non_scalar_context_fields_are_permanent_input_failures()
    {
        var context = Context();
        context["Stage"] = new JObject();
        Assert.ThrowsExactly<DataverseInputException>(() => new DataverseContextReader(Options()).Read(context.ToString()));
        context = Context();
        context["PrimaryEntityName"] = new JArray("account");
        Assert.ThrowsExactly<DataverseInputException>(() => new DataverseContextReader(Options()).Read(context.ToString()));
    }

    [TestMethod]
    public void Choice_arrays_and_selected_images_preserve_projection()
    {
        var context = Context();
        var entity = (JObject)context["InputParameters"]![0]!["value"]!;
        entity["Attributes"]![0]!["value"] = JArray.Parse("""[{"__type":"OptionSetValue:http://schemas.microsoft.com/xrm/2011/Contracts","Value":10}]""");
        context["PreEntityImages"] = new JArray(new JObject { ["key"] = "Before", ["value"] = entity.DeepClone() });
        var options = Options();
        options.PreImageAlias = "Before";
        var result = new DataverseContextReader(options).Read(context.ToString());
        Assert.AreEqual("choices", (string?)result.Attributes["name"]!["kind"]);
        Assert.AreEqual(10, (int)result.Before!["name"]!["values"]![0]!);
        Assert.IsNull(result.Before["secret"]);
    }
}

internal static class ContextFixture
{
    internal static JObject WithOperation(this JObject context, string operation)
    {
        context["MessageName"] = operation;
        return context;
    }
}
