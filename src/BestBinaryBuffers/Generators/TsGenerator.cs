using System.Text;
using BestBinaryBuffers.Model;

namespace BestBinaryBuffers.Generators;

/// <summary>Generates a single TypeScript module from the resolved schema model, wire-compatible with
/// <see cref="CppGenerator"/> (little-endian, null-terminated strings, ushort count prefixes). Unlike
/// the C++ side, decode here eagerly materializes real JS values/arrays (garbage-collected runtime, no
/// ESP32-style "avoid allocation" constraint) -- e.g. a UniformVariableArrayField becomes a plain
/// <c>string[]</c>, a PolymorphicArrayField a fully decoded discriminated-union array, not a lazy
/// raw-bytes handle.
///
/// All encode/decode bodies go through the <see cref="RuntimeHelpers"/> Writer/Reader pair (emitted
/// once at the top of the generated file) instead of hand-rolling a fresh Uint8Array+DataView per field
/// -- keeps every generated field statement to one line and avoids the old chunks-array-then-concat
/// allocation pattern (Writer grows its single backing buffer by doubling; Reader tracks its own cursor
/// so struct/class decode helpers return a plain value instead of a {value,nextPos} tuple).</summary>
public static class TsGenerator
{
	public static string Generate(IReadOnlyList<NamespaceDef> namespaces)
	{
		var sb = new StringBuilder();
		sb.Append(GeneratedFileHeader.Text);
		sb.Append("\n\nexport enum MessageKind { Event = 0, Request = 1, Response = 2 }\n\n");
		sb.Append(RuntimeHelpers);

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

	// --- Runtime prelude (Writer/Reader), emitted once per generated file ------------------------------

	// Growable little-endian byte writer: ONE backing buffer (doubled on overflow) instead of a fresh
	// Uint8Array+DataView per field plus a final concat pass -- both far fewer allocations and, since
	// every generated field statement becomes a single "w.writeXxx(...)" call, far less generated code.
	// Reader mirrors it for decode, tracking its own cursor so struct/class helpers return a plain value
	// instead of a {value,nextPos} tuple, and centralizing bounds-checking (one throw site per read
	// kind) instead of ad-hoc "if (view.byteLength - pos < N) throw" checks scattered per field.
	private const string RuntimeHelpers = """
		class Writer {
			private buf: ArrayBuffer;
			private view: DataView;
			private bytes: Uint8Array;
			private pos = 0;
			constructor(initialCapacity = 64) {
				this.buf = new ArrayBuffer(initialCapacity);
				this.view = new DataView(this.buf);
				this.bytes = new Uint8Array(this.buf);
			}
			private ensure(n: number): void {
				if (this.pos + n <= this.buf.byteLength) return;
				let capacity = this.buf.byteLength * 2;
				while (capacity < this.pos + n) capacity *= 2;
				const nextBuf = new ArrayBuffer(capacity);
				new Uint8Array(nextBuf).set(this.bytes);
				this.buf = nextBuf;
				this.view = new DataView(nextBuf);
				this.bytes = new Uint8Array(nextBuf);
			}
			writeUint8(v: number): void { this.ensure(1); this.view.setUint8(this.pos, v); this.pos += 1; }
			writeInt8(v: number): void { this.ensure(1); this.view.setInt8(this.pos, v); this.pos += 1; }
			writeUint16(v: number): void { this.ensure(2); this.view.setUint16(this.pos, v, true); this.pos += 2; }
			writeInt16(v: number): void { this.ensure(2); this.view.setInt16(this.pos, v, true); this.pos += 2; }
			writeUint32(v: number): void { this.ensure(4); this.view.setUint32(this.pos, v, true); this.pos += 4; }
			writeInt32(v: number): void { this.ensure(4); this.view.setInt32(this.pos, v, true); this.pos += 4; }
			writeFloat32(v: number): void { this.ensure(4); this.view.setFloat32(this.pos, v, true); this.pos += 4; }
			// Exact only for non-negative values <= 2^53 (Unix timestamps etc.) -- DataView has no
			// number-based 64-bit accessor; same limitation on the decode side (Reader.readUint64).
			writeUint64(v: number): void {
				this.ensure(8);
				this.view.setUint32(this.pos, v >>> 0, true);
				this.view.setUint32(this.pos + 4, Math.floor(v / 4294967296) >>> 0, true);
				this.pos += 8;
			}
			writeBool(v: boolean): void { this.writeUint8(v ? 1 : 0); }
			writeCString(s: string): void {
				const b = new TextEncoder().encode(s);
				this.ensure(b.length + 1);
				this.bytes.set(b, this.pos);
				this.pos += b.length;
				this.view.setUint8(this.pos, 0);
				this.pos += 1;
			}
			finish(): Uint8Array { return this.bytes.slice(0, this.pos); }
		}

		class Reader {
			pos: number;
			constructor(private view: DataView, offset = 0) { this.pos = offset; }
			private ensure(n: number): void {
				if (this.pos + n > this.view.byteLength) throw new Error("frame too short");
			}
			readUint8(): number { this.ensure(1); const v = this.view.getUint8(this.pos); this.pos += 1; return v; }
			readInt8(): number { this.ensure(1); const v = this.view.getInt8(this.pos); this.pos += 1; return v; }
			readUint16(): number { this.ensure(2); const v = this.view.getUint16(this.pos, true); this.pos += 2; return v; }
			readInt16(): number { this.ensure(2); const v = this.view.getInt16(this.pos, true); this.pos += 2; return v; }
			readUint32(): number { this.ensure(4); const v = this.view.getUint32(this.pos, true); this.pos += 4; return v; }
			readInt32(): number { this.ensure(4); const v = this.view.getInt32(this.pos, true); this.pos += 4; return v; }
			readFloat32(): number { this.ensure(4); const v = this.view.getFloat32(this.pos, true); this.pos += 4; return v; }
			readUint64(): number {
				this.ensure(8);
				const v = this.view.getUint32(this.pos + 4, true) * 4294967296 + this.view.getUint32(this.pos, true);
				this.pos += 8;
				return v;
			}
			readBool(): boolean { return this.readUint8() !== 0; }
			readCString(): string {
				let end = this.pos;
				while (end < this.view.byteLength && this.view.getUint8(end) !== 0) end++;
				if (end >= this.view.byteLength) throw new Error("frame too short (missing null terminator)");
				const value = new TextDecoder().decode(new Uint8Array(this.view.buffer, this.view.byteOffset + this.pos, end - this.pos));
				this.pos = end + 1;
				return value;
			}
		}

		""";

	// --- Scalar field encode/decode (Fixed/EnumRef/StructRef), shared by EVERY context (struct field,
	// array element, class/message top-level field) now that Writer/Reader carry their own cursor -------

	private static string WriterMethodName(int size, bool isFloat) =>
		"write" + (isFloat ? "Float32" : size switch { 1 => "Uint8", 2 => "Uint16", 4 => "Uint32", _ => throw new InvalidOperationException() });
	private static string ReaderMethodName(int size, bool isFloat) =>
		"read" + (isFloat ? "Float32" : size switch { 1 => "Uint8", 2 => "Uint16", 4 => "Uint32", _ => throw new InvalidOperationException() });
	private static string SignedWriterMethodName(int size) => "writeInt" + size switch { 1 => "8", 2 => "16", 4 => "32", _ => throw new InvalidOperationException() };
	private static string SignedReaderMethodName(int size) => "readInt" + size switch { 1 => "8", 2 => "16", 4 => "32", _ => throw new InvalidOperationException() };

	private static string EncodeFieldStatement(FieldBase f, string valueExpr) => f switch
	{
		FixedField { Size: 8 } => $"\t\t\tw.writeUint64({valueExpr});\n",
		FixedField ff when ff.IsBool => $"\t\t\tw.writeBool({valueExpr});\n",
		FixedField ff when ff.IsSigned && !ff.IsFloat => $"\t\t\tw.{SignedWriterMethodName(ff.Size)}({valueExpr});\n",
		FixedField ff => $"\t\t\tw.{WriterMethodName(ff.Size, ff.IsFloat)}({valueExpr});\n",
		EnumRefField erf => $"\t\t\tw.{WriterMethodName(erf.Enum.Size, false)}({valueExpr});\n",
		StructRefField srf => $"\t\t\t{Qualify(srf.Struct.Namespace, $"encode{srf.Struct.Name}Into")}({valueExpr}, w);\n",
		_ => throw new InvalidOperationException(),
	};

	private static string DecodeFieldExpr(FieldBase f) => f switch
	{
		FixedField { Size: 8 } => "r.readUint64()",
		FixedField ff when ff.IsBool => "r.readBool()",
		FixedField ff when ff.IsSigned && !ff.IsFloat => $"r.{SignedReaderMethodName(ff.Size)}()",
		FixedField ff => $"r.{ReaderMethodName(ff.Size, ff.IsFloat)}()",
		EnumRefField erf => $"r.{ReaderMethodName(erf.Enum.Size, false)}() as {EnumTypeName(erf.Enum)}",
		StructRefField srf => $"{Qualify(srf.Struct.Namespace, $"decode{srf.Struct.Name}")}(r)",
		_ => throw new InvalidOperationException(),
	};

	private static string EncodeFieldChunk(FieldBase f, string varName) => f switch
	{
		FixedField or EnumRefField or StructRefField => EncodeFieldStatement(f, $"{varName}.{f.Name}"),
		RepeatedField rf =>
			$"\t\t\tfor (let i = 0; i < {rf.Count}; i++) {{\n" +
			Reindent(EncodeFieldStatement(rf.Element, $"{varName}.{rf.Name}[i]"), "\t\t\t\t") +
			"\t\t\t}\n",
		StringField sf => $"\t\t\tw.writeCString({varName}.{sf.Name});\n",
		UniformPackedArrayField af =>
			$"\t\t\tw.writeUint16({varName}.{af.Name}.length);\n" +
			$"\t\t\tfor (const item of {varName}.{af.Name}) {{\n" +
			Reindent(EncodeFieldStatement(af.Element, "item"), "\t\t\t\t") +
			"\t\t\t}\n",
		UniformVariableArrayField vf =>
			$"\t\t\tw.writeUint16({varName}.{vf.Name}.length);\n" +
			$"\t\t\tfor (const s of {varName}.{vf.Name}) w.writeCString(s);\n",
		PolymorphicArrayField paf =>
			$"\t\t\tw.writeUint16({varName}.{paf.Name}.length);\n" +
			$"\t\t\tfor (const item of {varName}.{paf.Name}) {{\n" +
			Reindent(EncodeTaggedElement("item", paf.Variants), "\t\t\t\t") +
			"\t\t\t}\n",
		PolymorphicField pf => EncodeTaggedElement($"{varName}.{pf.Name}", pf.Variants),
		_ => throw new InvalidOperationException(),
	};

	private static string EncodeTaggedElement(string itemExpr, IReadOnlyList<ClassDef> variants)
	{
		var sb = new StringBuilder();
		sb.Append($"\t\t\tw.writeUint16({itemExpr}.classId);\n");
		sb.Append($"\t\t\tswitch ({itemExpr}.classId) {{\n");
		foreach (var v in variants)
		{
			var qv = ClassQualifiedName(v);
			sb.Append($"\t\t\tcase {qv}.CLASS_ID: {qv}.encodeInto({itemExpr}, w); break;\n");
		}
		sb.Append($"\t\t\tdefault: throw new Error(\"unbekannte classId \" + ({itemExpr} as any).classId);\n");
		sb.Append("\t\t\t}\n");
		return sb.ToString();
	}

	private static string DecodeFieldChunk(FieldBase f, string msgName) => f switch
	{
		FixedField or EnumRefField or StructRefField => $"\t\t\tconst {f.Name} = {DecodeFieldExpr(f)};\n",
		RepeatedField rf =>
			$"\t\t\tconst {rf.Name}: {ElementTsType(rf.Element)}[] = [];\n" +
			$"\t\t\tfor (let i = 0; i < {rf.Count}; i++) {rf.Name}.push({DecodeFieldExpr(rf.Element)});\n",
		StringField sf => $"\t\t\tconst {sf.Name} = r.readCString();\n",
		UniformPackedArrayField af =>
			$"\t\t\tconst {af.Name}Count = r.readUint16();\n" +
			$"\t\t\tconst {af.Name}: {ElementTsType(af.Element)}[] = [];\n" +
			$"\t\t\tfor (let i = 0; i < {af.Name}Count; i++) {af.Name}.push({DecodeFieldExpr(af.Element)});\n",
		UniformVariableArrayField vf =>
			$"\t\t\tconst {vf.Name}Count = r.readUint16();\n" +
			$"\t\t\tconst {vf.Name}: string[] = [];\n" +
			$"\t\t\tfor (let i = 0; i < {vf.Name}Count; i++) {vf.Name}.push(r.readCString());\n",
		PolymorphicArrayField paf => DecodeTaggedArray(paf.Name, paf.Variants, msgName),
		PolymorphicField pf => DecodeTaggedSingle(pf.Name, pf.Variants, msgName),
		_ => throw new InvalidOperationException(),
	};

	private static string DecodeTaggedArray(string fieldName, IReadOnlyList<ClassDef> variants, string msgName)
	{
		var unionType = PolymorphicUnionTypeName(variants);
		var sb = new StringBuilder();
		sb.Append($"\t\t\tconst {fieldName}Count = r.readUint16();\n");
		sb.Append($"\t\t\tconst {fieldName}: {unionType}[] = [];\n");
		sb.Append($"\t\t\tfor (let i = 0; i < {fieldName}Count; i++) {{\n");
		sb.Append($"\t\t\t\tconst classId = r.readUint16();\n");
		sb.Append("\t\t\t\tswitch (classId) {\n");
		foreach (var v in variants)
		{
			var qv = ClassQualifiedName(v);
			sb.Append($"\t\t\t\tcase {qv}.CLASS_ID: {fieldName}.push({{ classId, ...{qv}.decode(r) }}); break;\n");
		}
		sb.Append($"\t\t\t\tdefault: throw new Error(\"{msgName}: unbekannte classId \" + classId + \" in {fieldName}\");\n");
		sb.Append("\t\t\t\t}\n");
		sb.Append("\t\t\t}\n");
		return sb.ToString();
	}

	private static string DecodeTaggedSingle(string fieldName, IReadOnlyList<ClassDef> variants, string msgName)
	{
		var unionType = PolymorphicUnionTypeName(variants);
		var sb = new StringBuilder();
		sb.Append($"\t\t\tconst {fieldName}ClassId = r.readUint16();\n");
		sb.Append($"\t\t\tlet {fieldName}: {unionType};\n");
		sb.Append($"\t\t\tswitch ({fieldName}ClassId) {{\n");
		foreach (var v in variants)
		{
			var qv = ClassQualifiedName(v);
			sb.Append($"\t\t\tcase {qv}.CLASS_ID: {fieldName} = {{ classId: {qv}.CLASS_ID, ...{qv}.decode(r) }}; break;\n");
		}
		sb.Append($"\t\t\tdefault: throw new Error(\"{msgName}: unbekannte classId \" + {fieldName}ClassId + \" in {fieldName}\");\n");
		sb.Append("\t\t\t}\n");
		return sb.ToString();
	}

	private static string Reindent(string block, string prefix) =>
		string.Concat(block.Split('\n').Select(l => l.Trim().Length == 0 ? "" : prefix + l.TrimStart() + "\n"));

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

	// "encode<Name>Into"/"decode<Name>" take an explicit Writer/Reader (rather than allocating their own)
	// because a struct can be embedded at any position within a Message/Class/another struct -- the
	// Writer/Reader's own internal cursor makes that "any position" free (no pos parameter to thread).
	private static string GenerateStruct(StructDef s, string indent)
	{
		var sb = new StringBuilder();
		if (s.Description is not null) sb.Append($"{indent}// {s.Description}\n");
		sb.Append($"{indent}export interface {s.Name} {{\n");
		foreach (var f in s.Fields) sb.Append(FieldDecl(f));
		sb.Append($"{indent}}}\n");

		var totalSize = s.Fields.Sum(FixedSizeOf);
		sb.Append($"{indent}export const {s.Name}_SIZE = {totalSize};\n\n");

		sb.Append($"{indent}export function encode{s.Name}Into(value: {s.Name}, w: Writer): void {{\n");
		foreach (var f in s.Fields) sb.Append(EncodeFieldChunk(f, "value"));
		sb.Append($"{indent}}}\n\n");

		sb.Append($"{indent}export function encode{s.Name}(value: {s.Name}): Uint8Array {{\n");
		sb.Append($"{indent}\tconst w = new Writer({totalSize});\n{indent}\tencode{s.Name}Into(value, w);\n{indent}\treturn w.finish();\n{indent}}}\n\n");

		sb.Append($"{indent}export function decode{s.Name}(r: Reader): {s.Name} {{\n");
		foreach (var f in s.Fields) sb.Append(DecodeFieldChunk(f, s.Name));
		sb.Append("\t\treturn { " + string.Join(", ", s.Fields.Select(f => f.Name)) + " };\n\t}\n\n");

		return sb.ToString();
	}

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

		sb.Append("\t\texport function encodeInto(payload: Payload, w: Writer): void {\n");
		foreach (var f in c.Fields) sb.Append(EncodeFieldChunk(f, "payload"));
		sb.Append("\t\t}\n\n");

		sb.Append("\t\texport function encode(payload: Payload): Uint8Array {\n");
		sb.Append("\t\t\tconst w = new Writer();\n\t\t\tencodeInto(payload, w);\n\t\t\treturn w.finish();\n\t\t}\n\n");

		sb.Append("\t\texport function decode(r: Reader): Payload {\n");
		foreach (var f in c.Fields) sb.Append(DecodeFieldChunk(f, c.Name));
		sb.Append("\t\t\treturn { " + string.Join(", ", c.Fields.Select(f => f.Name)) + " };\n\t\t}\n");
		sb.Append($"{indent}}}\n\n");
		return sb.ToString();
	}

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
		sb.Append("\t\t\tconst w = new Writer();\n");
		sb.Append("\t\t\tw.writeUint16(NAMESPACE_ID);\n\t\t\tw.writeUint16(TYPE_ID);\n");
		foreach (var f in msg.Fields) sb.Append(EncodeFieldChunk(f, "payload"));
		sb.Append("\t\t\treturn w.finish();\n\t\t}\n\n");

		sb.Append("\t\t// 'view'/'offset': kompletter Frame inkl. 4-Byte-Kopf / dessen Anfang. Wirft bei zu kurzem Frame.\n");
		sb.Append("\t\texport function decode(view: DataView, offset: number): Payload {\n");
		sb.Append("\t\t\tconst r = new Reader(view, offset + 4);\n");
		foreach (var f in msg.Fields) sb.Append(DecodeFieldChunk(f, msg.Name));
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
}
