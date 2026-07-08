#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using Newtonsoft.Json.Linq;
using Xunit;

namespace NimBus.CommandLine.Tests;

// Covers issue #69 governance follow-ups (AF-98): `nb asyncapi validate|diff`, the exit-code
// runner, and the expanded attribute/fluent enrichment surface.
public sealed class AsyncApiGovernanceTests
{
    private static JObject Doc(IPlatform platform, AsyncApiEnrichmentRegistry? enrichment = null) =>
        JObject.Parse(AsyncApiExporter.Serialize(platform, AsyncApiFormat.Json, enrichment));

    // ---------------- validate ----------------

    [Fact]
    public void Validate_WellFormedDocument_IsValid()
    {
        var result = AsyncApiValidator.Validate(Doc(new NimBus.PlatformConfiguration()));
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Validate_YamlRoundTrip_IsValid()
    {
        var yaml = AsyncApiExporter.Serialize(new NimBus.PlatformConfiguration(), AsyncApiFormat.Yaml);
        var result = AsyncApiValidator.Validate(AsyncApiDocumentLoader.Parse(yaml, asJson: false));
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Validate_WrongVersion_IsInvalid()
    {
        var doc = Doc(new NimBus.PlatformConfiguration());
        doc["asyncapi"] = "2.0.0";

        var result = AsyncApiValidator.Validate(doc);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("3.0.0", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_DanglingSchemaRef_IsInvalid()
    {
        var doc = Doc(new NimBus.PlatformConfiguration());
        var message = ((JObject)doc["components"]!["messages"]!).Properties().First().Value;
        message["payload"] = new JObject { ["$ref"] = "#/components/schemas/DoesNotExist" };

        var result = AsyncApiValidator.Validate(doc);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("DoesNotExist", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_PayloadRefIntoMessagesSection_IsRejected()
    {
        // A payload $ref must resolve under #/components/schemas, not #/components/messages.
        var doc = Doc(new NimBus.PlatformConfiguration());
        var messages = (JObject)doc["components"]!["messages"]!;
        var firstMessageName = messages.Properties().First().Name;
        messages[firstMessageName]!["payload"] = new JObject { ["$ref"] = $"#/components/messages/{firstMessageName}" };

        var result = AsyncApiValidator.Validate(doc);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("payload", StringComparison.Ordinal)
            && e.Contains("#/components/schemas/", StringComparison.Ordinal));
    }

    // ---------------- diff: additive vs breaking ----------------

    [Fact]
    public void Diff_AdditiveOnly_IsNotBreaking()
    {
        var baseline = Doc(new NimBus.PlatformConfiguration());
        var updated = (JObject)baseline.DeepClone();
        ((JObject)updated["channels"]!)["NewEndpoint"] = new JObject { ["address"] = "NewEndpoint" };
        ((JObject)updated["components"]!["schemas"]!["OrderPlaced"]!["properties"]!)["extra"] =
            new JObject { ["type"] = "string" };

        var result = AsyncApiDiff.Diff(baseline, updated);
        Assert.False(result.HasBreaking);
        Assert.Contains(result.Changes, c => c.Kind == ChangeKind.Added);
    }

    [Fact]
    public void Diff_RemovedSchema_IsBreaking()
    {
        var baseline = Doc(new NimBus.PlatformConfiguration());
        var updated = (JObject)baseline.DeepClone();
        ((JObject)updated["components"]!["schemas"]!).Remove("OrderPlaced");

        var result = AsyncApiDiff.Diff(baseline, updated);
        Assert.True(result.HasBreaking);
        Assert.Contains(result.Changes, c => c.Category == "schemas" && c.Kind == ChangeKind.Removed && c.Breaking);
    }

    [Fact]
    public void Diff_RemovedProperty_IsBreaking()
    {
        var baseline = Doc(new NimBus.PlatformConfiguration());
        var updated = (JObject)baseline.DeepClone();
        ((JObject)updated["components"]!["schemas"]!["OrderPlaced"]!["properties"]!).Remove("customerId");

        Assert.True(AsyncApiDiff.Diff(baseline, updated).HasBreaking);
    }

    [Fact]
    public void Diff_NewlyRequiredProperty_IsBreaking()
    {
        var oldSchema = new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { ["x"] = new JObject { ["type"] = "string" } },
        };
        var newSchema = (JObject)oldSchema.DeepClone();
        newSchema["required"] = new JArray("x");

        Assert.True(AsyncApiDiff.Diff(WrapSchema("E", oldSchema), WrapSchema("E", newSchema)).HasBreaking);
    }

    [Fact]
    public void Diff_ScalarFormatChange_IsBreaking()
    {
        var baseline = Doc(new NimBus.PlatformConfiguration());
        var updated = (JObject)baseline.DeepClone();
        // totalAmount: number → integer/int64 (effective shape change).
        updated["components"]!["schemas"]!["OrderPlaced"]!["properties"]!["totalAmount"] =
            new JObject { ["type"] = "integer", ["format"] = "int64" };

        Assert.True(AsyncApiDiff.Diff(baseline, updated).HasBreaking);
    }

    [Fact]
    public void Diff_ScalarToRefChange_IsBreaking()
    {
        var baseline = Doc(new NimBus.PlatformConfiguration());
        var updated = (JObject)baseline.DeepClone();
        updated["components"]!["schemas"]!["OrderPlaced"]!["properties"]!["currencyCode"] =
            new JObject { ["$ref"] = "#/components/schemas/NimBusMessageHeaders" };

        Assert.True(AsyncApiDiff.Diff(baseline, updated).HasBreaking);
    }

    [Fact]
    public void Diff_ArrayItemsTypeChange_IsBreaking()
    {
        var oldSchema = new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { ["tags"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } } },
        };
        var newSchema = new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { ["tags"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer" } } },
        };

        Assert.True(AsyncApiDiff.Diff(WrapSchema("E", oldSchema), WrapSchema("E", newSchema)).HasBreaking);
    }

    [Fact]
    public void Diff_RemovedEnumValue_IsBreaking()
    {
        var oldSchema = SchemaWithEnum("A", "B");
        var newSchema = SchemaWithEnum("A");
        Assert.True(AsyncApiDiff.Diff(WrapSchema("E", oldSchema), WrapSchema("E", newSchema)).HasBreaking);
    }

    [Fact]
    public void Diff_AddedEnumValue_IsNotBreaking()
    {
        var oldSchema = SchemaWithEnum("A");
        var newSchema = SchemaWithEnum("A", "B");
        Assert.False(AsyncApiDiff.Diff(WrapSchema("E", oldSchema), WrapSchema("E", newSchema)).HasBreaking);
    }

    [Fact]
    public void Diff_RootSchemaRemovedEnumValue_IsBreaking()
    {
        // A component schema that is itself an enum (e.g. components.schemas.Status).
        var oldSchema = new JObject { ["type"] = "string", ["enum"] = new JArray("A", "B") };
        var newSchema = new JObject { ["type"] = "string", ["enum"] = new JArray("A") };
        Assert.True(AsyncApiDiff.Diff(WrapSchema("Status", oldSchema), WrapSchema("Status", newSchema)).HasBreaking);
    }

    [Fact]
    public void Diff_RootSchemaAddedEnumValue_IsNotBreaking()
    {
        var oldSchema = new JObject { ["type"] = "string", ["enum"] = new JArray("A") };
        var newSchema = new JObject { ["type"] = "string", ["enum"] = new JArray("A", "B") };
        Assert.False(AsyncApiDiff.Diff(WrapSchema("Status", oldSchema), WrapSchema("Status", newSchema)).HasBreaking);
    }

    [Fact]
    public void Diff_RootSchemaTypeChange_IsBreaking()
    {
        // A component schema whose own effective shape changes (scalar type/format).
        var oldSchema = new JObject { ["type"] = "string" };
        var newSchema = new JObject { ["type"] = "integer", ["format"] = "int32" };
        Assert.True(AsyncApiDiff.Diff(WrapSchema("Amount", oldSchema), WrapSchema("Amount", newSchema)).HasBreaking);
    }

    [Fact]
    public void Diff_DescriptionOnlyChange_IsNotBreaking()
    {
        var baseline = Doc(new NimBus.PlatformConfiguration());
        var updated = (JObject)baseline.DeepClone();
        updated["components"]!["schemas"]!["OrderPlaced"]!["description"] = "New docs.";

        Assert.False(AsyncApiDiff.Diff(baseline, updated).HasBreaking);
    }

    [Fact]
    public void Diff_RootSchemaMetadataOnlyChange_IsReportedNonBreaking()
    {
        // A component schema whose ONLY change is root metadata (title/description/deprecated) must be
        // classified as a non-breaking change, not swallowed so `diff` prints "No differences".
        var oldSchema = new JObject { ["type"] = "object", ["description"] = "old docs" };
        var newSchema = new JObject
        {
            ["type"] = "object",
            ["description"] = "new docs",
            ["title"] = "Order",
            ["deprecated"] = true,
        };

        var result = AsyncApiDiff.Diff(WrapSchema("Order", oldSchema), WrapSchema("Order", newSchema));
        Assert.NotEmpty(result.Changes);
        Assert.Contains(result.Changes, c => c.Category == "schemas"
            && c.Path.EndsWith("Order.description", StringComparison.Ordinal) && !c.Breaking);
        Assert.Contains(result.Changes, c => c.Path.EndsWith("Order.title", StringComparison.Ordinal) && !c.Breaking);
        Assert.Contains(result.Changes, c => c.Path.EndsWith("Order.deprecated", StringComparison.Ordinal) && !c.Breaking);
        Assert.False(result.HasBreaking);
    }

    [Fact]
    public void Diff_PropertyMinimumTightened_IsBreaking()
    {
        // [Range] lower bound raised: values valid before are now rejected → breaking.
        var oldSchema = new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { ["qty"] = new JObject { ["type"] = "integer", ["format"] = "int32", ["minimum"] = 1 } },
        };
        var newSchema = (JObject)oldSchema.DeepClone();
        newSchema["properties"]!["qty"]!["minimum"] = 5;

        var result = AsyncApiDiff.Diff(WrapSchema("E", oldSchema), WrapSchema("E", newSchema));
        Assert.True(result.HasBreaking);
        Assert.Contains(result.Changes, c => c.Path.EndsWith("qty.minimum", StringComparison.Ordinal) && c.Breaking);
    }

    [Fact]
    public void Diff_PropertyMaximumRelaxed_IsReportedNonBreaking()
    {
        // [Range] upper bound raised: previously-valid values still valid, more now allowed → additive.
        var oldSchema = new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { ["qty"] = new JObject { ["type"] = "integer", ["format"] = "int32", ["maximum"] = 10 } },
        };
        var newSchema = (JObject)oldSchema.DeepClone();
        newSchema["properties"]!["qty"]!["maximum"] = 100;

        var result = AsyncApiDiff.Diff(WrapSchema("E", oldSchema), WrapSchema("E", newSchema));
        Assert.Contains(result.Changes, c => c.Path.EndsWith("qty.maximum", StringComparison.Ordinal) && !c.Breaking);
        Assert.False(result.HasBreaking);
    }

    [Fact]
    public void Diff_PropertyDescriptionChanged_IsReportedNonBreaking()
    {
        // Reviewer's core concern: a property metadata change must be classified, not swallowed as "No differences".
        var oldSchema = new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { ["qty"] = new JObject { ["type"] = "integer", ["description"] = "old docs" } },
        };
        var newSchema = (JObject)oldSchema.DeepClone();
        newSchema["properties"]!["qty"]!["description"] = "new docs";

        var result = AsyncApiDiff.Diff(WrapSchema("E", oldSchema), WrapSchema("E", newSchema));
        Assert.NotEmpty(result.Changes);
        Assert.Contains(result.Changes, c => c.Path.EndsWith("qty.description", StringComparison.Ordinal) && !c.Breaking);
        Assert.False(result.HasBreaking);
    }

