using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using BestBinaryBuffers.Model;

namespace BestBinaryBuffers.Parsing;

/// <summary>Reads a set of C# schema files (syntax-only Roslyn parsing -- no Compilation/semantic
/// model, no project references needed, see the README/plan for why) and produces the fully resolved
/// namespace/type model that the C++/TypeScript generators consume.</summary>
public static class SchemaParser
{
	public static IReadOnlyList<NamespaceDef> Parse(IReadOnlyList<string> sourceFiles, IdMap idMap)
	{
		var enums = new Dictionary<string, RawEnum>(StringComparer.Ordinal);
		var structs = new Dictionary<string, RawStruct>(StringComparer.Ordinal);
		var classes = new Dictionary<string, RawClass>(StringComparer.Ordinal);
		var unions = new Dictionary<string, RawUnion>(StringComparer.Ordinal);
		var rawMessages = new Dictionary<string, RawMessage>(StringComparer.Ordinal);

		foreach (var path in sourceFiles)
		{
			var tree = ParseFile(path);
			CollectDeclarations(tree.GetCompilationUnitRoot().Members, currentNamespace: "", namespaceSet: false, path, enums, structs, classes, unions, rawMessages);
		}

		var unionVariantKeys = ComputeUnionVariants(unions, classes);
		var ctx = new ParseContext(idMap, enums, structs, classes, unions, unionVariantKeys);

		// Eager resolve everything (not just on first reference) -- unused declarations still get
		// generated/validated, and errors (unknown reference, cyclic struct, ...) surface immediately.
		var enumDefs = enums.Keys.ToDictionary(k => k, k => ctx.ResolveEnum(k, $"Registrierung \"{k}\""));
		var structDefs = structs.Keys.ToDictionary(k => k, k => ctx.ResolveStruct(k, $"Registrierung \"{k}\""));
		var classDefs = classes.Keys.ToDictionary(k => k, k => ctx.ResolveClass(k, $"Registrierung \"{k}\""));
		var messages = rawMessages.Select(kv => ctx.ResolveMessage(kv.Key, kv.Value)).ToList();

		var allNamespaceNames = enumDefs.Values.Select(e => e.Namespace)
			.Concat(structDefs.Values.Select(s => s.Namespace))
			.Concat(classDefs.Values.Select(c => c.Namespace))
			.Concat(messages.Select(m => m.Namespace))
			.Distinct()
			.OrderBy(n => n, StringComparer.Ordinal)
			.ToList();

		return allNamespaceNames.Select(nsName => new NamespaceDef(
			nsName,
			idMap.GetOrAssignNamespace(nsName),
			enumDefs.Values.Where(e => e.Namespace == nsName).OrderBy(e => e.Name, StringComparer.Ordinal).ToList(),
			structDefs.Values.Where(s => s.Namespace == nsName).OrderBy(s => s.Name, StringComparer.Ordinal).ToList(),
			classDefs.Values.Where(c => c.Namespace == nsName).OrderBy(c => c.Name, StringComparer.Ordinal).ToList(),
			messages.Where(m => m.Namespace == nsName).OrderBy(m => m.Name, StringComparer.Ordinal).ToList()
		)).ToList();
	}

