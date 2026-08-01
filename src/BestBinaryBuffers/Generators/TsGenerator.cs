using System.Text;
using BestBinaryBuffers.Model;

namespace BestBinaryBuffers.Generators;

/// <summary>Generates a single TypeScript module from the resolved schema model, wire-compatible with
/// <see cref="CppGenerator"/> (little-endian, null-terminated strings, ushort count prefixes). Unlike
/// the C++ side, decode here eagerly materializes real JS values/arrays (garbage-collected runtime, no
/// ESP32-style "avoid allocation" constraint) -- e.g. a UniformVariableArrayField becomes a plain
/// <c>string[]</c>, a PolymorphicArrayField a fully decoded discriminated-union array, not a lazy
/// raw-bytes handle.</summary>
public static class TsGenerator
{
	public static string Generate(IReadOnlyList<NamespaceDef> namespaces)
	{
		var sb = new StringBuilder();
		sb.Append(GeneratedFileHeader.Text);
		sb.Append("\n\nexport enum MessageKind { Event = 0, Request = 1, Response = 2 }\n\n");
		sb.Append(DecodeCStringHelper);

		// Same four-phase rationale as CppGenerator. NULL namespace (Name=="") gets no
		// "export namespace {}" wrapper -- contents live at module level (indent "").
		foreach (var ns in namespaces)
		{
			var open = ns.Name.Length > 0;
			var indent = open ? "\t" : "";
			if (open) sb.Append($"export namespace {ns.Name} {{\n");
			sb.Append($"{indent}export const NAMESPACE_ID = {ns.Id};\n\n");
			foreach (var e in ns.Enums) sb.Append(GenerateEnum(e, indent));
			foreach (var s in ns.Structs) sb.Append(GenerateStruct(s, indent));
			if (open) sb.Append("}\n\n");
		}
		foreach (var ns in namespaces)
		{
			if (ns.Classes.Count == 0) continue;
			var open = ns.Name.Length > 0;
			var indent = open ? "\t" : "";
			if (open) sb.Append($"export namespace {ns.Name} {{\n");
			foreach (var c in ns.Classes) sb.Append(GenerateClass(c, indent));
			if (open) sb.Append("}\n\n");
		}
		foreach (var ns in namespaces)
		{
			var open = ns.Name.Length > 0;
			var indent = open ? "\t" : "";
			if (open) sb.Append($"export namespace {ns.Name} {{\n");
			foreach (var msg in ns.Messages) sb.Append(GenerateMessage(msg, indent));
			if (open) sb.Append("}\n\n");
		}

		return sb.ToString();
	}

	// --- Naming --------------------------------------------------------------------------------------

	// Unlike C++, TS has no implicit "visible from here" across namespace boundaries for a bare name, so
	// cross-namespace references are ALWAYS "<namespace>.<Name>". NULL-namespace types (Namespace=="")
	// live at module scope (no namespace prefix) -- lexical scoping in TS still makes them visible
	// unqualified from any nested "export namespace".
	private static string Qualify(string ns, string name) => ns.Length == 0 ? name : $"{ns}.{name}";
	private static string StructTypeName(StructDef s) => Qualify(s.Namespace, s.Name);
	private static string EnumTypeName(EnumDef e) => Qualify(e.Namespace, e.Name);
	private static string ClassQualifiedName(ClassDef c) => Qualify(c.Namespace, c.Name);
	private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

