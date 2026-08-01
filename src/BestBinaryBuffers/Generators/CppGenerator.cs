using System.Text;
using BestBinaryBuffers.Model;

namespace BestBinaryBuffers.Generators;

/// <summary>Generates a single self-contained C++ header from the resolved schema model. Wire format:
/// little-endian, null-terminated strings, ushort (2-byte) count prefixes on Message-level arrays, no
/// dynamic allocation anywhere (all decode results are raw pointers into the original frame buffer plus
/// counts/sizes -- callers that need to actually walk variable-content fields do so via the generated
/// per-field Decode*Elements visitor helpers).</summary>
public static class CppGenerator
{
	public static string Generate(IReadOnlyList<NamespaceDef> namespaces)
	{
		var sb = new StringBuilder();
		sb.Append(GeneratedFileHeader.Text);
		sb.Append("\n#pragma once\n#include <cstdint>\n#include <cstddef>\n#include <cstring>\n\nnamespace WsProtocol {\n\n");
		sb.Append("enum class MessageKind : uint8_t { Event = 0, Request = 1, Response = 2 };\n\n");

		// Four phases instead of "everything in one pass per namespace": enums/structs must be fully
		// declared before classes and messages (referenced there, possibly across namespaces), classes
		// before messages. Each phase reopens "namespace X { ... }" again (legal in C++, "reopening").
		// NULL namespace (Name=="") never gets a wrapping "namespace {}" block -- its contents live
		// directly at WsProtocol scope.
		foreach (var ns in namespaces)
		{
			var open = ns.Name.Length > 0;
			if (open) sb.Append($"namespace {ns.Name} {{\n");
			sb.Append($"constexpr uint16_t NAMESPACE_ID = {ns.Id};\n\n");
			foreach (var e in ns.Enums) sb.Append(GenerateEnum(e));
			foreach (var s in ns.Structs) sb.Append(GenerateStruct(s));
			if (open) sb.Append($"}} // namespace {ns.Name}\n\n");
		}
		foreach (var ns in namespaces)
		{
			if (ns.Classes.Count == 0) continue;
			var open = ns.Name.Length > 0;
			if (open) sb.Append($"namespace {ns.Name} {{\n");
			foreach (var c in ns.Classes) sb.Append(GenerateClass(c));
			if (open) sb.Append($"}} // namespace {ns.Name}\n\n");
		}
		foreach (var ns in namespaces)
		{
			var open = ns.Name.Length > 0;
			if (open) sb.Append($"namespace {ns.Name} {{\n");
			foreach (var msg in ns.Messages) sb.Append(GenerateMessage(msg));
			if (open) sb.Append($"}} // namespace {ns.Name}\n\n");
		}

		sb.Append("} // namespace WsProtocol\n");
		return sb.ToString();
	}

	// --- Naming ------------------------------------------------------------------------------------

	private static string Qualify(string ns, string name) => ns.Length == 0 ? name : $"{ns}::{name}";
	private static string StructTypeName(StructDef s) => Qualify(s.Namespace, s.Name);
	private static string EnumTypeName(EnumDef e) => Qualify(e.Namespace, e.Name);
	private static string ClassQualifiedName(ClassDef c) => Qualify(c.Namespace, c.Name);
	private static string EnumUnderlyingType(int size) => size switch { 1 => "uint8_t", 2 => "uint16_t", 4 => "uint32_t", _ => throw new InvalidOperationException() };
	private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

	// Fixed/EnumRef/StructRef only (RepeatedField element, UniformPackedArrayField element).
	private static string ElementCppType(FieldBase f) => f switch
	{
		FixedField ff => ff.CppType,
		EnumRefField erf => EnumTypeName(erf.Enum),
		StructRefField srf => StructTypeName(srf.Struct),
		_ => throw new InvalidOperationException("Element must be Fixed/EnumRef/StructRef."),
	};

	private static int ElementSize(FieldBase f) => f switch
	{
		FixedField ff => ff.Size,
		EnumRefField erf => erf.Enum.Size,
		StructRefField srf => srf.Struct.Fields.Sum(FixedSizeOf),
		_ => throw new InvalidOperationException("Element must be Fixed/EnumRef/StructRef."),
	};

