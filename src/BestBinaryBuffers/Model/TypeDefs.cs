namespace BestBinaryBuffers.Model;

public sealed record EnumValue(string Name, long Value);

// Reusable, named enum (namespace-level). Size in bytes (1/2/4) comes from the C# underlying type
// (byte/ushort/uint).
public sealed record EnumDef(string Namespace, string Name, string? Description, int Size, IReadOnlyList<EnumValue> Values);

// Pure fixed-layout data (Fixed/EnumRef/StructRef/Repeated fields only, recursively). No runtime
// header/id (never sent standalone, always embedded).
public sealed record StructDef(string Namespace, string Name, string? Description, IReadOnlyList<FieldBase> Fields);

// Like a Message (name + fields), but only usable as an array-/polymorphic-field element type, no
// message header of its own. Unlike Struct, may also contain StringField and (as the trailing field)
// one PolymorphicField, but never an array. Id is GLOBAL (not per-namespace) because a single
// polymorphic list can mix classes from different namespaces and the 2-byte runtime discriminator
// alone must be unique.
public sealed record ClassDef(string Namespace, string Name, string? Description, IReadOnlyList<FieldBase> Fields, int Id);

public sealed record Message(string Namespace, string Name, int Id, MessageKind Kind, string? Description, IReadOnlyList<FieldBase> Fields);

// Fully resolved namespace (after parsing). Name=="" is the reserved NULL namespace (Id always 0) --
// its contents are generated without an enclosing "namespace {}" block (C++) / "export namespace {}"
// block (TS), directly at WsProtocol/module level.
public sealed record NamespaceDef(
	string Name,
	int Id,
	IReadOnlyList<EnumDef> Enums,
	IReadOnlyList<StructDef> Structs,
	IReadOnlyList<ClassDef> Classes,
	IReadOnlyList<Message> Messages
);