	private static SyntaxTree ParseFile(string path)
	{
		var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path);
		var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
		if (errors.Count > 0)
		{
			throw new SchemaException($"BestBinaryBuffers: Syntaxfehler in {path}:\n" + string.Join("\n", errors.Select(d => $"  {d}")));
		}
		return tree;
	}

	// Recursively walks CompilationUnit/(file-scoped or block) namespace members, tracking the current
	// flat namespace name, and registers every [BinaryType]/[BinaryMessage]/[BinaryUnion]-marked
	// declaration into the matching registry (see RawDeclarations). Namespaces must be flat (no nesting,
	// no dotted namespace name) -- matches the protocol's flat-namespace model 1:1, so the C# namespace
	// IS the protocol namespace with no separate flattening step needed at codegen time.
	private static void CollectDeclarations(
		SyntaxList<MemberDeclarationSyntax> members,
		string currentNamespace,
		bool namespaceSet,
		string path,
		Dictionary<string, RawEnum> enums,
		Dictionary<string, RawStruct> structs,
		Dictionary<string, RawClass> classes,
		Dictionary<string, RawUnion> unions,
		Dictionary<string, RawMessage> rawMessages)
	{
		foreach (var member in members)
		{
			switch (member)
			{
				case FileScopedNamespaceDeclarationSyntax fsns:
				{
					var ns = ValidateFlatNamespace(fsns.Name.ToString(), path);
					CollectDeclarations(fsns.Members, ns, namespaceSet: true, path, enums, structs, classes, unions, rawMessages);
					break;
				}
				case NamespaceDeclarationSyntax nds:
				{
					if (namespaceSet)
					{
						throw new SchemaException($"{path}: verschachtelte/mehrfache namespace-Bloecke sind nicht erlaubt -- Schema-Namespaces muessen flach sein (genau ein \"namespace X;\"/\"namespace X {{ }}\" pro Datei).");
					}
					var ns = ValidateFlatNamespace(nds.Name.ToString(), path);
					CollectDeclarations(nds.Members, ns, namespaceSet: true, path, enums, structs, classes, unions, rawMessages);
					break;
				}
				case EnumDeclarationSyntax eds when eds.HasAttribute("BinaryType"):
				{
					var (ns, name) = ResolveDeclaredIdentity(eds, eds.FindAttribute("BinaryType")!, eds.Identifier.Text, currentNamespace, path);
					var key = NameResolution.FullKey(ns, name);
					if (!enums.TryAdd(key, new RawEnum(ns, name, AttributeHelpers.ExtractDescription(eds), eds, path)))
					{
						throw new SchemaException($"{path}: Enum \"{key}\" ist mehrfach deklariert.");
					}
					break;
				}
				case StructDeclarationSyntax sds when sds.HasAttribute("BinaryType"):
				{
					var (ns, name) = ResolveDeclaredIdentity(sds, sds.FindAttribute("BinaryType")!, sds.Identifier.Text, currentNamespace, path);
					var key = NameResolution.FullKey(ns, name);
					if (!structs.TryAdd(key, new RawStruct(ns, name, AttributeHelpers.ExtractDescription(sds), sds, path)))
					{
						throw new SchemaException($"{path}: Struct \"{key}\" ist mehrfach deklariert.");
					}
					break;
				}
				case InterfaceDeclarationSyntax ids when ids.HasAttribute("BinaryUnion"):
				{
					var ns = currentNamespace;
					var name = ids.Identifier.Text;
					var key = NameResolution.FullKey(ns, name);
					if (!unions.TryAdd(key, new RawUnion(ns, name, ids, path)))
					{
						throw new SchemaException($"{path}: Union-Interface \"{key}\" ist mehrfach deklariert.");
					}
					break;
				}
				case ClassDeclarationSyntax cds when cds.HasAttribute("BinaryType") || cds.HasAttribute("BinaryMessage"):
				{
					var hasType = cds.HasAttribute("BinaryType");
					var hasMessage = cds.HasAttribute("BinaryMessage");
					if (hasType && hasMessage)
					{
						throw new SchemaException($"{path}: class \"{cds.Identifier.Text}\" hat sowohl [BinaryType] als auch [BinaryMessage] -- nur eines von beiden ist erlaubt.");
					}
					if (hasMessage)
					{
						var attr = cds.FindAttribute("BinaryMessage")!;
						var args = AttributeHelpers.ResolveArguments(attr, "kind", "name", "namespace");
						var kind = AttributeHelpers.GetMessageKindArgument(args, "kind");
						var name = AttributeHelpers.GetStringArgument(args, "name") ?? cds.Identifier.Text;
						var ns = ValidateFlatNamespace(AttributeHelpers.GetStringArgument(args, "namespace") ?? currentNamespace, path);
						var key = NameResolution.FullKey(ns, name);
						if (!rawMessages.TryAdd(key, new RawMessage(ns, name, kind, AttributeHelpers.ExtractDescription(cds), cds, path)))
						{
							throw new SchemaException($"{path}: Message \"{key}\" ist mehrfach deklariert.");
						}
					}
					else
					{
						var (ns, name) = ResolveDeclaredIdentity(cds, cds.FindAttribute("BinaryType")!, cds.Identifier.Text, currentNamespace, path);
						var key = NameResolution.FullKey(ns, name);
						if (!classes.TryAdd(key, new RawClass(ns, name, AttributeHelpers.ExtractDescription(cds), cds, path)))
						{
							throw new SchemaException($"{path}: Klasse \"{key}\" ist mehrfach deklariert.");
						}
					}
					break;
				}
			}
		}
	}

	// Shared by Enum/Struct/(BinaryType-)Class declarations: [BinaryType] / [BinaryType("Name")] /
	// [BinaryType("Name", "Namespace")] -- default name/namespace is the real C# identifier/namespace,
	// override only when the C# identifier must diverge from the wire name.
	private static (string Namespace, string Name) ResolveDeclaredIdentity(MemberDeclarationSyntax decl, Microsoft.CodeAnalysis.CSharp.Syntax.AttributeSyntax attr, string identifierText, string currentNamespace, string path)
	{
		var args = AttributeHelpers.ResolveArguments(attr, "name", "namespace");
		var name = AttributeHelpers.GetStringArgument(args, "name") ?? identifierText;
		var ns = ValidateFlatNamespace(AttributeHelpers.GetStringArgument(args, "namespace") ?? currentNamespace, path);
		return (ns, name);
	}

	private static string ValidateFlatNamespace(string ns, string path)
	{
		if (ns.Contains('.'))
		{
			throw new SchemaException($"{path}: Schema-Namespaces muessen flach sein (kein Punkt im Namen), gefunden: \"{ns}\".");
		}
		return ns;
	}

	// Union variants are discovered from the raw BaseList text (interface implementation), independent
	// of field resolution -- a class's base-list ("class Predefined : IScheduleVariant") is matched
	// against the discovered [BinaryUnion] interfaces the same way a field-type reference would be
	// (same-namespace-first, else globally-unique fallback). Sorted by key for reproducible codegen
	// output regardless of file/declaration scan order.
	private static Dictionary<string, List<string>> ComputeUnionVariants(Dictionary<string, RawUnion> unions, Dictionary<string, RawClass> classes)
	{
		var result = unions.Keys.ToDictionary(k => k, _ => new List<string>(), StringComparer.Ordinal);
		foreach (var (classKey, raw) in classes)
		{
			foreach (var baseType in raw.Syntax.BaseList?.Types.Select(t => t.Type) ?? [])
			{
				if (NameResolution.TryResolveKey(unions, baseType, raw.Namespace, $"Klasse \"{classKey}\"", "Union", out var unionKey))
				{
					result[unionKey].Add(classKey);
				}
			}
		}
		foreach (var key in result.Keys.ToList())
		{
			result[key] = result[key].OrderBy(k => k, StringComparer.Ordinal).ToList();
		}
		return result;
	}
}