	// Recursive -- a StructRefField carries the size of its (itself fixed-only) struct, a RepeatedField
	// the size of its element times Count.
	private static int FixedSizeOf(FieldBase f) => f switch
	{
		FixedField ff => ff.Size,
		EnumRefField erf => erf.Enum.Size,
		StructRefField srf => srf.Struct.Fields.Sum(FixedSizeOf),
		RepeatedField rf => ElementSize(rf.Element) * rf.Count,
		_ => throw new InvalidOperationException("Only defined for fixed field types."),
	};

	// --- Field declarations (Payload struct members) ------------------------------------------------

	// ownerName is only used to spell out the correct helper function name in a couple of field
	// comments (Struct/Class fields never contain a Uniform*/Polymorphic* array, so it's unused there).
	private static string FieldDecl(FieldBase f, string ownerName = "") => f switch
	{
		FixedField ff => $"    {ff.CppType} {ff.Name};" + Comment(ff.Description) + "\n",
		EnumRefField erf => $"    {EnumTypeName(erf.Enum)} {erf.Name};" + Comment(erf.Description) + "\n",
		StructRefField srf => $"    {StructTypeName(srf.Struct)} {srf.Name};" + Comment(srf.Description) + "\n",
		RepeatedField rf => $"    {ElementCppType(rf.Element)} {rf.Name}[{rf.Count}];" + Comment(rf.Description) + "\n",
		StringField sf => $"    const char* {sf.Name}; // null-terminiert" + Comment(sf.Description) + "\n",
		UniformPackedArrayField af =>
			$"    const uint8_t* {af.Name}Data; // rohe {af.Name}-Elemente, s. Decode{ownerName}{Capitalize(af.Name)}ElementAt()" + Comment(af.Description) + $"\n    size_t {af.Name}Count;\n",
		UniformVariableArrayField vf =>
			$"    const uint8_t* {vf.Name}Data; // sequenz null-terminierter Strings, s. Decode{ownerName}{Capitalize(vf.Name)}Elements()" + Comment(vf.Description) + $"\n    size_t {vf.Name}Count;\n    size_t {vf.Name}DataSize;\n",
		PolymorphicArrayField paf =>
			$"    const uint8_t* {paf.Name}Data; // vorserialisierte Elemente (je [classId:u16][Klassenfelder]), s. Append{ownerName}{Capitalize(paf.Name)}*Element-Funktionen" + Comment(paf.Description) + $"\n    size_t {paf.Name}Count;\n    size_t {paf.Name}DataSize;\n",
		PolymorphicField pf =>
			$"    const uint8_t* {pf.Name}Data; // vorserialisiertes Element (classId:u16 + Klassenfelder), s. Append{ownerName}{Capitalize(pf.Name)}*Element-Funktionen" + Comment(pf.Description) + $"\n    size_t {pf.Name}DataSize;\n",
		_ => throw new InvalidOperationException(),
	};

	private static string Comment(string? description) => description is not null ? $" // {description}" : "";

	// --- Low level scalar encode/decode (bitshift-based, portable regardless of host struct padding/
	// endianness -- deliberately never relies on raw reinterpret_cast of a packed struct) --------------

	// Accumulator type for the bitshift-based encoding -- MUST be uint64_t for 8-byte fields
	// (int64/uint64), otherwise either the value gets truncated to 32 bits before the first shift
	// (encode) or a shift by >=32 bits on a 32-bit type is performed (decode, undefined behavior in
	// C++). uint32_t remains sufficient (and is kept) for every other size.
	private static string AccumulatorType(int size) => size > 4 ? "uint64_t" : "uint32_t";

	private static string EncodeScalar(string valueExpr, int size)
	{
		var castExpr = $"({AccumulatorType(size)})({valueExpr})";
		var sb = new StringBuilder();
		sb.Append($"    if (pos + {size} > dest_size) return 0;\n");
		if (size == 1) sb.Append($"    dest[pos++] = (uint8_t){castExpr};\n");
		else for (var i = 0; i < size; i++) sb.Append($"    dest[pos++] = (uint8_t)(({castExpr}) >> {i * 8});\n");
		return sb.ToString();
	}

	private static string DecodeScalarInto(string targetExpr, string castType, int size)
	{
		var accType = AccumulatorType(size);
		var sb = new StringBuilder();
		sb.Append($"    if (len < pos + {size}) return false;\n");
		if (size == 1)
		{
			sb.Append($"    {targetExpr} = ({castType})data[pos];\n");
		}
		else
		{
			var terms = string.Join(" | ", Enumerable.Range(0, size).Select(i => i == 0 ? $"({accType})data[pos]" : $"(({accType})data[pos + {i}] << {i * 8})"));
			sb.Append($"    {targetExpr} = ({castType})({terms});\n");
		}
		sb.Append($"    pos += {size};\n");
		return sb.ToString();
	}

