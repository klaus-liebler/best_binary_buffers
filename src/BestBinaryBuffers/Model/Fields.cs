namespace BestBinaryBuffers.Model;

// Field hierarchy instead of one record with many optional properties -- makes codegen (switch/pattern
// matching in the generators) explicit per field kind, same rationale as the JSON-schema generator this
// library replaces.
public abstract record FieldBase(string Name, string? Description);

// IsSigned drives the TS-side choice between getUint*/getInt* (DataView). IsFloat needs its own
// encoding path (bit pattern via memcpy/DataView.getFloat32, NOT an integer bitshift cast -- a numeric
// cast to uint32_t would perform a VALUE conversion instead of a bit reinterpretation and destroy the
// value, e.g. (uint32_t)3.14f == 3, not the IEEE754 bit pattern).
public sealed record FixedField(
	string Name,
	string? Description,
	string CppType,
	string TsType,
	int Size,
	bool IsBool,
	bool IsSigned,
	bool IsFloat = false
) : FieldBase(Name, Description);

public sealed record EnumRefField(string Name, string? Description, EnumDef Enum) : FieldBase(Name, Description);

public sealed record StructRefField(string Name, string? Description, StructDef Struct) : FieldBase(Name, Description);

// Always null-terminated on the wire (UTF-8 bytes + 0x00) -- no length prefix.
public sealed record StringField(string Name, string? Description) : FieldBase(Name, Description);

// [BinaryCount(N)] on a Struct/Class field -- N identical fixed values back to back (a C array), e.g. a
// 6-byte MAC address as "byte[] Mac" with [BinaryCount(6)] instead of 6 individually named fields.
// Stays a fixed-size field type (size = element size * count), never wire-prefixed.
public sealed record RepeatedField(string Name, string? Description, FieldBase Element, int Count) : FieldBase(Name, Description);

// Message-only. Count-prefixed (ushort) array of elements with a compile-time-known fixed size (Element
// is a FixedField/EnumRefField/StructRefField "prototype" describing the element's wire shape -- its own
// Name is irrelevant, codegen always addresses "payload.<ArrayField.Name>[i]"). Self-terminating on
// decode (exact byte length is computable from count*elementSize), so -- unlike PolymorphicArrayField --
// it may appear anywhere in a message's field list, not just trailing.
public sealed record UniformPackedArrayField(string Name, string? Description, FieldBase Element) : FieldBase(Name, Description);

// Message-only. Count-prefixed (ushort) array of null-terminated strings. Each element is
// self-terminating (scan for 0x00), so decoding the whole array is eager but still fully determines its
// own byte length -- may appear anywhere in a message's field list, not just trailing.
public sealed record UniformVariableArrayField(string Name, string? Description) : FieldBase(Name, Description);

// Message/Class-only. Heterogeneous/polymorphic array: each element is one of several declared classes,
// tagged with a 2-byte classId. Decoding a message's Data/Count is DEFERRED (top-level Decode() just
// captures "count" and jumps to end of frame -- actual per-element decoding happens later, on demand,
// via a separate visitor function) to avoid double-parsing. Precisely BECAUSE of that deferral, at most
// one PolymorphicArrayField/PolymorphicField total is allowed per owner, and it must be the last field
// (see SchemaValidation) -- a second one wouldn't know where the frame actually continues. This is a
// deliberate tightening vs. the JSON-schema generator this library replaces, which permitted several
// consecutive trailing class-array fields but had a latent decode bug for exactly that case (the second
// trailing field's Decode always read past a "pos" already forced to the frame's end).
public sealed record PolymorphicArrayField(string Name, string? Description, IReadOnlyList<ClassDef> Variants) : FieldBase(Name, Description);

// Like PolymorphicArrayField, but a single element instead of a list (no count prefix) -- for fields
// that are, at runtime, one of several classes without being a list (e.g. "one of three schedule
// variants"). Same trailing-only / at-most-one restriction as PolymorphicArrayField, for the same reason.
public sealed record PolymorphicField(string Name, string? Description, IReadOnlyList<ClassDef> Variants) : FieldBase(Name, Description);