	private static string ElementTsType(FieldBase f) => f switch
	{
		FixedField ff => ff.TsType,
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

	private static int FixedSizeOf(FieldBase f) => f switch
	{
		FixedField ff => ff.Size,
		EnumRefField erf => erf.Enum.Size,
		StructRefField srf => srf.Struct.Fields.Sum(FixedSizeOf),
		RepeatedField rf => ElementSize(rf.Element) * rf.Count,
		_ => throw new InvalidOperationException("Only defined for fixed field types."),
	};

	// Discriminated union of a polymorphic field's allowed classes, each element tagged with "classId".
	private static string PolymorphicUnionTypeName(IReadOnlyList<ClassDef> variants) =>
		"(" + string.Join(" | ", variants.Select(c => $"({{ classId: typeof {ClassQualifiedName(c)}.CLASS_ID }} & {ClassQualifiedName(c)}.Payload)")) + ")";

	// --- Field declarations (interface members) -------------------------------------------------------

	private static string FieldDecl(FieldBase f) => f switch
	{
		FixedField ff => $"\t\t\t{ff.Name}: {ff.TsType};" + Comment(ff.Description) + "\n",
		EnumRefField erf => $"\t\t\t{erf.Name}: {EnumTypeName(erf.Enum)};" + Comment(erf.Description) + "\n",
		StructRefField srf => $"\t\t\t{srf.Name}: {StructTypeName(srf.Struct)};" + Comment(srf.Description) + "\n",
		RepeatedField rf => $"\t\t\t{rf.Name}: {ElementTsType(rf.Element)}[];" + Comment(rf.Description) + "\n",
		StringField sf => $"\t\t\t{sf.Name}: string;" + Comment(sf.Description) + "\n",
		UniformPackedArrayField af => $"\t\t\t{af.Name}: {ElementTsType(af.Element)}[];" + Comment(af.Description) + "\n",
		UniformVariableArrayField vf => $"\t\t\t{vf.Name}: string[];" + Comment(vf.Description) + "\n",
		PolymorphicArrayField paf => $"\t\t\t{paf.Name}: {PolymorphicUnionTypeName(paf.Variants)}[];" + Comment(paf.Description) + "\n",
		PolymorphicField pf => $"\t\t\t{pf.Name}: {PolymorphicUnionTypeName(pf.Variants)};" + Comment(pf.Description) + "\n",
		_ => throw new InvalidOperationException(),
	};

	private static string Comment(string? description) => description is not null ? $" // {description}" : "";

	// --- Direct (DataView + cursor, single preallocated buffer) encode/decode for Fixed/EnumRef/
	// StructRef/RepeatedField -- used by Struct encode/decode and by the per-element helpers of a
	// UniformPackedArrayField (each element still has a compile-time-known fixed size). -----------------

	private static string SetterName(int size, bool isSigned, bool isFloat)
	{
		if (isFloat) return "setFloat32";
		return (isSigned ? "setInt" : "setUint") + size switch { 1 => "8", 2 => "16", 4 => "32", _ => throw new InvalidOperationException() };
	}

	private static string GetterName(int size, bool isSigned, bool isFloat)
	{
		if (isFloat) return "getFloat32";
		return (isSigned ? "getInt" : "getUint") + size switch { 1 => "8", 2 => "16", 4 => "32", _ => throw new InvalidOperationException() };
	}

	// 64-bit integers: DataView has no number-based 64-bit accessor (only getBigInt64/getBigUint64), so
	// they're manually split into two 32-bit halves (LE). Exact only for non-negative values <= 2^53 --
	// plenty for this ecosystem's current needs (e.g. Unix timestamps), not a general 64-bit story.
	private static string EncodeValueDirect(FieldBase kind, string valueExpr)
	{
		switch (kind)
		{
			case FixedField { Size: 8 }:
				return $"\t\t\tview.setUint32(pos, ({valueExpr}) >>> 0, true); view.setUint32(pos + 4, Math.floor(({valueExpr}) / 4294967296) >>> 0, true); pos += 8;\n";
			case FixedField ff:
			{
				var expr = ff.IsBool ? $"({valueExpr} ? 1 : 0)" : valueExpr;
				var setter = SetterName(ff.Size, ff.IsSigned, ff.IsFloat);
				var call = ff.Size == 1 ? $"view.{setter}(pos, {expr});" : $"view.{setter}(pos, {expr}, true);";
				return $"\t\t\t{call} pos += {ff.Size};\n";
			}
			case EnumRefField erf:
			{
				var setter = SetterName(erf.Enum.Size, false, false);
				var call = erf.Enum.Size == 1 ? $"view.{setter}(pos, {valueExpr});" : $"view.{setter}(pos, {valueExpr}, true);";
				return $"\t\t\t{call} pos += {erf.Enum.Size};\n";
			}
			case StructRefField srf:
				return $"\t\t\tpos = {Qualify(srf.Struct.Namespace, $"encode{srf.Struct.Name}")}Into({valueExpr}, view, pos);\n";
			default:
				throw new InvalidOperationException();
		}
	}

	private static string DecodeValueDirectExpr(FieldBase kind) => kind switch
	{
		FixedField { Size: 8 } => "view.getUint32(pos + 4, true) * 4294967296 + view.getUint32(pos, true)",
		FixedField ff when ff.IsBool => $"view.{GetterName(1, false, false)}(pos) !== 0",
		FixedField ff => ff.Size == 1 ? $"view.{GetterName(ff.Size, ff.IsSigned, ff.IsFloat)}(pos)" : $"view.{GetterName(ff.Size, ff.IsSigned, ff.IsFloat)}(pos, true)",
		EnumRefField erf => (erf.Enum.Size == 1 ? $"view.{GetterName(1, false, false)}(pos)" : $"view.{GetterName(erf.Enum.Size, false, false)}(pos, true)") + $" as {EnumTypeName(erf.Enum)}",
		_ => throw new InvalidOperationException(),
	};

	// Only Fixed(non-struct)/EnumRef go through DecodeValueDirectExpr (a single expression); StructRef
	// needs its own statement (calls decodeX, which returns {value,nextPos}) -- kept separate so callers
	// (struct fields, array-element loops) can each embed it with the right local variable naming.
	private static string DecodeValueDirectStatements(FieldBase kind, string resultVar)
	{
		if (kind is StructRefField srf)
		{
			return $"\t\t\tconst {resultVar}Decoded = {Qualify(srf.Struct.Namespace, $"decode{srf.Struct.Name}")}(view, pos);\n" +
				$"\t\t\tconst {resultVar} = {resultVar}Decoded.value; pos = {resultVar}Decoded.nextPos;\n";
		}
		var expr = DecodeValueDirectExpr(kind);
		var size = kind switch { FixedField ff => ff.Size, EnumRefField erf => erf.Enum.Size, _ => throw new InvalidOperationException() };
		return $"\t\t\tconst {resultVar} = {expr};\n\t\t\tpos += {size};\n";
	}

	// --- Chunk-based encode/decode (variable length: Message/Class top level) -------------------------

	private static string EncodeValueChunk(FieldBase kind, string valueExpr)
	{
		if (kind is StructRefField srf)
		{
			return $"\t\t\tchunks.push({Qualify(srf.Struct.Namespace, $"encode{srf.Struct.Name}")}({valueExpr}));\n";
		}
		if (kind is FixedField { Size: 8 })
		{
			return $"\t\t\t{{ const b = new Uint8Array(8); const v = new DataView(b.buffer); " +
				$"v.setUint32(0, ({valueExpr}) >>> 0, true); v.setUint32(4, Math.floor(({valueExpr}) / 4294967296) >>> 0, true); chunks.push(b); }}\n";
		}
		var (size, setter, expr) = kind switch
		{
			FixedField ff => (ff.Size, SetterName(ff.Size, ff.IsSigned, ff.IsFloat), ff.IsBool ? $"({valueExpr} ? 1 : 0)" : valueExpr),
			EnumRefField erf => (erf.Enum.Size, SetterName(erf.Enum.Size, false, false), valueExpr),
			_ => throw new InvalidOperationException(),
		};
		var call = size == 1 ? $"v.{setter}(0, {expr});" : $"v.{setter}(0, {expr}, true);";
		return $"\t\t\t{{ const b = new Uint8Array({size}); const v = new DataView(b.buffer); {call} chunks.push(b); }}\n";
	}

	private static string EncodeStringChunk(string valueExpr) =>
		$"\t\t\t{{ const bytes = new TextEncoder().encode({valueExpr}); const withNul = new Uint8Array(bytes.length + 1); " +
		"withNul.set(bytes, 0); withNul[bytes.length] = 0; chunks.push(withNul); }\n";

	// Reads a null-terminated string starting at 'pos' from 'view', returns {value, nextPos}. Emitted
	// once per generated file (top-level helper) rather than inlined at every call site.
	private const string DecodeCStringHelper = """
		function decodeCString(view: DataView, offset: number): { value: string; nextPos: number } {
			let end = offset;
			while (end < view.byteLength && view.getUint8(end) !== 0) end++;
			if (end >= view.byteLength) throw new Error("frame too short (missing null terminator)");
			const value = new TextDecoder().decode(new Uint8Array(view.buffer, view.byteOffset + offset, end - offset));
			return { value, nextPos: end + 1 };
		}

		""";

	private static string DecodeStringStatements(string resultVar) =>
		$"\t\t\tconst {resultVar}Decoded = decodeCString(view, pos); const {resultVar} = {resultVar}Decoded.value; pos = {resultVar}Decoded.nextPos;\n";

	// --- Enum ----------------------------------------------------------------------------------------

	private static string GenerateEnum(EnumDef e, string indent)
	{
		var sb = new StringBuilder();
		if (e.Description is not null) sb.Append($"{indent}// {e.Description}\n");
		sb.Append($"{indent}export enum {e.Name} {{ ");
		sb.Append(string.Join(", ", e.Values.Select(v => $"{v.Name} = {v.Value}")));
		sb.Append(" }\n\n");
		return sb.ToString();
	}

	// --- Struct ----------------------------------------------------------------------------------------

	// "encode<Name>Into"/"decode<Name>" work with an explicit cursor (rather than a fresh, exactly-sized
	// DataView like an array element) because a struct can be embedded at any position within a Message/
	// Class/another struct.
	private static string GenerateStruct(StructDef s, string indent)
	{
		var sb = new StringBuilder();
		if (s.Description is not null) sb.Append($"{indent}// {s.Description}\n");
		sb.Append($"{indent}export interface {s.Name} {{\n");
		foreach (var f in s.Fields) sb.Append(FieldDecl(f));
		sb.Append($"{indent}}}\n");

		var totalSize = s.Fields.Sum(FixedSizeOf);
		sb.Append($"{indent}export const {s.Name}_SIZE = {totalSize};\n\n");

		sb.Append($"{indent}export function encode{s.Name}Into(value: {s.Name}, view: DataView, pos: number): number {{\n");
		foreach (var f in s.Fields) sb.Append(EncodeStructField(f, "value"));
		sb.Append($"{indent}\treturn pos;\n{indent}}}\n\n");

		sb.Append($"{indent}export function encode{s.Name}(value: {s.Name}): Uint8Array {{\n");
		sb.Append($"{indent}\tconst buffer = new ArrayBuffer({s.Name}_SIZE);\n{indent}\tencode{s.Name}Into(value, new DataView(buffer), 0);\n{indent}\treturn new Uint8Array(buffer);\n{indent}}}\n\n");

		sb.Append($"{indent}export function decode{s.Name}(view: DataView, offset: number): {{ value: {s.Name}; nextPos: number }} {{\n");
		sb.Append("\t\tlet pos = offset;\n");
		foreach (var f in s.Fields) sb.Append(DecodeStructField(f));
		sb.Append("\t\treturn { value: { " + string.Join(", ", s.Fields.Select(f => f.Name)) + " }, nextPos: pos };\n\t}\n\n");

		return sb.ToString();
	}

	// Struct/RepeatedField fields only ever contain Fixed/EnumRef/StructRef/RepeatedField -- direct
	// cursor style throughout (no chunking needed, everything here has a compile-time-known size).
	private static string EncodeStructField(FieldBase f, string varName)
	{
		if (f is RepeatedField rf)
		{
			var sb = new StringBuilder();
			sb.Append($"\t\t\tfor (let i = 0; i < {rf.Count}; i++) {{\n");
			sb.Append("\t\t" + EncodeValueDirect(rf.Element, $"{varName}.{rf.Name}[i]").TrimStart());
			sb.Append("\t\t\t}\n");
			return sb.ToString();
		}
		return EncodeValueDirect(f, $"{varName}.{f.Name}");
	}

	private static string DecodeStructField(FieldBase f)
	{
		if (f is RepeatedField rf)
		{
			var sb = new StringBuilder();
			sb.Append($"\t\t\tconst {rf.Name}: {ElementTsType(rf.Element)}[] = [];\n");
			sb.Append($"\t\t\tfor (let i = 0; i < {rf.Count}; i++) {{\n");
			sb.Append(Reindent(DecodeValueDirectStatements(rf.Element, "item"), "\t\t\t\t"));
			sb.Append($"\t\t\t\t{rf.Name}.push(item);\n");
			sb.Append("\t\t\t}\n");
			return sb.ToString();
		}
		return DecodeValueDirectStatements(f, f.Name);
	}

	private static string Reindent(string block, string prefix) =>
		string.Concat(block.Split('\n').Select(l => l.Trim().Length == 0 ? "" : prefix + l.TrimStart() + "\n"));

	// --- Class ------------------------------------------------------------------------------------------

	private static string GenerateClass(ClassDef c, string indent)
	{
		var sb = new StringBuilder();
		if (c.Description is not null) sb.Append($"{indent}// {c.Description}\n");
		sb.Append($"{indent}export namespace {c.Name} {{\n");
		sb.Append($"{indent}\texport const CLASS_ID = {c.Id};\n\n");

		sb.Append("\t\texport interface Payload {\n");
		foreach (var f in c.Fields) sb.Append(FieldDecl(f));
		sb.Append("\t\t}\n\n");

		sb.Append("\t\texport function encode(payload: Payload): Uint8Array {\n");
		sb.Append("\t\t\tconst chunks: Uint8Array[] = [];\n");
		foreach (var f in c.Fields) sb.Append(EncodeChunkField(f));
		sb.Append(ConcatChunks());
		sb.Append("\t\t}\n\n");

		sb.Append($"\t\texport function decodeAt(view: DataView, offset: number): {{ value: Payload; nextPos: number }} {{\n");
		sb.Append("\t\t\tlet pos = offset;\n");
		foreach (var f in c.Fields) sb.Append(DecodeChunkField(f, c.Name));
		sb.Append("\t\t\treturn { value: { " + string.Join(", ", c.Fields.Select(f => f.Name)) + " }, nextPos: pos };\n\t\t}\n");
		sb.Append($"{indent}}}\n\n");
		return sb.ToString();
	}

	private static string ConcatChunks() =>
		"\t\t\tlet total = 0;\n\t\t\tfor (const c of chunks) total += c.length;\n" +
		"\t\t\tconst out = new Uint8Array(total);\n\t\t\tlet o = 0;\n\t\t\tfor (const c of chunks) { out.set(c, o); o += c.length; }\n" +
		"\t\t\treturn out;\n";

	// --- Message ------------------------------------------------------------------------------------------

	private static string GenerateMessage(Message msg, string indent)
	{
		var sb = new StringBuilder();
		if (msg.Description is not null) sb.Append($"{indent}// {msg.Description}\n");
		sb.Append($"{indent}export namespace {msg.Name} {{\n");
		sb.Append($"{indent}\texport const TYPE_ID = {msg.Id};\n");
		sb.Append($"{indent}\texport const KIND = {KindEnumTs(msg.Kind)};\n\n");

		sb.Append("\t\texport interface Payload {\n");
		foreach (var f in msg.Fields) sb.Append(FieldDecl(f));
		sb.Append("\t\t}\n\n");

		sb.Append("\t\texport function encode(payload: Payload): Uint8Array {\n");
		sb.Append("\t\t\tconst chunks: Uint8Array[] = [];\n");
		sb.Append("\t\t\t{ const h = new Uint8Array(4); const hv = new DataView(h.buffer); hv.setUint16(0, NAMESPACE_ID, true); hv.setUint16(2, TYPE_ID, true); chunks.push(h); }\n");
		foreach (var f in msg.Fields) sb.Append(EncodeChunkField(f));
		sb.Append(ConcatChunks());
		sb.Append("\t\t}\n\n");

		sb.Append("\t\t// 'view' ist der KOMPLETTE Frame inkl. 4-Byte-Kopf, 'offset' zeigt auf dessen Anfang\n");
		sb.Append("\t\t// (namespaceId/messageTypeId werden hier nicht erneut geprueft -- Aufgabe des Dispatchers).\n");
		sb.Append("\t\t// Wirft, wenn der Frame kuerzer als angegeben/erwartet ist.\n");
		sb.Append("\t\texport function decode(view: DataView, offset: number): Payload {\n");
		sb.Append("\t\t\tlet pos = offset + 4;\n");
		foreach (var f in msg.Fields) sb.Append(DecodeChunkField(f, msg.Name));
		sb.Append("\t\t\treturn { " + string.Join(", ", msg.Fields.Select(f => f.Name)) + " };\n\t\t}\n");
		sb.Append($"{indent}}}\n\n");
		return sb.ToString();
	}

	private static string KindEnumTs(MessageKind kind) => kind switch
	{
		MessageKind.Event => "MessageKind.Event",
		MessageKind.Request => "MessageKind.Request",
		MessageKind.Response => "MessageKind.Response",
		_ => throw new InvalidOperationException(),
	};

	// --- Chunk field encode/decode (Class/Message top level, variable length) ---------------------------

	private static string EncodeChunkField(FieldBase f)
	{
		switch (f)
		{
			case FixedField or EnumRefField or StructRefField:
				return EncodeValueChunk(f, $"payload.{f.Name}");
			case RepeatedField rf:
			{
				var sb = new StringBuilder();
				sb.Append($"\t\t\tfor (let i = 0; i < {rf.Count}; i++) {{\n");
				sb.Append(Reindent(EncodeValueChunk(rf.Element, $"payload.{rf.Name}[i]"), "\t\t\t\t"));
				sb.Append("\t\t\t}\n");
				return sb.ToString();
			}
			case StringField sf:
				return EncodeStringChunk($"payload.{sf.Name}");
			case UniformPackedArrayField af:
				return
					"\t\t\t{\n" +
					$"\t\t\t\tconst count = new Uint8Array(2); new DataView(count.buffer).setUint16(0, payload.{af.Name}.length, true); chunks.push(count);\n" +
					$"\t\t\t\tfor (const item of payload.{af.Name}) {{\n" +
					Reindent(EncodeValueChunk(af.Element, "item"), "\t\t\t\t\t") +
					"\t\t\t\t}\n" +
					"\t\t\t}\n";
			case UniformVariableArrayField vf:
				return
					"\t\t\t{\n" +
					$"\t\t\t\tconst count = new Uint8Array(2); new DataView(count.buffer).setUint16(0, payload.{vf.Name}.length, true); chunks.push(count);\n" +
					$"\t\t\t\tfor (const s of payload.{vf.Name}) {{\n" +
					Reindent(EncodeStringChunk("s"), "\t\t\t\t\t") +
					"\t\t\t\t}\n" +
					"\t\t\t}\n";
			case PolymorphicArrayField paf:
				return
					"\t\t\t{\n" +
					$"\t\t\t\tconst count = new Uint8Array(2); new DataView(count.buffer).setUint16(0, payload.{paf.Name}.length, true); chunks.push(count);\n" +
					$"\t\t\t\tfor (const item of payload.{paf.Name}) {{\n" +
					Reindent(EncodeTaggedElement("item", paf.Variants), "\t\t\t\t\t") +
					"\t\t\t\t}\n" +
					"\t\t\t}\n";
			case PolymorphicField pf:
				return EncodeTaggedElement($"payload.{pf.Name}", pf.Variants);
			default:
				throw new InvalidOperationException();
		}
	}

	private static string EncodeTaggedElement(string itemExpr, IReadOnlyList<ClassDef> variants)
	{
		var sb = new StringBuilder();
		sb.Append($"\t\t\tconst tag = new Uint8Array(2); new DataView(tag.buffer).setUint16(0, {itemExpr}.classId, true); chunks.push(tag);\n");
		sb.Append($"\t\t\tswitch ({itemExpr}.classId) {{\n");
		foreach (var v in variants)
		{
			var qv = ClassQualifiedName(v);
			sb.Append($"\t\t\tcase {qv}.CLASS_ID: chunks.push({qv}.encode({itemExpr})); break;\n");
		}
		sb.Append($"\t\t\tdefault: throw new Error(\"unbekannte classId \" + ({itemExpr} as any).classId);\n");
		sb.Append("\t\t\t}\n");
		return sb.ToString();
	}

	private static string DecodeChunkField(FieldBase f, string msgName)
	{
		switch (f)
		{
			case FixedField or EnumRefField or StructRefField:
				return DecodeValueDirectStatements(f, f.Name);
			case RepeatedField rf:
			{
				var sb = new StringBuilder();
				sb.Append($"\t\t\tconst {rf.Name}: {ElementTsType(rf.Element)}[] = [];\n");
				sb.Append($"\t\t\tfor (let i = 0; i < {rf.Count}; i++) {{\n");
				sb.Append(Reindent(DecodeValueDirectStatements(rf.Element, "item"), "\t\t\t\t"));
				sb.Append($"\t\t\t\t{rf.Name}.push(item);\n");
				sb.Append("\t\t\t}\n");
				return sb.ToString();
			}
			case StringField sf:
				return DecodeStringStatements(sf.Name);
			case UniformPackedArrayField af:
			{
				var sb = new StringBuilder();
				sb.Append($"\t\t\tif (view.byteLength - pos < 2) throw new Error(\"{msgName}: frame too short\");\n");
				sb.Append($"\t\t\tconst {af.Name}Count = view.getUint16(pos, true); pos += 2;\n");
				sb.Append($"\t\t\tconst {af.Name}: {ElementTsType(af.Element)}[] = [];\n");
				sb.Append($"\t\t\tfor (let i = 0; i < {af.Name}Count; i++) {{\n");
				sb.Append(Reindent(DecodeValueDirectStatements(af.Element, "item"), "\t\t\t\t"));
				sb.Append($"\t\t\t\t{af.Name}.push(item);\n");
				sb.Append("\t\t\t}\n");
				return sb.ToString();
			}
			case UniformVariableArrayField vf:
			{
				var sb = new StringBuilder();
				sb.Append($"\t\t\tif (view.byteLength - pos < 2) throw new Error(\"{msgName}: frame too short\");\n");
				sb.Append($"\t\t\tconst {vf.Name}Count = view.getUint16(pos, true); pos += 2;\n");
				sb.Append($"\t\t\tconst {vf.Name}: string[] = [];\n");
				sb.Append($"\t\t\tfor (let i = 0; i < {vf.Name}Count; i++) {{\n");
				sb.Append(Reindent(DecodeStringStatements("item"), "\t\t\t\t"));
				sb.Append($"\t\t\t\t{vf.Name}.push(item);\n");
				sb.Append("\t\t\t}\n");
				return sb.ToString();
			}
			case PolymorphicArrayField paf:
				return DecodeTaggedArray(paf.Name, paf.Variants, msgName);
			case PolymorphicField pf:
				return DecodeTaggedSingle(pf.Name, pf.Variants, msgName);
			default:
				throw new InvalidOperationException();
		}
	}

	private static string DecodeTaggedArray(string fieldName, IReadOnlyList<ClassDef> variants, string msgName)
	{
		var unionType = PolymorphicUnionTypeName(variants);
		var sb = new StringBuilder();
		sb.Append($"\t\t\tif (view.byteLength - pos < 2) throw new Error(\"{msgName}: frame too short\");\n");
		sb.Append($"\t\t\tconst {fieldName}Count = view.getUint16(pos, true); pos += 2;\n");
		sb.Append($"\t\t\tconst {fieldName}: {unionType}[] = [];\n");
		sb.Append($"\t\t\tfor (let i = 0; i < {fieldName}Count; i++) {{\n");
		sb.Append($"\t\t\t\tlet element!: {unionType};\n");
		sb.Append(Reindent(DecodeTaggedInto("element", variants, msgName, fieldName), "\t\t\t\t"));
		sb.Append($"\t\t\t\t{fieldName}.push(element);\n");
		sb.Append("\t\t\t}\n");
		return sb.ToString();
	}

	private static string DecodeTaggedSingle(string fieldName, IReadOnlyList<ClassDef> variants, string msgName)
	{
		var unionType = PolymorphicUnionTypeName(variants);
		return $"\t\t\tlet {fieldName}!: {unionType};\n" + DecodeTaggedInto(fieldName, variants, msgName, fieldName);
	}

	private static string DecodeTaggedInto(string target, IReadOnlyList<ClassDef> variants, string msgName, string fieldName)
	{
		var sb = new StringBuilder();
		sb.Append($"\t\t\tif (view.byteLength - pos < 2) throw new Error(\"{msgName}: frame too short\");\n");
		sb.Append("\t\t\tconst classId = view.getUint16(pos, true); pos += 2;\n");
		sb.Append("\t\t\tswitch (classId) {\n");
		foreach (var v in variants)
		{
			var qv = ClassQualifiedName(v);
			sb.Append($"\t\t\tcase {qv}.CLASS_ID: {{\n");
			sb.Append($"\t\t\t\tconst {{ value, nextPos }} = {qv}.decodeAt(view, pos);\n");
			sb.Append($"\t\t\t\t{target} = {{ classId: {qv}.CLASS_ID, ...value }};\n");
			sb.Append("\t\t\t\tpos = nextPos;\n");
			sb.Append("\t\t\t\tbreak;\n");
			sb.Append("\t\t\t}\n");
		}
		sb.Append($"\t\t\tdefault: throw new Error(\"{msgName}: unbekannte classId \" + classId + \" in {fieldName}\");\n");
		sb.Append("\t\t\t}\n");
		return sb.ToString();
	}
}