	// Float fields: bit pattern via memcpy (NOT an integer-bitshift cast like EncodeScalar -- a numeric
	// cast to uint32_t would perform a VALUE conversion instead of reinterpreting the bit pattern, e.g.
	// (uint32_t)3.14f == 3, not the IEEE754 bits). Assumes little-endian, true for every target platform
	// in this ecosystem (ESP32, STM32, x86/ARM64 dev machines) -- the same assumption the rest of the
	// wire format already makes (multi-byte fields written LSB-first).
	private static string EncodeFloatBytes(string valueExpr, string cppType, int size) =>
		$"    if (pos + {size} > dest_size) return 0;\n" +
		$"    {{ {cppType} tmp_ = {valueExpr}; memcpy(dest + pos, &tmp_, {size}); pos += {size}; }}\n";

	private static string DecodeFloatInto(string targetExpr, int size) =>
		$"    if (len < pos + {size}) return false;\n" +
		$"    memcpy(&{targetExpr}, data + pos, {size});\n" +
		$"    pos += {size};\n";

	private static string Indent(string block, string prefix) =>
		string.Concat(block.Split('\n').Select(line => line.Length == 0 ? line : prefix + line + "\n")).TrimEnd('\n') + "\n";

	// Encodes/decodes ONE Fixed/EnumRef/StructRef "value" at cursor pos -- shared by plain fields,
	// RepeatedField loop bodies, and UniformPackedArrayField element helpers, so there's exactly one
	// place that knows how to move a scalar/enum/struct value across the wire.
	private static string EncodeValue(FieldBase kind, string valueExpr) => kind switch
	{
		FixedField ff when ff.IsFloat => EncodeFloatBytes(valueExpr, ff.CppType, ff.Size),
		FixedField ff => EncodeScalar(ff.IsBool ? $"({valueExpr} ? 1u : 0u)" : valueExpr, ff.Size),
		EnumRefField erf => EncodeScalar($"(uint32_t)({valueExpr})", erf.Enum.Size),
		StructRefField srf => $"    {{\n        size_t newPos = {StructTypeName(srf.Struct)}Encode({valueExpr}, dest, pos, dest_size);\n        if (newPos == 0) return 0;\n        pos = newPos;\n    }}\n",
		_ => throw new InvalidOperationException(),
	};

	private static string DecodeValueInto(FieldBase kind, string targetExpr) => kind switch
	{
		FixedField ff when ff.IsBool => $"    if (len < pos + 1) return false;\n    {targetExpr} = data[pos] != 0;\n    pos += 1;\n",
		FixedField ff when ff.IsFloat => DecodeFloatInto(targetExpr, ff.Size),
		FixedField ff => DecodeScalarInto(targetExpr, ff.CppType, ff.Size),
		EnumRefField erf => DecodeScalarInto(targetExpr, EnumTypeName(erf.Enum), erf.Enum.Size),
		StructRefField srf => $"    if (!{StructTypeName(srf.Struct)}Decode(data, len, pos, {targetExpr})) return false;\n",
		_ => throw new InvalidOperationException(),
	};

	// --- Field-level encode/decode (Message/Class/Struct payload, cursor-based) ----------------------

