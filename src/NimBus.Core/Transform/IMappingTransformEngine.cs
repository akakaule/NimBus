namespace NimBus.Core.Transform;

/// <summary>Deterministically applies a declarative transform to a JSON document (spec 023).</summary>
public interface IMappingTransformEngine
{
    /// <summary>
    /// Applies <paramref name="transform"/> to <paramref name="inputJson"/> and returns the
    /// resulting JSON. Throws <see cref="MappingTransformException"/> on a malformed transform
    /// or input. Must be deterministic and side-effect free.
    /// </summary>
    string Transform(string transform, string inputJson);
}

/// <summary>Thrown when a transform cannot be compiled or applied.</summary>
public sealed class MappingTransformException : System.Exception
{
    /// <inheritdoc cref="System.Exception(string, System.Exception)"/>
    public MappingTransformException(string message, System.Exception? inner = null) : base(message, inner) { }
}
