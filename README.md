# BestBinaryBuffers

A binary wire-protocol compiler where the schema is plain C# (structs/classes/enums with a few marker
attributes) instead of an external IDL like `.proto`/`.fbs`/JSON. Reads a set of `*.cs` schema files and
generates a matching C++ header and TypeScript module for encoding/decoding the same wire format.

## Why C# as the schema language

Full rationale in the originating conversation (see the consuming project's history), summarized:

- No external tool/parser generator needed on the schema-authoring side -- schema files are just C#,
  readable with normal IDE support (though **parsing is syntax-only Roslyn**, see below -- schema files
  don't need to compile as a real project, and referencing this library from them is optional, purely
  for IntelliSense).
- Namespace/type identity is the real C# identifier by default (`[BinaryType]` with no arguments),
  overridable (`[BinaryType("Name", "Namespace")]`) when a C# rename must not change the wire name.
- Wire-level namespaces are deliberately **flat** (no nesting) -- a schema file's `namespace X;` maps
  1:1 onto the protocol namespace.

## Schema DSL

- `[BinaryType]` on `enum`/`struct`/`class` -- registers the type.
  - `enum`: underlying type must be `byte`/`ushort`/`uint` (wire size 1/2/4). Members without an
    explicit value auto-increment from the previous one (0 if none precede).
  - `struct`: **fixed-layout only** -- primitive/enum-ref/struct-ref fields, no `string`, no arrays
    (except `[BinaryCount(N)]`-repeated fixed fields, e.g. a 6-byte MAC as `[BinaryCount(6)] public
    byte[] Bytes;`, never wire-prefixed).
  - `class`: like `struct`, plus `string` fields, single polymorphic fields (see `[BinaryUnion]`) and
    length-prefixed arrays (`T[]` without `[BinaryCount]`, same kinds and bounds as in a message), anywhere
    and any number of them. A class may not contain itself (directly or via other classes) through a
    `[BinaryUnion]` field -- its maximum encoded size would be unbounded.
- `[BinaryMessage(MessageKind.Event|Request|Response)]` on a `class` -- the wire-addressable root
  (namespace id + type id header). Like a class, it may contain length-prefixed arrays (`T[]` without
  `[BinaryCount]`: fixed-size elements, strings or polymorphic elements) -- any number of them, at any
  position; each needs `[BinaryMaxItemCount(N)]`.
  The kind decides the generated `Encode()`/`encode()`: the correlation ID (see the header below) is a
  required parameter for `Request`/`Response` messages and optional (0) for `Event` messages.
- `[BinaryUnion]` on an `interface` -- marks it as a polymorphic discriminator; every `[BinaryType]`
  class implementing it becomes a tagged variant (2-byte classId).
- Schema members are **public fields** (not properties). A `///` doc comment above a declaration/field
  becomes its `Description`, carried into the generated code as a comment.

Field types: `byte/sbyte/ushort/short/uint/int/ulong/long/bool/float/string`, or a reference to another
`[BinaryType]` enum/struct/class, or a `[BinaryUnion]` interface, or `T[]` (meaning differs by owner --
see above).

## Wire format

- Little-endian throughout.
- Strings: UTF-8 bytes + a single `0x00` terminator. No length prefix.
- Message-level arrays (`T[]` fields without `[BinaryCount]`): `ushort` (2-byte) element count prefix.
- Polymorphic fields additionally carry a `uint` (4-byte) **byte-length** prefix covering their elements:
  array `[count:u16][byteLength:u32][elements]`, single field `[byteLength:u32][classId:u16][fields]`.
- `[BinaryCount(N)]` fields: N raw elements back to back, never wire-prefixed (size is schema-time only).
- Polymorphic elements: 2-byte classId immediately before each element's own encoded bytes.
- A message frame starts with an 8-byte header (`HEADER_SIZE`):
  `namespaceId:u16, messageTypeId:u16, correlationId:u16, flags:u16`.
  - `correlationId`: chosen by the requester (non-zero), copied unchanged into the response, so a
    client can match responses generically without knowing any payload. 0 = no correlation (events,
    unsolicited pushes).
  - `flags`: bit 0 `FLAG_ERROR` - a generic error reply to the request with that correlation ID; the
    namespace/type are those of the request and the body is `code:u16` + a null-terminated message
    (`ErrorCode`: UnknownMessage, Malformed, Busy, Failed; application codes from 0x100). Generated
    helpers: `ReadHeader`/`readHeader`, `CorrelationId` (C++), `EncodeError`/`encodeError`,
    `DecodeError`/`decodeError`. The
    other bits are reserved (0).

## Answering requests (C++)

Handlers should not deal with the header. The generated `Responder` holds the request's header, a
buffer and the project's send function; `Dispatch()` decodes a request and calls the handler, or
answers `Malformed`:

```cpp
void send_to_client(void* session, const uint8_t* data, size_t len);   // the project's transport

void on_frame(Session& s, const uint8_t* data, size_t len) {
    WsProtocol::FrameHeader h{};
    if (!WsProtocol::ReadHeader(data, len, h)) return;
    WsProtocol::Responder reply(h, buffer, sizeof buffer, send_to_client, &s);
    switch (h.messageTypeId) {
        case modbus::WriteHoldingRequest::TYPE_ID:
            return WsProtocol::Dispatch(reply, data, len, modbus::WriteHoldingRequest::Decode, write_holding);
        default: return reply.Error(WsProtocol::ErrorCode::UnknownMessage, "unknown message");
    }
}

void write_holding(WsProtocol::Responder& reply, const modbus::WriteHoldingRequest::Payload& req) {
    reply.Send(modbus::WriteHoldingResponse::Encode, {.success = write(req.address, req.value)});
}
```

`Send()` puts the request's correlation into the response, `Error()` answers only if the requester
waits (correlation != 0).

Decoding a polymorphic field (`[BinaryUnion]`-typed, single or array) is deferred in C++: the owner's
`Decode()` only records start, count and byte length and skips the elements; they are decoded later, on
demand, by a generated visitor function (`Decode<Owner><Field>Elements`), to avoid parsing twice and
without heap allocation. The byte-length prefix is what makes the skipping possible -- so a polymorphic
field may be followed by further fields, and an owner may have several. The visitor rejects elements
that do not fill the announced byte length exactly. (Before the byte-length prefix existed, `Decode()`
had to jump to the end of the frame, so a polymorphic field had to be the single, last field of its
owner. Frames of the two formats are not compatible -- regenerate both ends.)

## Usage

```csharp
using BestBinaryBuffers;

var idMap = IdMap.Load("ids.txt");           // caller owns id persistence, not this library
SchemaCompiler.Compile(schemaFiles, idMap, "ws_protocol.hh", "ws-protocol.ts");
idMap.SaveIfDirty("ids.txt");
```

`SchemaCompiler.Generate(sourceFiles, idMap)` is the file-I/O-free variant, returning `(string Cpp,
string Ts)` directly -- useful for tests or callers that want to write the output themselves.

## Parsing approach: syntax-only Roslyn, deliberately

Schema files are parsed with `CSharpSyntaxTree.ParseText` and walked as plain syntax (no
`CSharpCompilation`/semantic model, no project/reference-assembly setup). Attribute names are matched by
text, and type references are resolved against BestBinaryBuffers' own registry (same-namespace-first,
else a globally-unique fallback, else "qualify it" -- see `Parsing/NameResolution.cs`), not through C#'s
own `using`/scoping rules. This was a deliberate choice (see project history) over building a real
`Compilation`: our flat, non-nested protocol-namespace model doesn't benefit much from real C#
namespace/using resolution, so the extra reference-assembly plumbing wouldn't pay for itself. Free syntax
diagnostics (`SyntaxTree.GetDiagnostics()`) still catch real C# typos; DSL-specific mistakes (wrong field
kind for the owner, non-trailing polymorphic field, unknown type reference, ...) get their own
domain-specific `SchemaException` messages either way, regardless of parsing strategy.
