using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BestBinaryBuffers.Parsing;

// Pure syntax-level attribute recognition (no semantic model/compilation, see SchemaParser). Attributes
// are matched by their simple NAME text ("BinaryType", optionally "BinaryTypeAttribute", ignoring
// whatever namespace-qualification or "using" the schema file happens to use) -- deliberately loose,
// mirrors how the JSON-schema generator this library replaces matched string keys, not compiler symbols.
internal static class AttributeHelpers
{
	public static IEnumerable<AttributeSyntax> AllAttributes(this MemberDeclarationSyntax member) =>
		member.AttributeLists.SelectMany(list => list.Attributes);

	public static bool HasAttribute(this MemberDeclarationSyntax member, string shortName) =>
		member.AllAttributes().Any(a => NameMatches(a, shortName));

	public static AttributeSyntax? FindAttribute(this MemberDeclarationSyntax member, string shortName) =>
		member.AllAttributes().FirstOrDefault(a => NameMatches(a, shortName));

	private static bool NameMatches(AttributeSyntax attr, string shortName)
	{
		var text = attr.Name.ToString();
		var lastSegment = text[(text.LastIndexOf('.') + 1)..];
		return lastSegment == shortName || lastSegment == shortName + "Attribute";
	}

	// Resolves positional AND named arguments against a fixed constructor parameter order -- e.g. for
	// [BinaryType("Name", "Namespace")] as well as [BinaryType(namespace: "Namespace")].
	public static Dictionary<string, ExpressionSyntax> ResolveArguments(AttributeSyntax attr, params string[] parameterNamesInOrder)
	{
		var result = new Dictionary<string, ExpressionSyntax>(StringComparer.Ordinal);
		if (attr.ArgumentList is null) return result;
		var positionalIndex = 0;
		foreach (var arg in attr.ArgumentList.Arguments)
		{
			if (arg.NameEquals is not null)
			{
				result[arg.NameEquals.Name.Identifier.Text] = arg.Expression;
				continue;
			}
			if (positionalIndex >= parameterNamesInOrder.Length)
			{
				throw new SchemaException($"BestBinaryBuffers: zu viele Argumente im Attribut \"{attr}\".");
			}
			result[parameterNamesInOrder[positionalIndex]] = arg.Expression;
			positionalIndex++;
		}
		return result;
	}

	public static string? GetStringArgument(Dictionary<string, ExpressionSyntax> args, string name)
	{
		if (!args.TryGetValue(name, out var expr)) return null;
		if (expr is LiteralExpressionSyntax { Token.Value: string s }) return s;
		throw new SchemaException($"BestBinaryBuffers: Attribut-Argument \"{name}\" muss ein String-Literal sein, war \"{expr}\".");
	}

	public static int GetIntArgument(Dictionary<string, ExpressionSyntax> args, string name)
	{
		if (!args.TryGetValue(name, out var expr) || expr is not LiteralExpressionSyntax { Token.Value: int i })
		{
			throw new SchemaException($"BestBinaryBuffers: Attribut-Argument \"{name}\" muss ein Ganzzahl-Literal sein.");
		}
		return i;
	}

	// [BinaryMessage(MessageKind.Request)] -- the argument is a member-access expression whose last
	// segment is the enum member name (no semantic model, so matched by identifier text against
	// Enum.GetNames, not by resolving the real MessageKind type).
	public static MessageKind GetMessageKindArgument(Dictionary<string, ExpressionSyntax> args, string name)
	{
		if (!args.TryGetValue(name, out var expr))
		{
			throw new SchemaException("BestBinaryBuffers: [BinaryMessage] braucht ein \"kind\"-Argument (MessageKind.Event/Request/Response).");
		}
		var text = expr switch
		{
			MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
			IdentifierNameSyntax id => id.Identifier.Text,
			_ => throw new SchemaException($"BestBinaryBuffers: \"kind\"-Argument von [BinaryMessage] konnte nicht gelesen werden: \"{expr}\"."),
		};
		if (!Enum.TryParse<MessageKind>(text, out var kind))
		{
			throw new SchemaException($"BestBinaryBuffers: unbekannter MessageKind \"{text}\" (gueltig: Event/Request/Response).");
		}
		return kind;
	}

	private static readonly Regex XmlTagRegex = new("<[^>]*>", RegexOptions.Compiled);

	// Extracts the "///" doc-comment directly above a declaration as its Description (used verbatim as
	// a comment in the generated code) -- crude XML-tag stripping, good enough for the short one-line
	// summaries actually used in schema files, no need for a real XML-doc parser.
	public static string? ExtractDescription(SyntaxNode node)
	{
		var doc = node.GetLeadingTrivia()
			.Select(t => t.GetStructure())
			.OfType<DocumentationCommentTriviaSyntax>()
			.FirstOrDefault();
		if (doc is null) return null;

		var raw = XmlTagRegex.Replace(doc.Content.ToFullString(), " ");
		var lines = raw.Split('\n')
			.Select(l => l.Trim().TrimStart('/').Trim())
			.Where(l => l.Length > 0);
		var text = string.Join(" ", lines).Trim();
		return text.Length == 0 ? null : text;
	}
}