	private static string EncodeField(FieldBase f)
	{
		switch (f)
		{
			case FixedField or EnumRefField or StructRefField:
				return EncodeValue(f, $"payload.{f.Name}");
			case RepeatedField rf:
			{
				var sb = new StringBuilder();
				sb.Append($"    if (pos + {ElementSize(rf.Element)} * {rf.Count} > dest_size) return 0;\n");
				sb.Append($"    for (size_t i = 0; i < {rf.Count}; i++) {{\n");
				sb.Append(Indent(EncodeValue(rf.Element, $"payload.{rf.Name}[i]"), "    "));
				sb.Append("    }\n");
				return sb.ToString();
			}
			case StringField sf:
				return
					$"    {{\n" +
					$"        size_t {sf.Name}Len = strlen(payload.{sf.Name});\n" +
					$"        if (pos + {sf.Name}Len + 1 > dest_size) return 0;\n" +
					$"        memcpy(dest + pos, payload.{sf.Name}, {sf.Name}Len); pos += {sf.Name}Len;\n" +
					$"        dest[pos++] = 0;\n" +
					"    }\n";
			case UniformPackedArrayField af:
				return
					$"    if (pos + 2 + payload.{af.Name}Count * {ElementSize(af.Element)} > dest_size) return 0;\n" +
					$"    dest[pos++] = (uint8_t)((uint16_t)payload.{af.Name}Count >> 0); dest[pos++] = (uint8_t)((uint16_t)payload.{af.Name}Count >> 8);\n" +
					$"    if (payload.{af.Name}Count > 0) {{ memcpy(dest + pos, payload.{af.Name}Data, payload.{af.Name}Count * {ElementSize(af.Element)}); pos += payload.{af.Name}Count * {ElementSize(af.Element)}; }}\n";
			case UniformVariableArrayField vf:
				return
					$"    if (pos + 2 + payload.{vf.Name}DataSize > dest_size) return 0;\n" +
					$"    dest[pos++] = (uint8_t)((uint16_t)payload.{vf.Name}Count >> 0); dest[pos++] = (uint8_t)((uint16_t)payload.{vf.Name}Count >> 8);\n" +
					$"    if (payload.{vf.Name}DataSize > 0) {{ memcpy(dest + pos, payload.{vf.Name}Data, payload.{vf.Name}DataSize); pos += payload.{vf.Name}DataSize; }}\n";
			case PolymorphicArrayField paf:
				// Elements are already pre-serialized (classId + class fields each), see
				// Append<Owner><Field><Class>Element() helpers. Must be the last field (see
				// SchemaValidation) -- decode below intentionally jumps straight to the frame end.
				return
					$"    if (pos + 2 + payload.{paf.Name}DataSize > dest_size) return 0;\n" +
					$"    dest[pos++] = (uint8_t)((uint16_t)payload.{paf.Name}Count >> 0); dest[pos++] = (uint8_t)((uint16_t)payload.{paf.Name}Count >> 8);\n" +
					$"    if (payload.{paf.Name}DataSize > 0) {{ memcpy(dest + pos, payload.{paf.Name}Data, payload.{paf.Name}DataSize); pos += payload.{paf.Name}DataSize; }}\n";
			case PolymorphicField pf:
				// Like PolymorphicArrayField, but no count prefix -- exactly one pre-serialized element.
				return
					$"    if (pos + payload.{pf.Name}DataSize > dest_size) return 0;\n" +
					$"    if (payload.{pf.Name}DataSize > 0) {{ memcpy(dest + pos, payload.{pf.Name}Data, payload.{pf.Name}DataSize); pos += payload.{pf.Name}DataSize; }}\n";
			default:
				throw new InvalidOperationException();
		}
	}

