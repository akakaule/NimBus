#pragma warning disable CA1707, CA2007
using NimBus.Core.Events;

namespace NimBus.Core.Tests.Events;

/// <summary>
/// <see cref="Property.TypeName"/> is what the WebApp's event-type page prints in the
/// type column and in the placeholder example payload. Reflection's raw
/// <c>Type.Name</c> renders <c>Guid?</c> as <c>Nullable`1</c>, which is what operators
/// saw on the CrmErpDemo contracts; these pin the C#-style rendering instead.
/// </summary>
[TestClass]
public class PropertyTypeNameTests
{
    private sealed class Sample
    {
        public Guid Id { get; set; }

        public Guid? OptionalId { get; set; }

        public int? Count { get; set; }

        public string Name { get; set; } = string.Empty;

        public List<string> Tags { get; set; } = [];

        public Dictionary<string, int?> Scores { get; set; } = [];

        public string[] Aliases { get; set; } = [];

        public int[,] Grid { get; set; } = new int[0, 0];
    }

    private static Property For(string name) => new(typeof(Sample).GetProperty(name)!);

    [TestMethod]
    public void TypeName_PlainType_IsUnchanged()
    {
        Assert.AreEqual("Guid", For(nameof(Sample.Id)).TypeName);
        Assert.AreEqual("String", For(nameof(Sample.Name)).TypeName);
    }

    [TestMethod]
    public void TypeName_NullableValueType_RendersWithQuestionMark()
    {
        Assert.AreEqual("Guid?", For(nameof(Sample.OptionalId)).TypeName);
        Assert.AreEqual("Int32?", For(nameof(Sample.Count)).TypeName);
    }

    [TestMethod]
    public void TypeName_Generic_RendersArgumentsInsteadOfArity()
    {
        Assert.AreEqual("List<String>", For(nameof(Sample.Tags)).TypeName);
        Assert.AreEqual("Dictionary<String, Int32?>", For(nameof(Sample.Scores)).TypeName);
    }

    [TestMethod]
    public void TypeName_Array_RendersBrackets()
    {
        Assert.AreEqual("String[]", For(nameof(Sample.Aliases)).TypeName);
        Assert.AreEqual("Int32[,]", For(nameof(Sample.Grid)).TypeName);
    }

    [TestMethod]
    public void TypeFullName_QualifiesWithoutAssemblyNoise()
    {
        Assert.AreEqual("System.Guid?", For(nameof(Sample.OptionalId)).TypeFullName);
        Assert.AreEqual(
            "System.Collections.Generic.Dictionary<System.String, System.Int32?>",
            For(nameof(Sample.Scores)).TypeFullName);
        Assert.AreEqual("System.String", For(nameof(Sample.Name)).TypeFullName);
    }
}
