using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BestBinaryBuffers.Parsing;

// Field-type-reference resolution: no semantic model, so a referenced name is matched against our OWN
// registry of discovered [BinaryType]/[BinaryUnion] declarations by plain text, deliberately ignoring
// real C# "using"/scoping rules -- consistent with the flat, non-nested protocol-namespace model (a
// schema type is identified by "namespace.Name", not by where in the C# file graph it happens to be
// visible from). An unqualified reference ("ApplicationId") first tries the referencing declaration's
// own namespace, then falls back to a globally unique match; a qualified reference ("sensact.Foo")
// always means exactly that (namespace, name) pair.
internal static class NameResolution
{
	public static string FullKey(string ns, string name) => ns.Length == 0 ? name : $"{ns}.{name}";

	public static (string? Namespace, string Simple) SplitTypeSyntax(TypeSyntax type) => type switch
	{
		QualifiedNameSyntax q => (q.Left.ToString(), q.Right.Identifier.Text),
		IdentifierNameSyntax id => (null, id.Identifier.Text),
		_ => throw new SchemaException($"BestBinaryBuffers: unerwarteter Typ-Ausdruck \"{type}\" -- erwartet ein einfacher oder \"namespace.Name\"-qualifizierter Typname."),
	};

	// Returns false (not throws) when the name isn't found in THIS registry at all -- callers try
	// several registries (enum/struct/union/class) and turn "found in none" / "found in more than one
	// category" into their own combined error message. An ambiguous match WITHIN a single registry
	// (same simple name declared in two different namespaces, referenced unqualified) is still a hard
	// error here, since there's no "which category" fallback left to try.
	public static bool TryResolveKey<T>(IReadOnlyDictionary<string, T> registry, TypeSyntax type, string currentNamespace, string fieldLabel, string kindLabel, out string key)
	{
		var (explicitNs, simple) = SplitTypeSyntax(type);
		if (explicitNs is not null)
		{
			key = FullKey(explicitNs, simple);
			return registry.ContainsKey(key);
		}

		var sameNamespaceKey = FullKey(currentNamespace, simple);
		if (registry.ContainsKey(sameNamespaceKey))
		{
			key = sameNamespaceKey;
			return true;
		}

		var matches = registry.Keys.Where(k => k == simple || k.EndsWith("." + simple, StringComparison.Ordinal)).ToList();
		switch (matches.Count)
		{
			case 0:
				key = "";
				return false;
			case 1:
				key = matches[0];
				return true;
			default:
				throw new SchemaException(
					$"{fieldLabel}: mehrdeutiger {kindLabel} \"{simple}\" (passt zu {string.Join(", ", matches)}) -- " +
					$"mit Namespace qualifizieren (z.B. \"{matches[0]}\").");
		}
	}
}