	private static string DecodeField(FieldBase f)
	{
		switch (f)
		{
			case FixedField or EnumRefField or StructRefField:
				return DecodeValueInto(f, $"out.{f.Name}");
			case RepeatedField rf:
			{
				var sb = new StringBuilder();
				sb.Append($"    if (len < pos + {ElementSize(rf.Element)} * {rf.Count}) return false;\n");
				sb.Append($"    for (size_t i = 0; i < {rf.Count}; i++) {{\n");
				sb.Append(Indent(DecodeValueInto(rf.Element, $"out.{rf.Name}[i]"), "    "));
				sb.Append("    }\n");
				return sb.ToString();
			}
			case StringField sf:
				return
					"    {\n" +
					$"        size_t p_ = pos;\n" +
					$"        while (p_ < len && data[p_] != 0) p_++;\n" +
					$"        if (p_ >= len) return false;\n" +
					$"        out.{sf.Name} = (const char*)(data + pos);\n" +
					$"        pos = p_ + 1;\n" +
					"    }\n";
			case UniformPackedArrayField af:
				// Overflow guard: count comes raw from the peer, count*ELEMENT_SIZE could otherwise wrap
				// around as size_t and bypass the length check below.
				return
					$"    if (len < pos + 2) return false;\n" +
					"    {\n" +
					$"        uint16_t {af.Name}Count_ = (uint16_t)((uint32_t)data[pos] | ((uint32_t)data[pos + 1] << 8));\n" +
					"        pos += 2;\n" +
					$"        if ((uint64_t){af.Name}Count_ * {ElementSize(af.Element)} > (uint64_t)(len - pos)) return false;\n" +
					$"        out.{af.Name}Data = data + pos;\n" +
					$"        out.{af.Name}Count = {af.Name}Count_;\n" +
					$"        pos += (size_t){af.Name}Count_ * {ElementSize(af.Element)};\n" +
					"    }\n";
			case UniformVariableArrayField vf:
				// Eager but zero-copy: walks each null-terminated string to find the array's total byte
				// length (needed to know where subsequent fields, if any, continue) without materializing
				// anything -- self-terminating, so (unlike PolymorphicArrayField) this field is allowed
				// anywhere in a message, not just trailing.
				return
					$"    if (len < pos + 2) return false;\n" +
					"    {\n" +
					$"        uint16_t {vf.Name}Count_ = (uint16_t)((uint32_t)data[pos] | ((uint32_t)data[pos + 1] << 8));\n" +
					"        pos += 2;\n" +
					$"        size_t {vf.Name}Start_ = pos;\n" +
					$"        for (uint16_t i = 0; i < {vf.Name}Count_; i++) {{\n" +
					$"            size_t p_ = pos;\n" +
					$"            while (p_ < len && data[p_] != 0) p_++;\n" +
					$"            if (p_ >= len) return false;\n" +
					$"            pos = p_ + 1;\n" +
					"        }\n" +
					$"        out.{vf.Name}Data = data + {vf.Name}Start_;\n" +
					$"        out.{vf.Name}Count = {vf.Name}Count_;\n" +
					$"        out.{vf.Name}DataSize = pos - {vf.Name}Start_;\n" +
					"    }\n";
			case PolymorphicArrayField paf:
				return
					$"    if (len < pos + 2) return false;\n" +
					"    {\n" +
					$"        uint16_t {paf.Name}Count_ = (uint16_t)((uint32_t)data[pos] | ((uint32_t)data[pos + 1] << 8));\n" +
					"        pos += 2;\n" +
					$"        out.{paf.Name}Count = {paf.Name}Count_;\n" +
					$"        out.{paf.Name}Data = data + pos;\n" +
					$"        out.{paf.Name}DataSize = len - pos;\n" +
					"        pos = len;\n" +
					"    }\n";
			case PolymorphicField pf:
				return
					$"    out.{pf.Name}Data = data + pos;\n" +
					$"    out.{pf.Name}DataSize = len - pos;\n" +
					"    pos = len;\n";
			default:
				throw new InvalidOperationException();
		}
	}

	// Best-effort minimum buffer size for Encode() given the CURRENTLY set string/array lengths in
	// payload -- not a compile-time constant (depends on the actual call), built as statements (not a
	// single expression) since string/array lengths need a small loop-free sum in the simple cases but a
	// caller-supplied DataSize in the pre-serialized (polymorphic) cases.
	private static string EncodedSizeStatement(FieldBase f) => f switch
	{
		FixedField ff => $"    total += {ff.Size};\n",
		EnumRefField erf => $"    total += {erf.Enum.Size};\n",
		StructRefField srf => $"    total += {StructTypeName(srf.Struct)}_SIZE;\n",
		RepeatedField rf => $"    total += {ElementSize(rf.Element) * rf.Count};\n",
		StringField sf => $"    total += strlen(payload.{sf.Name}) + 1;\n",
		UniformPackedArrayField af => $"    total += 2 + payload.{af.Name}Count * {ElementSize(af.Element)};\n",
		UniformVariableArrayField vf => $"    total += 2 + payload.{vf.Name}DataSize;\n",
		PolymorphicArrayField paf => $"    total += 2 + payload.{paf.Name}DataSize;\n",
		PolymorphicField pf => $"    total += payload.{pf.Name}DataSize;\n",
		_ => throw new InvalidOperationException(),
	};

	// --- Enum ---------------------------------------------------------------------------------------

	private static string GenerateEnum(EnumDef e)
	{
		var sb = new StringBuilder();
		if (e.Description is not null) sb.Append($"// {e.Description}\n");
		sb.Append($"enum class {e.Name} : {EnumUnderlyingType(e.Size)} {{ ");
		sb.Append(string.Join(", ", e.Values.Select(v => $"{v.Name} = {v.Value}")));
		sb.Append(" };\n\n");
		return sb.ToString();
	}

	// --- Struct ---------------------------------------------------------------------------------------