    [Fact]
    public void Diff_ChangedChannel_MessageRefRemoved_IsBreaking()
    {
        var baseline = Doc(new NimBus.PlatformConfiguration());
        var updated = (JObject)baseline.DeepClone();
        ((JObject)updated["channels"]!["StorefrontEndpoint"]!["messages"]!).Remove("OrderPlaced");

        var result = AsyncApiDiff.Diff(baseline, updated);
        Assert.True(result.HasBreaking);
        Assert.Contains(result.Changes, c => c.Category == "channels" && c.Kind == ChangeKind.Removed && c.Breaking);
    }

    [Fact]
    public void Diff_ChangedChannel_MessageRefRetargeted_IsBreaking()
    {
        // A same-key channel message whose $ref is retargeted to a different component message changes
        // the channel/message contract even though the message key is unchanged — must be Changed + breaking,
        // never swallowed as "No differences".
        var baseline = Doc(new NimBus.PlatformConfiguration());
        var updated = (JObject)baseline.DeepClone();
        updated["channels"]!["StorefrontEndpoint"]!["messages"]!["OrderPlaced"] =
            new JObject { ["$ref"] = "#/components/messages/OrderCancelled" };

        var result = AsyncApiDiff.Diff(baseline, updated);
        Assert.True(result.HasBreaking);
        Assert.Contains(result.Changes, c => c.Category == "channels" && c.Kind == ChangeKind.Changed && c.Breaking
            && c.Path.EndsWith("messages.OrderPlaced", StringComparison.Ordinal));
    }

