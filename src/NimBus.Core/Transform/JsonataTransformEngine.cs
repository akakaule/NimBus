using System;
using System.IO;
using Jsonata.Net.Native;
using Jsonata.Net.Native.Json;

namespace NimBus.Core.Transform;

/// <summary>
/// <see cref="IMappingTransformEngine"/> backed by the Jsonata.Net.Native JSONata evaluator.
/// Compiles the transform expression and evaluates it against the input JSON, translating any
/// library error into a <see cref="MappingTransformException"/> so callers have one exception
/// type to handle (spec 023).
/// </summary>
public sealed class JsonataTransformEngine : IMappingTransformEngine
{
    /// <inheritdoc/>
    public string Transform(string transform, string inputJson)
    {
        if (string.IsNullOrWhiteSpace(transform))
            throw new MappingTransformException("Transform expression is empty.");

        try
        {
            var query = new JsonataQuery(transform);
            var inputToken = JToken.Parse(new StringReader(inputJson));
            var result = query.Eval(inputToken, EvaluationEnvironment.DefaultEnvironment);
            return result.ToFlatString();
        }
        catch (MappingTransformException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new MappingTransformException($"Transform failed: {ex.Message}", ex);
        }
    }
}