	// Pure fixed-field data, no header/id (never sent standalone). Encode/Decode take "pos" as an
	// in/out parameter (not always starting at 0/4 like a Message) since a struct can be embedded at any
	// cursor position within a Message/Class/another struct.
	private static string GenerateStruct(StructDef s)
	{
		var sb = new StringBuilder();
		if (s.Description is not null) sb.Append($"// {s.Description}\n");
		sb.Append($"struct {s.Name} {{\n");
		foreach (var f in s.Fields) sb.Append(FieldDecl(f));
		sb.Append("};\n\n");

		var totalSize = s.Fields.Sum(FixedSizeOf);
		sb.Append($"constexpr size_t {s.Name}_SIZE = {totalSize};\n\n");

		sb.Append($"inline size_t {s.Name}Encode(const {s.Name}& payload, uint8_t* dest, size_t pos, size_t dest_size) {{\n");
		foreach (var f in s.Fields) sb.Append(EncodeField(f));
		sb.Append("    return pos;\n}\n\n");

		sb.Append($"inline bool {s.Name}Decode(const uint8_t* data, size_t len, size_t& pos, {s.Name}& out) {{\n");
		foreach (var f in s.Fields) sb.Append(DecodeField(f));
		sb.Append("    return true;\n}\n\n");

		return sb.ToString();
	}

	// --- Class ----------------------------------------------------------------------------------------

	// Like a Message (Payload struct + Encode/Decode), but no NAMESPACE_ID/TYPE_ID header -- never sent
	// standalone, only ever an element of a PolymorphicArrayField/PolymorphicField. CLASS_ID is the
	// globally unique 2-byte discriminator.
	private static string GenerateClass(ClassDef c)
	{
		var sb = new StringBuilder();
		if (c.Description is not null) sb.Append($"// {c.Description}\n");
		sb.Append($"namespace {c.Name} {{\n");
		sb.Append($"constexpr uint16_t CLASS_ID = {c.Id};\n\n");

		// A class may itself carry (as its last field) a PolymorphicField -- needs the same Append*/
		// Decode*Elements helpers as a message's trailing polymorphic field.
		foreach (var pf in c.Fields.OfType<PolymorphicField>()) sb.Append(GenerateClassTaggedHelpers(c.Name, pf.Name, pf.Variants));

		sb.Append("struct Payload {\n");
		foreach (var f in c.Fields) sb.Append(FieldDecl(f, c.Name));
		sb.Append("};\n\n");

		sb.Append("inline size_t Encode(const Payload& payload, uint8_t* dest, size_t pos, size_t dest_size) {\n");
		foreach (var f in c.Fields) sb.Append(EncodeField(f));
		sb.Append("    return pos;\n}\n\n");

		sb.Append("inline bool Decode(const uint8_t* data, size_t len, size_t& pos, Payload& out) {\n");
		foreach (var f in c.Fields) sb.Append(DecodeField(f));
		sb.Append("    return true;\n}\n\n");

		sb.Append($"}} // namespace {c.Name}\n\n");
		return sb.ToString();
	}

	// Per element-kind helper for a UniformPackedArrayField: an ELEMENT_SIZE constant plus explicit
	// Encode/DecodeAt functions (bitshift-based like everything else -- never a raw reinterpret_cast of
	// the packed element type, so this works regardless of host struct padding/alignment/endianness).
	private static string GenerateUniformPackedArrayHelpers(string ownerName, UniformPackedArrayField af)
	{
		var baseName = $"{ownerName}{Capitalize(af.Name)}";
		var elementType = ElementCppType(af.Element);
		var elementSize = ElementSize(af.Element);
		var sb = new StringBuilder();
		sb.Append($"constexpr size_t {baseName}_ELEMENT_SIZE = {elementSize};\n\n");
		sb.Append($"inline size_t Encode{baseName}Element(const {elementType}& item, uint8_t* dest, size_t dest_size) {{\n");
		sb.Append("    size_t pos = 0;\n");
		sb.Append(EncodeValue(af.Element, "item"));
		sb.Append("    return pos;\n}\n\n");
		sb.Append($"inline bool Decode{baseName}ElementAt(const uint8_t* base_, size_t index, {elementType}& out) {{\n");
		sb.Append($"    const uint8_t* data = base_ + index * {baseName}_ELEMENT_SIZE;\n");
		sb.Append($"    size_t len = {baseName}_ELEMENT_SIZE;\n");
		sb.Append("    size_t pos = 0;\n");
		sb.Append(DecodeValueInto(af.Element, "out"));
		sb.Append("    return true;\n}\n\n");
		return sb.ToString();
	}

