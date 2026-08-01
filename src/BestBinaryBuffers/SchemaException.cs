namespace BestBinaryBuffers;

/// <summary>Thrown for any schema problem BestBinaryBuffers itself detects -- both real C# syntax
/// errors (surfaced via Roslyn's free syntax diagnostics) and our own DSL-specific validation (unknown
/// type reference, array in the wrong place, non-public field, ...). Always carries enough context
/// (file, and usually a "Type X, field Y: ..." prefix) to locate the offending schema declaration
/// without re-deriving it.</summary>
public sealed class SchemaException(string message) : Exception(message);
