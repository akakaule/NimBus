#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Transform;

namespace NimBus.Core.Tests;

[TestClass]
public sealed class JsonataTransformEngineTests
{
    private static IMappingTransformEngine Engine() => new JsonataTransformEngine();

    [TestMethod]
    public void Transform_renames_and_derives_fields()
    {
        // Marketing lead -> ERP customer: rename + derive fullName.
        var transform = "{ \"customerId\": leadId, \"fullName\": firstName & ' ' & lastName }";
        var input = "{ \"leadId\": \"L-1\", \"firstName\": \"Ada\", \"lastName\": \"Lovelace\" }";

        var output = Engine().Transform(transform, input);

        StringAssert.Contains(output.Replace(" ", ""), "\"customerId\":\"L-1\"");
        StringAssert.Contains(output.Replace(" ", ""), "\"fullName\":\"AdaLovelace\"");
    }

    [TestMethod]
    public void Transform_is_deterministic()
    {
        var t = "{ \"id\": leadId }";
        var input = "{ \"leadId\": \"L-9\" }";
        Assert.AreEqual(Engine().Transform(t, input), Engine().Transform(t, input));
    }

    [TestMethod]
    public void Transform_malformed_input_throws_MappingTransformException()
    {
        Assert.ThrowsException<MappingTransformException>(
            () => Engine().Transform("{ \"id\": leadId }", "{ not json"));
    }
}