	private static string GenerateUniformVariableArrayHelpers(string ownerName, UniformVariableArrayField vf)
	{
		var baseName = $"{ownerName}{Capitalize(vf.Name)}";
		var sb = new StringBuilder();
		sb.Append($"// Laeuft die null-terminierten {vf.Name}-Strings von 'data' (Laenge 'dataSize') sequentiell ab und ruft\n");
		sb.Append("// 'visitor' je Element mit dem dekodierten (weiterhin null-terminierten) C-String auf.\n");
		sb.Append("template <typename Visitor>\n");
		sb.Append($"inline bool Decode{baseName}Elements(const uint8_t* data, size_t dataSize, size_t count, Visitor&& visitor) {{\n");
		sb.Append("    size_t pos = 0;\n");
		sb.Append("    for (size_t i = 0; i < count; i++) {\n");
		sb.Append("        size_t p_ = pos;\n");
		sb.Append("        while (p_ < dataSize && data[p_] != 0) p_++;\n");
		sb.Append("        if (p_ >= dataSize) return false;\n");
		sb.Append("        visitor((const char*)(data + pos));\n");
		sb.Append("        pos = p_ + 1;\n");
		sb.Append("    }\n");
		sb.Append("    return true;\n");
		sb.Append("}\n\n");
		return sb.ToString();
	}

	// For a PolymorphicArrayField/PolymorphicField: per allowed variant an "Append<Owner><Field><Class>
	// Element" helper (sender side: appends [classId][class fields] to a scratch buffer), plus ONE
	// "Decode<Owner><Field>Elements" visitor (receiver side: walks the elements sequentially -- called
	// with count=1 for a PolymorphicField). A visitor callback (template, not a materialized result
	// list) avoids heap allocation and an extra variant/union type.
	private static string GenerateClassTaggedHelpers(string ownerName, string fieldName, IReadOnlyList<ClassDef> variants)
	{
		var baseName = $"{ownerName}{Capitalize(fieldName)}";
		var sb = new StringBuilder();

		foreach (var v in variants)
		{
			var qv = ClassQualifiedName(v);
			sb.Append($"inline size_t Append{baseName}{v.Name}Element(const {qv}::Payload& item, uint8_t* dest, size_t pos, size_t dest_size) {{\n");
			sb.Append("    if (pos + 2 > dest_size) return 0;\n");
			sb.Append($"    dest[pos++] = (uint8_t)({qv}::CLASS_ID & 0xFF);\n");
			sb.Append($"    dest[pos++] = (uint8_t)({qv}::CLASS_ID >> 8);\n");
			sb.Append($"    size_t newPos = {qv}::Encode(item, dest, pos, dest_size);\n");
			sb.Append("    return newPos;\n");
			sb.Append("}\n\n");
		}

		sb.Append($"// Laeuft {fieldName}-Element(e) von 'data' (Laenge 'dataSize') sequentiell ab und ruft 'visitor'\n");
		sb.Append("// je nach vorangestellter classId mit dem passenden dekodierten Klassen-Payload auf. Gibt false\n");
		sb.Append("// bei unbekannter classId oder einem zu kurzen/inkonsistenten Element zurueck.\n");
		sb.Append("template <typename Visitor>\n");
		sb.Append($"inline bool Decode{baseName}Elements(const uint8_t* data, size_t dataSize, size_t count, Visitor&& visitor) {{\n");
		sb.Append("    size_t pos = 0;\n");
		sb.Append("    for (size_t i = 0; i < count; i++) {\n");
		sb.Append("        if (pos + 2 > dataSize) return false;\n");
		sb.Append("        uint16_t classId = (uint16_t)((uint32_t)data[pos] | ((uint32_t)data[pos + 1] << 8));\n");
		sb.Append("        pos += 2;\n");
		sb.Append("        switch (classId) {\n");
		foreach (var v in variants)
		{
			var qv = ClassQualifiedName(v);
			sb.Append($"        case {qv}::CLASS_ID: {{\n");
			sb.Append($"            {qv}::Payload item{{}};\n");
			sb.Append($"            if (!{qv}::Decode(data, dataSize, pos, item)) return false;\n");
			sb.Append("            visitor(item);\n");
			sb.Append("            break;\n");
			sb.Append("        }\n");
		}
		sb.Append("        default: return false;\n");
		sb.Append("        }\n");
		sb.Append("    }\n");
		sb.Append("    return true;\n");
		sb.Append("}\n\n");

		return sb.ToString();
	}

