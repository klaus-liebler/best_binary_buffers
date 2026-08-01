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
  - `class`: like `struct`, plus `string` fields are allowed, plus **one** trailing polymorphic field
    (see `[BinaryUnion]`) is allowed. Still no length-prefixed arrays.
- `[BinaryMessage(MessageKind.Event|Request|Response)]` on a `class` -- the wire-addressable root
  (namespace id + type id header). Only a message may contain a length-prefixed array field, and only
  one such array (or one trailing single polymorphic field), which must be the last field.
  `Request`/`Response` messages get an implicit `requestId` (`ushort`) field as their first field.
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
- `[BinaryCount(N)]` fields: N raw elements back to back, never wire-prefixed (size is schema-time only).
- Polymorphic elements: 2-byte classId immediately before each element's own encoded bytes.
- A message frame starts with a 4-byte header: `namespaceId:u16, messageTypeId:u16`.

A polymorphic field (`[BinaryUnion]`-typed, single or array) must be the very last field of its owner
and there may be at most one per owner -- decoding it is deferred (the owner's `Decode()` just records
the count/classId and jumps straight to the end of the frame; actual per-element decoding happens later,
on demand, via a generated visitor function, to avoid parsing twice). That only works if nothing else in
the frame follows.

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
