namespace BestBinaryBuffers;

/// <summary>Kind of a <see cref="BinaryMessageAttribute"/>-marked message -- mirrors the wire-level
/// MessageKind enum emitted into the generated C++/TypeScript code.</summary>
public enum MessageKind
{
	Event,
	Request,
	Response,
}

// These attributes are recognized by SchemaParser purely by NAME (syntax-only Roslyn parsing, no
// semantic model/compilation) -- referencing this assembly from a schema project is optional and only
// buys IntelliSense/red-squiggles while authoring, it has no effect on how BestBinaryBuffers itself
// reads a schema file.

/// <summary>Marks an enum/struct/class as a schema type. Name/Namespace default to the real C#
/// identifier/namespace; pass overrides only when the C# identifier must diverge from the wire name
/// (e.g. after a rename, to keep existing wire IDs stable).</summary>
[AttributeUsage(AttributeTargets.Enum | AttributeTargets.Struct | AttributeTargets.Class)]
public sealed class BinaryTypeAttribute(string? name = null, string? @namespace = null) : Attribute
{
	public string? Name { get; } = name;
	public string? Namespace { get; } = @namespace;
}

/// <summary>Marks a class as a message: the root, wire-addressable container (namespace id + type id
/// header). Only messages can be encoded/decoded standalone and are the only place a trailing array
/// field is allowed.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class BinaryMessageAttribute(MessageKind kind, string? name = null, string? @namespace = null) : Attribute
{
	public MessageKind Kind { get; } = kind;
	public string? Name { get; } = name;
	public string? Namespace { get; } = @namespace;
}

/// <summary>Marks an interface as the discriminator for a polymorphic field/array. Every
/// <see cref="BinaryTypeAttribute"/>-marked class implementing this interface becomes one tagged
/// variant (2-byte classId prefix per element).</summary>
[AttributeUsage(AttributeTargets.Interface)]
public sealed class BinaryUnionAttribute : Attribute;

/// <summary>Fixed, non-wire-prefixed repeat count for a field inside a struct/class (e.g. a 6-byte MAC
/// address as `[BinaryCount(6)] public byte[] Mac;`). Not a length-prefixed array -- the count is only
/// known at schema/compile time, never written to the wire.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class BinaryCountAttribute(int count) : Attribute
{
	public int Count { get; } = count;
}