	// --- Message ----------------------------------------------------------------------------------------

	private static string GenerateMessage(Message msg)
	{
		var sb = new StringBuilder();
		if (msg.Description is not null) sb.Append($"// {msg.Description}\n");
		sb.Append($"namespace {msg.Name} {{\n");
		sb.Append($"constexpr uint16_t TYPE_ID = {msg.Id};\n");
		sb.Append($"constexpr MessageKind KIND = {KindEnumCpp(msg.Kind)};\n\n");

		foreach (var af in msg.Fields.OfType<UniformPackedArrayField>()) sb.Append(GenerateUniformPackedArrayHelpers(msg.Name, af));
		foreach (var vf in msg.Fields.OfType<UniformVariableArrayField>()) sb.Append(GenerateUniformVariableArrayHelpers(msg.Name, vf));
		foreach (var paf in msg.Fields.OfType<PolymorphicArrayField>()) sb.Append(GenerateClassTaggedHelpers(msg.Name, paf.Name, paf.Variants));
		foreach (var pf in msg.Fields.OfType<PolymorphicField>()) sb.Append(GenerateClassTaggedHelpers(msg.Name, pf.Name, pf.Variants));

		sb.Append("struct Payload {\n");
		foreach (var f in msg.Fields) sb.Append(FieldDecl(f, msg.Name));
		sb.Append("};\n\n");

		sb.Append("// Minimal benoetigte Puffergroesse fuer Encode() bei den AKTUELL in payload gesetzten\n");
		sb.Append("// String-/Array-Laengen (haengt vom jeweiligen Aufruf ab, keine Compile-Zeit-Konstante).\n");
		sb.Append("inline size_t EncodedSize(const Payload& payload) {\n");
		sb.Append("    size_t total = 4;\n");
		foreach (var f in msg.Fields) sb.Append(EncodedSizeStatement(f));
		sb.Append("    return total;\n");
		sb.Append("}\n\n");

		sb.Append("// Schreibt Kopf+Payload nach 'dest' (s. EncodedSize() fuer die benoetigte Mindestgroesse).\n");
		sb.Append("// Gibt die geschriebene Gesamtlaenge zurueck, oder 0 bei zu kleinem Zielpuffer.\n");
		sb.Append("inline size_t Encode(const Payload& payload, uint8_t* dest, size_t dest_size) {\n");
		sb.Append("    size_t pos = 0;\n");
		sb.Append("    if (dest_size < 4) return 0;\n");
		sb.Append("    dest[pos++] = (uint8_t)(NAMESPACE_ID & 0xFF); dest[pos++] = (uint8_t)(NAMESPACE_ID >> 8);\n");
		sb.Append("    dest[pos++] = (uint8_t)(TYPE_ID & 0xFF); dest[pos++] = (uint8_t)(TYPE_ID >> 8);\n");
		foreach (var f in msg.Fields) sb.Append(EncodeField(f));
		sb.Append("    return pos;\n}\n\n");

		sb.Append("// 'data' zeigt auf den KOMPLETTEN Frame inkl. 4-Byte-Kopf (namespaceId/messageTypeId werden\n");
		sb.Append("// hier nicht erneut geprueft -- Aufgabe des aufrufenden Dispatchers). false bei zu kurzem\n");
		sb.Append("// oder inkonsistentem Frame (z.B. Laengenpraefix zeigt ueber das Frame-Ende hinaus).\n");
		sb.Append("inline bool Decode(const uint8_t* data, size_t len, Payload& out) {\n");
		sb.Append("    size_t pos = 4;\n");
		foreach (var f in msg.Fields) sb.Append(DecodeField(f));
		sb.Append("    return true;\n}\n\n");

		sb.Append($"}} // namespace {msg.Name}\n\n");
		return sb.ToString();
	}

	private static string KindEnumCpp(MessageKind kind) => kind switch
	{
		MessageKind.Event => "MessageKind::Event",
		MessageKind.Request => "MessageKind::Request",
		MessageKind.Response => "MessageKind::Response",
		_ => throw new InvalidOperationException(),
	};
}