    [Fact]
    public void Diff_ChangedChannel_BindingsChange_IsNotBreaking()
    {
        var baseline = Doc(new NimBus.PlatformConfiguration());
        var updated = (JObject)baseline.DeepClone();
        updated["channels"]!["StorefrontEndpoint"]!["x-servicebus"]!["duplicateDetectionHistoryTimeWindow"] = "PT20M";

        var result = AsyncApiDiff.Diff(baseline, updated);
        Assert.False(result.HasBreaking);
        Assert.Contains(result.Changes, c => c.Category == "channels" && c.Kind == ChangeKind.Changed && !c.Breaking);
    }

    [Fact]
    public void Diff_ChangedOperation_MessageAssociationRemoved_IsBreaking()
    {
        var baseline = Doc(new NimBus.PlatformConfiguration());
        var updated = (JObject)baseline.DeepClone();
        ((JArray)updated["operations"]!["StorefrontEndpoint_send_OrderPlaced"]!["messages"]!).Clear();

        Assert.True(AsyncApiDiff.Diff(baseline, updated).HasBreaking);
    }

    [Fact]
    public void Diff_ChangedOperation_ActionFlip_IsBreaking()
    {
        var baseline = Doc(new NimBus.PlatformConfiguration());
        var updated = (JObject)baseline.DeepClone();
        updated["operations"]!["StorefrontEndpoint_send_OrderPlaced"]!["action"] = "receive";

        Assert.True(AsyncApiDiff.Diff(baseline, updated).HasBreaking);
    }

