#pragma warning disable CA1707, CA2007
using System;
using System.Globalization;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.Extensions.Primitives;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Pins the binding semantics of <see cref="EnumMemberModelBinder"/>:
/// EnumMember values and field names resolve exactly, anything else falls
/// back to case-insensitive <see cref="Enum.TryParse(Type, string, bool, out object)"/>,
/// and unknown values produce a model error.
/// </summary>
[TestClass]
public sealed class EnumMemberModelBinderTests
{
    private enum SampleStatus
    {
        [EnumMember(Value = "in-progress")]
        InProgress,

        [EnumMember(Value = "dead-lettered")]
        DeadLettered,

        Done,
    }

    [TestMethod]
    public async Task Binds_enum_member_value()
    {
        var result = await BindAsync("in-progress");

        Assert.IsTrue(result.IsModelSet);
        Assert.AreEqual(SampleStatus.InProgress, result.Model);
    }

    [TestMethod]
    public async Task Binds_field_name()
    {
        var result = await BindAsync("Done");

        Assert.IsTrue(result.IsModelSet);
        Assert.AreEqual(SampleStatus.Done, result.Model);
    }

    [TestMethod]
    public async Task Falls_back_to_case_insensitive_parse()
    {
        var result = await BindAsync("deadlettered");

        Assert.IsTrue(result.IsModelSet);
        Assert.AreEqual(SampleStatus.DeadLettered, result.Model);
    }

    [TestMethod]
    public async Task Rejects_unknown_value_with_model_error()
    {
        var context = MakeContext("no-such-status");

        await new EnumMemberModelBinder(typeof(SampleStatus)).BindModelAsync(context);

        Assert.IsFalse(context.Result.IsModelSet);
        Assert.AreEqual(1, context.ModelState.ErrorCount);
    }

    [TestMethod]
    public async Task Leaves_result_unset_for_empty_value()
    {
        var context = MakeContext(string.Empty);

        await new EnumMemberModelBinder(typeof(SampleStatus)).BindModelAsync(context);

        Assert.IsFalse(context.Result.IsModelSet);
        Assert.AreEqual(0, context.ModelState.ErrorCount);
    }

    private static async Task<ModelBindingResult> BindAsync(string value)
    {
        var context = MakeContext(value);
        await new EnumMemberModelBinder(typeof(SampleStatus)).BindModelAsync(context);
        return context.Result;
    }

    private static DefaultModelBindingContext MakeContext(string value)
    {
        return new DefaultModelBindingContext
        {
            ModelMetadata = new EmptyModelMetadataProvider().GetMetadataForType(typeof(SampleStatus)),
            ModelName = "status",
            ModelState = new ModelStateDictionary(),
            ValueProvider = new SingleValueProvider("status", value),
        };
    }

    private sealed class SingleValueProvider : IValueProvider
    {
        private readonly string _key;
        private readonly string _value;

        public SingleValueProvider(string key, string value)
        {
            _key = key;
            _value = value;
        }

        public bool ContainsPrefix(string prefix) => prefix == _key;

        public ValueProviderResult GetValue(string key)
            => key == _key
                ? new ValueProviderResult(new StringValues(_value), CultureInfo.InvariantCulture)
                : ValueProviderResult.None;
    }
}
