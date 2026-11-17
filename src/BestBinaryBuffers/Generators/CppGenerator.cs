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
	/// <summary>Frame header: namespaceId, messageTypeId, correlationId, flags (u16 each).</summary>
	public const int HeaderSize = 8;

	public static string Generate(IReadOnlyList<NamespaceDef> namespaces)
	{
		var sb = new StringBuilder();
		sb.Append(GeneratedFileHeader.Text);
		sb.Append("\n#pragma once\n#include <cstdint>\n#include <cstddef>\n#include <cstring>\n\nnamespace WsProtocol {\n\n");
		sb.Append("enum class MessageKind : uint8_t { Event = 0, Request = 1, Response = 2 };\n\n");
		sb.Append(FrameRuntime);

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
			if (open) sb.Append($"}} // namespace {ns.Name}\n\n");
		}
		// Structs may reference other structs (StructRefField, incl. inside RepeatedField), possibly
		// declared later in the same namespace or in a different namespace entirely -- unlike the TS
		// output (function/class declarations, order-independent at call time), a C++ struct must be
		// fully defined before anything embeds it by value. Emitted in dependency order (topological
		// sort over ALL namespaces combined) instead of raw declaration order, individually re-opening
		// each item's namespace as needed.
		EmitInDependencyOrder(sb, TopoSortStructs(namespaces), GenerateStruct);
		// Same reasoning for classes: a class with a PolymorphicField/PolymorphicArrayField embeds its
		// variant classes' Payload types inside the Append*Element helpers generated alongside it, so
		// those variants must already be fully defined.
		EmitInDependencyOrder(sb, TopoSortClasses(namespaces), GenerateClass);
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

	// --- Frame header and error replies, emitted once per generated file -----------------------------

	private const string FrameRuntime = """
		// Every frame starts with this 8-byte header (little-endian): the message (namespace + type), the
		// correlation of a request and its response, and flags.
		constexpr size_t HEADER_SIZE = 8;
		// The frame is a generic error reply (body: code u16 + null-terminated message) to the request with
		// this correlationId; namespaceId/messageTypeId are those of the request.
		constexpr uint16_t FLAG_ERROR = 0x0001;

		struct FrameHeader {
		    uint16_t namespaceId;
		    uint16_t messageTypeId;
		    // Chosen by the requester (non-zero), copied unchanged into the response or error reply;
		    // 0 = no correlation (event, unsolicited push).
		    uint16_t correlationId;
		    uint16_t flags;
		};

		// false if the frame is shorter than the header.
		inline bool ReadHeader(const uint8_t* data, size_t len, FrameHeader& out) {
		    if (len < HEADER_SIZE) return false;
		    out.namespaceId = (uint16_t)(data[0] | (data[1] << 8));
		    out.messageTypeId = (uint16_t)(data[2] | (data[3] << 8));
		    out.correlationId = (uint16_t)(data[4] | (data[5] << 8));
		    out.flags = (uint16_t)(data[6] | (data[7] << 8));
		    return true;
		}

		// The correlationId of a frame (0 if it is shorter than the header) - what a handler passes to the
		// Encode() of its response.
		inline uint16_t CorrelationId(const uint8_t* data, size_t len) {
		    return len < HEADER_SIZE ? 0 : (uint16_t)(data[4] | (data[5] << 8));
		}

		// 'dest' must have room for HEADER_SIZE bytes.
		inline void WriteHeader(uint8_t* dest, uint16_t namespaceId, uint16_t messageTypeId, uint16_t correlationId, uint16_t flags) {
		    dest[0] = (uint8_t)(namespaceId & 0xFF); dest[1] = (uint8_t)(namespaceId >> 8);
		    dest[2] = (uint8_t)(messageTypeId & 0xFF); dest[3] = (uint8_t)(messageTypeId >> 8);
		    dest[4] = (uint8_t)(correlationId & 0xFF); dest[5] = (uint8_t)(correlationId >> 8);
		    dest[6] = (uint8_t)(flags & 0xFF); dest[7] = (uint8_t)(flags >> 8);
		}

		// Codes of an error reply; an application may define its own from APPLICATION on.
		enum class ErrorCode : uint16_t {
		    UnknownMessage = 1, // namespace/type not handled by the receiver
		    Malformed = 2,      // the frame could not be decoded
		    Busy = 3,           // the request cannot be served now, try again later
		    Failed = 4,         // the handler could not carry out the request (see the message)
		    Application = 0x100,
		};

		struct ErrorPayload {
		    uint16_t code;
		    const char* message; // points into the frame
		};

		// Error reply to 'request' (its namespace/type/correlation, FLAG_ERROR): code + message (truncated
		// to fit). Returns the frame length, 0 if 'dest' is too small for even an empty message.
		inline size_t EncodeError(const FrameHeader& request, uint16_t code, const char* message, uint8_t* dest, size_t dest_size) {
		    if (dest_size < HEADER_SIZE + 3) return 0;
		    WriteHeader(dest, request.namespaceId, request.messageTypeId, request.correlationId, FLAG_ERROR);
		    size_t pos = HEADER_SIZE;
		    dest[pos++] = (uint8_t)(code & 0xFF); dest[pos++] = (uint8_t)(code >> 8);
		    size_t n = message != nullptr ? strlen(message) : 0;
		    if (n > dest_size - pos - 1) n = dest_size - pos - 1;
		    if (n > 0) memcpy(dest + pos, message, n);
		    pos += n;
		    dest[pos++] = 0;
		    return pos;
		}

		// The body of a frame with FLAG_ERROR; false if it is too short or the message is not terminated.
		inline bool DecodeError(const uint8_t* data, size_t len, ErrorPayload& out) {
		    if (len < HEADER_SIZE + 3) return false;
		    out.code = (uint16_t)(data[HEADER_SIZE] | (data[HEADER_SIZE + 1] << 8));
		    const uint8_t* text = data + HEADER_SIZE + 2;
		    if (memchr(text, 0, len - HEADER_SIZE - 2) == nullptr) return false;
		    out.message = (const char*)text;
		    return true;
		}

		// The answer side of one received request: its header, a buffer for the answer and how to send it.
		// A handler answers with Send() - the response carries the request's correlation - or Error(), and
		// never deals with the frame header itself. The transport is the project's: 'send' gets the encoded
		// frame, or len 0 if an answer did not fit 'buffer' (nothing to send; worth a log line).
		class Responder {
		public:
		    using SendFn = void (*)(void* context, const uint8_t* data, size_t len);

		    Responder(const FrameHeader& request, uint8_t* buffer, size_t size, SendFn send, void* context)
		        : request_(request), buffer_(buffer), size_(size), send_(send), context_(context) {}

		    const FrameHeader& Request() const { return request_; }

		    // 'encode': the generated Encode() of the response message.
		    template <class Encode, class Payload>
		    void Send(Encode encode, const Payload& payload) {
		        send_(context_, buffer_, encode(payload, buffer_, size_, request_.correlationId));
		    }

		    // A generic error reply (FLAG_ERROR) - only if the requester waits for an answer (correlationId != 0).
		    void Error(ErrorCode code, const char* message) {
		        if (request_.correlationId == 0) return;
		        send_(context_, buffer_, EncodeError(request_, (uint16_t)code, message, buffer_, size_));
		    }

		private:
		    FrameHeader request_;
		    uint8_t* buffer_;
		    size_t size_;
		    SendFn send_;
		    void* context_;
		};

		// Decodes the request in 'data' (the complete frame) with its generated Decode() and calls
		// handler(responder, payload); a frame that cannot be decoded gets the error reply Malformed. The
		// payload may point into 'data' (strings, arrays): valid during the call.
		template <class Payload, class Handler>
		void Dispatch(Responder& responder, const uint8_t* data, size_t len, bool (*decode)(const uint8_t*, size_t, Payload&),
		              Handler&& handler) {
		    Payload payload{};
		    if (!decode(data, len, payload)) {
		        responder.Error(ErrorCode::Malformed, "the frame could not be decoded");
		        return;
		    }
		    handler(responder, payload);
		}


		""";

	// --- Dependency ordering -------------------------------------------------------------------------

	// Emits each (namespace, item) pair in the given order, re-opening/closing "namespace X { ... }"
	// only when the namespace actually changes from one item to the next (items sharing a namespace
	// stay in the same block; dependency order can otherwise interleave namespaces freely).
	private static void EmitInDependencyOrder<T>(StringBuilder sb, IReadOnlyList<(NamespaceDef Ns, T Item)> ordered, Func<T, string> generate)
	{
		string? openNs = null;
		foreach (var (ns, item) in ordered)
		{
			if (openNs != ns.Name)
			{
				if (!string.IsNullOrEmpty(openNs)) sb.Append($"}} // namespace {openNs}\n\n");
				if (ns.Name.Length > 0) sb.Append($"namespace {ns.Name} {{\n");
				openNs = ns.Name;
			}
			sb.Append(generate(item));
		}
		if (!string.IsNullOrEmpty(openNs)) sb.Append($"}} // namespace {openNs}\n\n");
	}

	// Stable DFS-based topological sort (dependencies first) over ALL namespaces combined -- a
	// struct/class is keyed by (Namespace, Name), which the schema already guarantees unique.
	private static List<(NamespaceDef Ns, StructDef Item)> TopoSortStructs(IReadOnlyList<NamespaceDef> namespaces)
	{
		var all = new List<(NamespaceDef Ns, StructDef Item)>();
		foreach (var ns in namespaces) foreach (var s in ns.Structs) all.Add((ns, s));
		var byKey = all.ToDictionary(x => (x.Ns.Name, x.Item.Name), x => x);
		var visited = new HashSet<(string, string)>();
		var result = new List<(NamespaceDef, StructDef)>();
		void Visit((string, string) key)
		{
			if (!visited.Add(key)) return;
			var (ns, s) = byKey[key];
			foreach (var dep in StructDependencies(s))
			{
				var depKey = (dep.Namespace, dep.Name);
				if (byKey.ContainsKey(depKey)) Visit(depKey);
			}
			result.Add((ns, s));
		}
		foreach (var x in all) Visit((x.Ns.Name, x.Item.Name));
		return result;
	}

	private static IEnumerable<StructDef> StructDependencies(StructDef s)
	{
		foreach (var f in s.Fields)
		{
			if (f is StructRefField srf) yield return srf.Struct;
			if (f is RepeatedField { Element: StructRefField srf2 }) yield return srf2.Struct;
		}
	}

	private static List<(NamespaceDef Ns, ClassDef Item)> TopoSortClasses(IReadOnlyList<NamespaceDef> namespaces)
	{
		var all = new List<(NamespaceDef Ns, ClassDef Item)>();
		foreach (var ns in namespaces) foreach (var c in ns.Classes) all.Add((ns, c));
		var byKey = all.ToDictionary(x => (x.Ns.Name, x.Item.Name), x => x);
		var visited = new HashSet<(string, string)>();
		var result = new List<(NamespaceDef, ClassDef)>();
		void Visit((string, string) key)
		{
			if (!visited.Add(key)) return;
			var (ns, c) = byKey[key];
			foreach (var dep in ClassDependencies(c))
			{
				var depKey = (dep.Namespace, dep.Name);
				if (byKey.ContainsKey(depKey)) Visit(depKey);
			}
			result.Add((ns, c));
		}
		foreach (var x in all) Visit((x.Ns.Name, x.Item.Name));
		return result;
	}

	private static IEnumerable<ClassDef> ClassDependencies(ClassDef c)
	{
		foreach (var f in c.Fields)
		{
			if (f is PolymorphicField pf) foreach (var v in pf.Variants) yield return v;
			if (f is PolymorphicArrayField paf) foreach (var v in paf.Variants) yield return v;
		}
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
	// comments (Struct fields never contain a Uniform*/Polymorphic* array, so it's unused there).
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
				// Append<Owner><Field><Class>Element() helpers. Wire: [count:u16][byteLength:u32][elements]
				// -- the byte length lets the decoder skip the elements without decoding them, so the
				// field may appear anywhere in its owner (s. SchemaValidation).
				return
					$"    if (pos + 6 + payload.{paf.Name}DataSize > dest_size) return 0;\n" +
					$"    dest[pos++] = (uint8_t)((uint16_t)payload.{paf.Name}Count >> 0); dest[pos++] = (uint8_t)((uint16_t)payload.{paf.Name}Count >> 8);\n" +
					WriteU32($"payload.{paf.Name}DataSize") +
					$"    if (payload.{paf.Name}DataSize > 0) {{ memcpy(dest + pos, payload.{paf.Name}Data, payload.{paf.Name}DataSize); pos += payload.{paf.Name}DataSize; }}\n";
			case PolymorphicField pf:
				// Like PolymorphicArrayField, but no count prefix -- exactly one pre-serialized element.
				// Wire: [byteLength:u32][classId:u16][class fields].
				return
					$"    if (pos + 4 + payload.{pf.Name}DataSize > dest_size) return 0;\n" +
					WriteU32($"payload.{pf.Name}DataSize") +
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
				// Deferred: only count, start and byte length are recorded here; the elements are decoded
				// later by Decode<Owner><Field>Elements(). Thanks to the byte length the cursor can skip
				// them -- fields after this one are decoded normally.
				return
					$"    if (len < pos + 6) return false;\n" +
					"    {\n" +
					$"        uint16_t {paf.Name}Count_ = (uint16_t)((uint32_t)data[pos] | ((uint32_t)data[pos + 1] << 8));\n" +
					$"        uint32_t {paf.Name}Bytes_ = (uint32_t)data[pos + 2] | ((uint32_t)data[pos + 3] << 8) | ((uint32_t)data[pos + 4] << 16) | ((uint32_t)data[pos + 5] << 24);\n" +
					"        pos += 6;\n" +
					$"        if ((uint64_t){paf.Name}Bytes_ > (uint64_t)(len - pos)) return false;\n" +
					$"        out.{paf.Name}Count = {paf.Name}Count_;\n" +
					$"        out.{paf.Name}Data = data + pos;\n" +
					$"        out.{paf.Name}DataSize = {paf.Name}Bytes_;\n" +
					$"        pos += {paf.Name}Bytes_;\n" +
					"    }\n";
			case PolymorphicField pf:
				return
					$"    if (len < pos + 4) return false;\n" +
					"    {\n" +
					$"        uint32_t {pf.Name}Bytes_ = (uint32_t)data[pos] | ((uint32_t)data[pos + 1] << 8) | ((uint32_t)data[pos + 2] << 16) | ((uint32_t)data[pos + 3] << 24);\n" +
					"        pos += 4;\n" +
					$"        if ((uint64_t){pf.Name}Bytes_ > (uint64_t)(len - pos)) return false;\n" +
					$"        out.{pf.Name}Data = data + pos;\n" +
					$"        out.{pf.Name}DataSize = {pf.Name}Bytes_;\n" +
					$"        pos += {pf.Name}Bytes_;\n" +
					"    }\n";
			default:
				throw new InvalidOperationException();
		}
	}

	// Worst-case encoded size of ONE field, using the schema's mandatory [BinaryMaxEncodedByteLength]/
	// [BinaryMaxItemCount] bounds instead of a concrete payload's actual runtime lengths -- unlike
	// EncodedSizeStatement below, this is a compile-time constant (see GenerateMessage's
	// <Message>_MAX_SIZE). Mirrors EncodeField's actual byte layout field-by-field (count/length
	// prefixes included) so the two can never silently drift apart.
	// Little-endian u32 write of a size_t expression at "pos" (bounds already checked by the caller).
	private static string WriteU32(string expr) =>
		$"    dest[pos++] = (uint8_t)((uint32_t)({expr}) >> 0); dest[pos++] = (uint8_t)((uint32_t)({expr}) >> 8); " +
		$"dest[pos++] = (uint8_t)((uint32_t)({expr}) >> 16); dest[pos++] = (uint8_t)((uint32_t)({expr}) >> 24);\n";

	private static int MaxSizeOf(FieldBase f) => f switch
	{
		FixedField ff => ff.Size,
		EnumRefField erf => erf.Enum.Size,
		StructRefField srf => srf.Struct.Fields.Sum(FixedSizeOf),
		RepeatedField rf => ElementSize(rf.Element) * rf.Count,
		StringField sf => sf.MaxLength + 1,
		UniformPackedArrayField af => 2 + af.MaxCount * ElementSize(af.Element),
		UniformVariableArrayField vf => 2 + vf.MaxCount * (vf.MaxElementLength + 1),
		// 2-byte count + 4-byte byte-length prefix, then MaxCount elements each [2-byte classId][largest
		// variant's own encoded size] -- mirrors Append<...>Element (classId) + <Variant>::Encode.
		PolymorphicArrayField paf => 6 + paf.MaxCount * (2 + paf.Variants.Max(ClassMaxSize)),
		// No count prefix (single element), but the 4-byte byte-length prefix, then the largest
		// variant's own [2-byte classId][class fields].
		PolymorphicField pf => 4 + 2 + pf.Variants.Max(ClassMaxSize),
		_ => throw new InvalidOperationException(),
	};

	private static int ClassMaxSize(ClassDef c) => c.Fields.Sum(MaxSizeOf);

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
		PolymorphicArrayField paf => $"    total += 6 + payload.{paf.Name}DataSize;\n",
		PolymorphicField pf => $"    total += 4 + payload.{pf.Name}DataSize;\n",
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

		// A class may itself carry array and polymorphic fields -- needs the same ElementAt/Append*/
		// Decode*Elements helpers as a message's polymorphic field. Emitted BEFORE "namespace
		// {c.Name} {" opens (not nested under it): the identifiers already embed the owning class +
		// field name for uniqueness (s. GenerateClassTaggedHelpers), so nesting them one level deeper
		// would only add a qualification ("Namespace::ClassName::Helper") that callers don't need.
		foreach (var af in c.Fields.OfType<UniformPackedArrayField>()) sb.Append(GenerateUniformPackedArrayHelpers(c.Name, af));
		foreach (var vf in c.Fields.OfType<UniformVariableArrayField>()) sb.Append(GenerateUniformVariableArrayHelpers(c.Name, vf));
		foreach (var paf in c.Fields.OfType<PolymorphicArrayField>()) sb.Append(GenerateClassTaggedHelpers(c.Name, paf.Name, paf.Variants));
		foreach (var pf in c.Fields.OfType<PolymorphicField>()) sb.Append(GenerateClassTaggedHelpers(c.Name, pf.Name, pf.Variants));

		if (c.Description is not null) sb.Append($"// {c.Description}\n");
		sb.Append($"namespace {c.Name} {{\n");
		sb.Append($"constexpr uint16_t CLASS_ID = {c.Id};\n\n");

		sb.Append("struct Payload {\n");
		foreach (var f in c.Fields) sb.Append(FieldDecl(f, c.Name));
		sb.Append("};\n\n");

		sb.Append("inline size_t Encode([[maybe_unused]] const Payload& payload, uint8_t* dest, size_t pos, size_t dest_size) {\n");
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
		sb.Append("// bei unbekannter classId, einem zu kurzen/inkonsistenten Element oder wenn die Elemente den\n");
		sb.Append("// per Laengenpraefix angegebenen Bereich ('dataSize') nicht genau ausfuellen zurueck.\n");
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
		sb.Append("    // Die Elemente muessen den per Laengenpraefix angegebenen Bereich genau ausfuellen.\n");
		sb.Append("    return pos == dataSize;\n");
		sb.Append("}\n\n");

		return sb.ToString();
	}

	// --- Message ----------------------------------------------------------------------------------------

	private static string GenerateMessage(Message msg)
	{
		var sb = new StringBuilder();

		// Emitted BEFORE "namespace {msg.Name} {" opens (not nested under it) -- same reasoning as
		// GenerateClass: the identifiers already embed the owning message + field name for uniqueness,
		// so nesting them one level deeper would only add a qualification callers don't need.
		foreach (var af in msg.Fields.OfType<UniformPackedArrayField>()) sb.Append(GenerateUniformPackedArrayHelpers(msg.Name, af));
		foreach (var vf in msg.Fields.OfType<UniformVariableArrayField>()) sb.Append(GenerateUniformVariableArrayHelpers(msg.Name, vf));
		foreach (var paf in msg.Fields.OfType<PolymorphicArrayField>()) sb.Append(GenerateClassTaggedHelpers(msg.Name, paf.Name, paf.Variants));
		foreach (var pf in msg.Fields.OfType<PolymorphicField>()) sb.Append(GenerateClassTaggedHelpers(msg.Name, pf.Name, pf.Variants));

		if (msg.Description is not null) sb.Append($"// {msg.Description}\n");
		sb.Append($"namespace {msg.Name} {{\n");
		sb.Append($"constexpr uint16_t TYPE_ID = {msg.Id};\n");
		sb.Append($"constexpr MessageKind KIND = {KindEnumCpp(msg.Kind)};\n\n");

		sb.Append("struct Payload {\n");
		foreach (var f in msg.Fields) sb.Append(FieldDecl(f, msg.Name));
		sb.Append("};\n\n");

		sb.Append("// Minimal benoetigte Puffergroesse fuer Encode() bei den AKTUELL in payload gesetzten\n");
		sb.Append("// String-/Array-Laengen (haengt vom jeweiligen Aufruf ab, keine Compile-Zeit-Konstante).\n");
		sb.Append("inline size_t EncodedSize([[maybe_unused]] const Payload& payload) {\n");
		sb.Append("    size_t total = HEADER_SIZE;\n");
		foreach (var f in msg.Fields) sb.Append(EncodedSizeStatement(f));
		sb.Append("    return total;\n");
		sb.Append("}\n\n");

		var maxSize = HeaderSize + msg.Fields.Sum(MaxSizeOf);
		sb.Append("// Groesste jemals moegliche Encode()-Groesse, aus den [BinaryMaxEncodedByteLength]/[BinaryMaxItemCount]-\n");
		sb.Append("// Bounds im Schema hergeleitet (nicht vom Aufruf abhaengig, im Gegensatz zu EncodedSize()) --\n");
		sb.Append("// zum Dimensionieren fest allozierter Puffer (statt Ratewerten).\n");
		sb.Append($"constexpr size_t {msg.Name}_MAX_SIZE = {maxSize};\n\n");

		// The correlation is a required parameter for requests and responses (a response that forgets the
		// request's correlation does not compile), optional for events.
		var correlationParam = msg.Kind == MessageKind.Event ? "uint16_t correlationId = 0" : "uint16_t correlationId";
		sb.Append("// Writes header + payload to 'dest' (see EncodedSize() for the size needed). correlationId: for a\n");
		sb.Append("// response the one of the request (FrameHeader::correlationId), for a request one chosen by the\n");
		sb.Append("// requester. Returns the total length written, 0 if 'dest' is too small.\n");
		sb.Append($"inline size_t Encode([[maybe_unused]] const Payload& payload, uint8_t* dest, size_t dest_size, {correlationParam}) {{\n");
		sb.Append("    if (dest_size < HEADER_SIZE) return 0;\n");
		sb.Append("    WriteHeader(dest, NAMESPACE_ID, TYPE_ID, correlationId, 0);\n");
		sb.Append("    size_t pos = HEADER_SIZE;\n");
		foreach (var f in msg.Fields) sb.Append(EncodeField(f));
		sb.Append("    return pos;\n}\n\n");

		sb.Append("// 'data' is the COMPLETE frame including the header (namespaceId/messageTypeId are not checked\n");
		sb.Append("// again here -- the job of the calling dispatcher). false for a frame that is too short or\n");
		sb.Append("// inconsistent (e.g. a length prefix pointing beyond the end of the frame).\n");
		// [[maybe_unused]]: a message without fields reads nothing after the header
		sb.Append("inline bool Decode([[maybe_unused]] const uint8_t* data, size_t len, [[maybe_unused]] Payload& out) {\n");
		sb.Append("    if (len < HEADER_SIZE) return false;\n");
		sb.Append("    [[maybe_unused]] size_t pos = HEADER_SIZE;\n");
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