    [Fact]
    public void Diff_ChangedOperation_ChannelRetarget_IsBreaking()
    {
        var baseline = Doc(new NimBus.PlatformConfiguration());
        var updated = (JObject)baseline.DeepClone();
        updated["operations"]!["StorefrontEndpoint_send_OrderPlaced"]!["channel"] =
            new JObject { ["$ref"] = "#/channels/BillingEndpoint" };

        Assert.True(AsyncApiDiff.Diff(baseline, updated).HasBreaking);
    }

    [Fact]
    public void Diff_ChangedMessage_PayloadRetarget_IsBreaking()
    {
        var baseline = Doc(new NimBus.PlatformConfiguration());
        var updated = (JObject)baseline.DeepClone();
        updated["components"]!["messages"]!["OrderPlaced"]!["payload"] =
            new JObject { ["$ref"] = "#/components/schemas/NimBusMessageHeaders" };

        Assert.True(AsyncApiDiff.Diff(baseline, updated).HasBreaking);
    }

    [Fact]
    public void Diff_ChangedMessage_RequiresSessionChange_IsBreaking()
    {
        var baseline = Doc(new NimBus.PlatformConfiguration());
        var updated = (JObject)baseline.DeepClone();
        updated["components"]!["messages"]!["OrderPlaced"]!["x-servicebus"]!["requiresSession"] = false;

        Assert.True(AsyncApiDiff.Diff(baseline, updated).HasBreaking);
    }

    [Fact]
    public void Diff_ChangedMessage_TitleOnly_IsNotBreaking()
    {
        var baseline = Doc(new NimBus.PlatformConfiguration());
        var updated = (JObject)baseline.DeepClone();
        updated["components"]!["messages"]!["OrderPlaced"]!["title"] = "Renamed";

        Assert.False(AsyncApiDiff.Diff(baseline, updated).HasBreaking);
    }

    // ---------------- enrichment mapping ----------------

