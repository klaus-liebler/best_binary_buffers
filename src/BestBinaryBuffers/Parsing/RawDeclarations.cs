using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BestBinaryBuffers.Parsing;

// Raw = discovered in pass 1 (name/namespace/description resolved, attribute already validated), but
// fields not resolved yet -- mirrors ParseFileRaw/CollectDeclarations in the JSON-schema generator this
// library replaces: keeping "found it, know its identity" separate from "fully resolved its fields"
// lets cross-file/cross-namespace references (struct-in-struct, enum-in-message, ...) be resolved
// without needing an include mechanism.
internal sealed record RawEnum(string Namespace, string Name, string? Description, EnumDeclarationSyntax Syntax, string Path);
internal sealed record RawStruct(string Namespace, string Name, string? Description, StructDeclarationSyntax Syntax, string Path);
internal sealed record RawClass(string Namespace, string Name, string? Description, ClassDeclarationSyntax Syntax, string Path);
internal sealed record RawUnion(string Namespace, string Name, InterfaceDeclarationSyntax Syntax, string Path);
internal sealed record RawMessage(string Namespace, string Name, MessageKind Kind, string? Description, ClassDeclarationSyntax Syntax, string Path);