    [Fact]
    public void Enrichment_FluentValues_SurfaceInDocument()
    {
        var platform = new FakePlatform(new FakeEndpoint("Ep", produces: new[] { typeof(EnrichEvent) }));
        var registry = new AsyncApiEnrichmentRegistry();
        var options = registry.For(typeof(EnrichEvent));
        options.Title = "Enriched";
        options.Summary = "A summary";
        options.Owner = "alice";
        options.Team = "payments";
        options.BusinessCapability = "billing";
        options.Version = "2";
        options.Deprecated = true;
        options.ExternalDocsUrl = "https://docs.example.com/enrich";
        options.Tags.Add("gov");
        options.Examples.Add(new AsyncApiMessageExample { Name = "ex1", Payload = new Dictionary<string, object> { ["id"] = "x" } });

        var doc = Doc(platform, registry);
        var message = doc["components"]!["messages"]!["EnrichEvent"]!;

        Assert.Equal("Enriched", message["title"]!.Value<string>());
        Assert.Equal("A summary", message["summary"]!.Value<string>());
        Assert.Equal("alice", message["x-nimbus-governance"]!["owner"]!.Value<string>());
        Assert.Equal("payments", message["x-nimbus-governance"]!["team"]!.Value<string>());
        Assert.Equal("billing", message["x-nimbus-governance"]!["businessCapability"]!.Value<string>());
        Assert.Equal("2", message["x-nimbus-governance"]!["version"]!.Value<string>());
        Assert.True(message["x-nimbus-governance"]!["deprecated"]!.Value<bool>());
        Assert.Equal("https://docs.example.com/enrich", message["externalDocs"]!["url"]!.Value<string>());
        Assert.Contains(message["tags"]!.Select(t => t["name"]!.Value<string>()), n => n == "gov");
        Assert.Contains(message["examples"]!, e => e["name"]!.Value<string>() == "ex1");

        // deprecated marker lives on the schema object, not the message object.
        Assert.True(doc["components"]!["schemas"]!["EnrichEvent"]!["deprecated"]!.Value<bool>());
        Assert.Null(message["deprecated"]);
    }

    [Fact]
    public void Enrichment_CustomName_SurfacesOnMessageAndSchemaTitle()
    {
        var platform = new FakePlatform(new FakeEndpoint("Ep", produces: new[] { typeof(EnrichEvent) }));
        var registry = new AsyncApiEnrichmentRegistry();
        registry.For(typeof(EnrichEvent)).Name = "CustomerOnboarded";

        var doc = Doc(platform, registry);

        Assert.Equal("CustomerOnboarded", doc["components"]!["messages"]!["EnrichEvent"]!["name"]!.Value<string>());
        Assert.Equal("CustomerOnboarded", doc["components"]!["schemas"]!["EnrichEvent"]!["title"]!.Value<string>());
        // Component keys are unchanged so the payload $ref still resolves.
        Assert.Equal("#/components/schemas/EnrichEvent", doc["components"]!["messages"]!["EnrichEvent"]!["payload"]!["$ref"]!.Value<string>());
        Assert.True(AsyncApiValidator.Validate(doc).IsValid);
    }

    [Fact]
    public void Enrichment_MergePrecedence_FluentWinsScalars_TagsUnion()
    {
        var platform = new FakePlatform(new FakeEndpoint("Ep", produces: new[] { typeof(AttributedEvent) }));
        var registry = new AsyncApiEnrichmentRegistry();
        var options = registry.For(typeof(AttributedEvent));
        options.Title = "Fluent";
        options.Tags.Add("b");

        var message = Doc(platform, registry)["components"]!["messages"]!["AttributedEvent"]!;

        Assert.Equal("Fluent", message["title"]!.Value<string>());
        var tags = message["tags"]!.Select(t => t["name"]!.Value<string>()).ToList();
        Assert.Equal(new[] { "a", "b" }, tags);
    }

    [Fact]
    public void Enrichment_ExtendedAttribute_SurfacesWithoutFluent()
    {
        var platform = new FakePlatform(new FakeEndpoint("Ep", produces: new[] { typeof(AttributedEvent) }));
        var message = Doc(platform)["components"]!["messages"]!["AttributedEvent"]!;

        Assert.Equal("acme", message["x-nimbus-governance"]!["owner"]!.Value<string>());
        Assert.Equal("1.0", message["x-nimbus-governance"]!["version"]!.Value<string>());
        Assert.Equal("CustomAttrName", message["name"]!.Value<string>());
    }

    // ---------------- exit-code runner (F2) ----------------

    [Fact]
    public void RunValidate_ValidDocument_ReturnsZero()
    {
        var path = WriteTempDoc(AsyncApiExporter.Serialize(new NimBus.PlatformConfiguration(), AsyncApiFormat.Yaml), ".yaml");
        try
        {
            Assert.Equal(0, AsyncApiCli.RunValidate(path, new StringWriter()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RunValidate_DanglingRef_ReturnsNonZeroAndNamesRef()
    {
        var doc = Doc(new NimBus.PlatformConfiguration());
        doc["components"]!["messages"]!["OrderPlaced"]!["payload"] = new JObject { ["$ref"] = "#/components/schemas/Nope" };
        var path = WriteTempDoc(doc.ToString(), ".json");
        try
        {
            var writer = new StringWriter();
            Assert.NotEqual(0, AsyncApiCli.RunValidate(path, writer));
            Assert.Contains("Nope", writer.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RunDiff_AdditiveOnly_ReturnsZero()
    {
        var baseline = Doc(new NimBus.PlatformConfiguration());
        var updated = (JObject)baseline.DeepClone();
        ((JObject)updated["channels"]!)["NewEndpoint"] = new JObject { ["address"] = "NewEndpoint" };

        var oldPath = WriteTempDoc(baseline.ToString(), ".json");
        var newPath = WriteTempDoc(updated.ToString(), ".json");
        try
        {
            Assert.Equal(0, AsyncApiCli.RunDiff(oldPath, newPath, new StringWriter()));
        }
        finally
        {
            File.Delete(oldPath);
            File.Delete(newPath);
        }
    }

    [Fact]
    public void RunDiff_BreakingChange_ReturnsNonZeroAndListsIt()
    {
        var baseline = Doc(new NimBus.PlatformConfiguration());
        var updated = (JObject)baseline.DeepClone();
        ((JObject)updated["channels"]!).Remove("StorefrontEndpoint");

        var oldPath = WriteTempDoc(baseline.ToString(), ".json");
        var newPath = WriteTempDoc(updated.ToString(), ".json");
        try
        {
            var writer = new StringWriter();
            Assert.NotEqual(0, AsyncApiCli.RunDiff(oldPath, newPath, writer));
            Assert.Contains("BREAKING", writer.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(oldPath);
            File.Delete(newPath);
        }
    }

    // ---------------- export: fluent-enrichment provider bridge (AC#8 via CLI) ----------------

    [Fact]
    public void ProviderLoader_ResolvesNamedPublicProvider_ProducesFluentDocument()
    {
        var provider = AsyncApiProviderLoader.Resolve(
            typeof(AsyncApiGovernanceTests).Assembly,
            typeof(FluentProviderDouble).FullName);

        var doc = provider.GetDocument(AsyncApiFormat.Json);

        // The fluent-registry owner surfaces via x-nimbus-governance, proving the CLI provider bridge
        // carries fluent (Publish<T>(o => o.AsyncApi…)) metadata the static platform export cannot.
        Assert.Contains(FluentProviderDouble.OwnerMarker, doc, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderLoader_ResolvesPublicParameterlessFactory_ProducesFluentDocument()
    {
        // A host that registers its provider through the SDK (a private, DI-backed IAsyncApiDocumentProvider)
        // exposes it to the CLI via a public parameterless IAsyncApiDocumentProviderFactory. The loader
        // must discover the factory and call Create(), not require a public parameterless provider type.
        var provider = AsyncApiProviderLoader.Resolve(
            typeof(AsyncApiGovernanceTests).Assembly,
            typeof(FluentProviderFactoryDouble).FullName);

        var doc = provider.GetDocument(AsyncApiFormat.Json);
        Assert.Contains(FluentProviderDouble.OwnerMarker, doc, StringComparison.Ordinal);
    }

    [Fact]
    public void RunExport_WithProviderAssembly_IncludesFluentMetadata()
    {
        var output = Path.Combine(Path.GetTempPath(), $"nimbus-asyncapi-{Guid.NewGuid():N}.json");
        try
        {
            var exit = AsyncApiCli.RunExport(
                output,
                AsyncApiFormat.Json,
                new StringWriter(),
                assemblyPath: typeof(AsyncApiGovernanceTests).Assembly.Location,
                providerType: typeof(FluentProviderDouble).FullName);

            Assert.Equal(0, exit);
            var content = File.ReadAllText(output);
            Assert.Contains(FluentProviderDouble.OwnerMarker, content, StringComparison.Ordinal);
            Assert.True(AsyncApiValidator.Validate(JObject.Parse(content)).IsValid);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public void RunExport_WithMissingAssembly_ReturnsNonZeroAndReports()
    {
        var writer = new StringWriter();
        var exit = AsyncApiCli.RunExport(
            Path.Combine(Path.GetTempPath(), $"nimbus-asyncapi-{Guid.NewGuid():N}.json"),
            AsyncApiFormat.Json,
            writer,
            assemblyPath: Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.dll"));

        Assert.NotEqual(0, exit);
        Assert.NotEqual(string.Empty, writer.ToString());
    }

    [Fact]
    public void RunExport_BuiltInPlatform_StillWritesValidDocument()
    {
        var output = Path.Combine(Path.GetTempPath(), $"nimbus-asyncapi-{Guid.NewGuid():N}.yaml");
        try
        {
            Assert.Equal(0, AsyncApiCli.RunExport(output, AsyncApiFormat.Yaml, new StringWriter()));
            Assert.True(AsyncApiValidator.Validate(AsyncApiDocumentLoader.LoadFile(output)).IsValid);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    // ---------------- helpers & doubles ----------------

    private static string WriteTempDoc(string content, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nimbus-asyncapi-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, content);
        return path;
    }

    private static JObject WrapSchema(string name, JObject schema) => new()
    {
        ["asyncapi"] = "3.0.0",
        ["components"] = new JObject { ["schemas"] = new JObject { [name] = schema } },
    };

    private static JObject SchemaWithEnum(params string[] values) => new()
    {
        ["type"] = "object",
        ["properties"] = new JObject { ["status"] = new JObject { ["type"] = "string", ["enum"] = new JArray(values) } },
    };

    private sealed class FakePlatform : Platform
    {
        public FakePlatform(params FakeEndpoint[] endpoints)
        {
            foreach (var endpoint in endpoints) AddEndpoint(endpoint);
        }
    }

    private sealed class FakeSystem : ISystem
    {
        public FakeSystem(string id) => SystemId = id;

        public string SystemId { get; }
    }

    private sealed class FakeEndpoint : IEndpoint
    {
        public FakeEndpoint(string id, Type[]? produces = null, Type[]? consumes = null)
        {
            Id = id;
            Name = id;
            EventTypesProduced = (produces ?? Array.Empty<Type>()).Select(t => (IEventType)new EventType(t)).ToList();
            EventTypesConsumed = (consumes ?? Array.Empty<Type>()).Select(t => (IEventType)new EventType(t)).ToList();
        }

        public string Id { get; }
        public string Name { get; }
        public string Description => $"{Id} description";
        public string Namespace => "Tests";
        public string SecurityGroupName => string.Empty;
        public ISystem System => new FakeSystem(Id);
        public IEnumerable<IEventType> EventTypesProduced { get; }
        public IEnumerable<IEventType> EventTypesConsumed { get; }
        public IEnumerable<IRoleAssignment> RoleAssignments => Array.Empty<IRoleAssignment>();
    }

    private sealed class EnrichEvent : Event
    {
        [Required]
        public Guid Id { get; set; }
    }

    [AsyncApiMessage(Title = "Attr", Tags = new[] { "a" }, Owner = "acme", Version = "1.0", Name = "CustomAttrName")]
    private sealed class AttributedEvent : Event
    {
        [Required]
        public Guid Id { get; set; }
    }

    // Public (so AsyncApiProviderLoader, which scans exported types, can discover it) with a public
    // parameterless ctor. Simulates a host that recorded fluent enrichment (here via the registry the
    // SDK Publish<T>(o => o.AsyncApi…) path feeds) and exposes its enriched document to the CLI.
    public sealed class FluentProviderDouble : IAsyncApiDocumentProvider
    {
        public const string OwnerMarker = "AF98-Fluent-Owner";

        public string GetDocument(AsyncApiFormat format)
        {
            var platform = new FakePlatform(new FakeEndpoint("Ep", produces: new[] { typeof(EnrichEvent) }));
            var registry = new AsyncApiEnrichmentRegistry();
            registry.For(typeof(EnrichEvent)).Owner = OwnerMarker;
            return AsyncApiExporter.Serialize(platform, format, registry);
        }
    }

    // Public parameterless factory (the CLI-export convention for reaching a DI-backed provider). Simulates
    // a host exposing its enriched document through IAsyncApiDocumentProviderFactory rather than a public
    // parameterless IAsyncApiDocumentProvider.
    public sealed class FluentProviderFactoryDouble : IAsyncApiDocumentProviderFactory
    {
        public IAsyncApiDocumentProvider Create() => new FluentProviderDouble();
    }
}
