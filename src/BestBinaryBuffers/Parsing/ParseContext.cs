using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using BestBinaryBuffers.Model;

namespace BestBinaryBuffers.Parsing;

// Pass 2: resolves fields against the registries pass 1 (SchemaParser.CollectDeclarations) filled in --
// independent of which file/namespace a reference came from, no "include" mechanism needed. Enums/
// structs/classes are resolved lazily but CACHED (see enumCache/structCache/classCache) and eagerly
// walked once for every declared name right after construction (see SchemaParser.Parse) so unused
// declarations still get validated and errors surface immediately, not only when something happens to
// reference them.
internal sealed class ParseContext(
	IdMap ids,
	IReadOnlyDictionary<string, RawEnum> rawEnums,
	IReadOnlyDictionary<string, RawStruct> rawStructs,
	IReadOnlyDictionary<string, RawClass> rawClasses,
	IReadOnlyDictionary<string, RawUnion> rawUnions,
	IReadOnlyDictionary<string, List<string>> unionVariantKeys)
{
	public IdMap Ids { get; } = ids;

	private readonly Dictionary<string, EnumDef> enumCache = new(StringComparer.Ordinal);
	private readonly Dictionary<string, StructDef> structCache = new(StringComparer.Ordinal);
	private readonly Dictionary<string, ClassDef> classCache = new(StringComparer.Ordinal);
	private readonly HashSet<string> structsBeingResolved = new(StringComparer.Ordinal);

	private static readonly Dictionary<string, (string Cpp, string Ts, int Size, bool IsBool, bool IsSigned, bool IsFloat)> PrimitiveTypes = new()
	{
		["byte"] = ("uint8_t", "number", 1, false, false, false),
		["sbyte"] = ("int8_t", "number", 1, false, true, false),
		["ushort"] = ("uint16_t", "number", 2, false, false, false),
		["short"] = ("int16_t", "number", 2, false, true, false),
		["uint"] = ("uint32_t", "number", 4, false, false, false),
		["int"] = ("int32_t", "number", 4, false, true, false),
		["bool"] = ("bool", "boolean", 1, true, false, false),
		["float"] = ("float", "number", 4, false, false, true),
		["ulong"] = ("uint64_t", "number", 8, false, false, false),
		["long"] = ("int64_t", "number", 8, false, true, false),
	};

	// --- Enum/Struct/Class/Message resolution --------------------------------------------------

	public EnumDef ResolveEnum(string key, string usedFrom)
	{
		if (enumCache.TryGetValue(key, out var cached)) return cached;
		if (!rawEnums.TryGetValue(key, out var raw))
		{
			throw new SchemaException($"{usedFrom}: unbekanntes Enum \"{key}\" -- ist es mit [BinaryType] in einer der eingelesenen Schema-Dateien deklariert?");
		}

		var baseType = raw.Syntax.BaseList?.Types.FirstOrDefault()?.Type;
		var keyword = (baseType as PredefinedTypeSyntax)?.Keyword.Text;
		var size = keyword switch
		{
			"byte" => 1,
			"ushort" => 2,
			"uint" => 4,
			_ => throw new SchemaException($"Enum \"{key}\": underlying type fehlt oder ist ungueltig (erwartet \"byte\"/\"ushort\"/\"uint\", z.B. \"public enum {raw.Name} : ushort\")."),
		};

		var values = new List<EnumValue>();
		long next = 0;
		foreach (var member in raw.Syntax.Members)
		{
			var value = member.EqualsValue is not null ? EvaluateIntLiteral(member.EqualsValue.Value) : next;
			values.Add(new EnumValue(member.Identifier.Text, value));
			next = value + 1;
		}

		var def = new EnumDef(raw.Namespace, raw.Name, raw.Description, size, values);
		enumCache[key] = def;
		return def;
	}

	public StructDef ResolveStruct(string key, string usedFrom)
	{
		if (structCache.TryGetValue(key, out var cached)) return cached;
		if (!rawStructs.TryGetValue(key, out var raw))
		{
			throw new SchemaException($"{usedFrom}: unbekannter Struct \"{key}\" -- ist er mit [BinaryType] in einer der eingelesenen Schema-Dateien deklariert?");
		}
		if (!structsBeingResolved.Add(key))
		{
			throw new SchemaException($"{usedFrom}: zyklische Struct-Referenz ueber \"{key}\".");
		}
		var fields = ResolveFields(raw.Syntax.Members, raw.Namespace, $"Struct \"{key}\"", OwnerKind.Struct);
		structsBeingResolved.Remove(key);

		var def = new StructDef(raw.Namespace, raw.Name, raw.Description, fields);
		structCache[key] = def;
		return def;
	}

	public ClassDef ResolveClass(string key, string usedFrom)
	{
		if (classCache.TryGetValue(key, out var cached)) return cached;
		if (!rawClasses.TryGetValue(key, out var raw))
		{
			throw new SchemaException($"{usedFrom}: unbekannte Klasse \"{key}\" -- ist sie mit [BinaryType] in einer der eingelesenen Schema-Dateien deklariert?");
		}
		var fields = ResolveFields(raw.Syntax.Members, raw.Namespace, $"Klasse \"{key}\"", OwnerKind.Class);
		var id = Ids.GetOrAssignClass(key);
		var def = new ClassDef(raw.Namespace, raw.Name, raw.Description, fields, id);
		classCache[key] = def;
		return def;
	}

	public IReadOnlyList<ClassDef> ResolveUnionVariants(string unionKey, string usedFrom)
	{
		if (!unionVariantKeys.TryGetValue(unionKey, out var classKeys) || classKeys.Count == 0)
		{
			throw new SchemaException($"{usedFrom}: [BinaryUnion]-Interface \"{unionKey}\" hat keine implementierenden [BinaryType]-Klassen.");
		}
		return classKeys.Select(k => ResolveClass(k, usedFrom)).ToList();
	}

	public Message ResolveMessage(string key, RawMessage raw)
	{
		var fields = ResolveFields(raw.Syntax.Members, raw.Namespace, $"Message \"{key}\"", OwnerKind.Message);
		if (raw.Kind is MessageKind.Request or MessageKind.Response)
		{
			// requestId (uint16) is an IMPLICIT first field right after the 4-byte header for
			// request/response messages -- synthesized here instead of every request/response schema
			// declaration having to list it itself.
			var requestId = new FixedField("requestId", "Vom Client vergebene Korrelations-ID, in der Response unveraendert zurueckgegeben.", "uint16_t", "number", 2, false, false);
			fields = [requestId, .. fields];
		}
		var id = Ids.GetOrAssignMessage(key);
		return new Message(raw.Namespace, raw.Name, id, raw.Kind, raw.Description, fields);
	}

	// --- Field resolution ------------------------------------------------------------------------

	private List<FieldBase> ResolveFields(SyntaxList<MemberDeclarationSyntax> members, string ns, string ownerDescription, OwnerKind ownerKind)
	{
		var fields = new List<FieldBase>();
		foreach (var member in members)
		{
			if (member is not FieldDeclarationSyntax fieldDecl)
			{
				throw new SchemaException($"{ownerDescription}: nur oeffentliche Felder erlaubt, unerwartetes Member \"{member}\".");
			}
			if (!fieldDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
			{
				throw new SchemaException($"{ownerDescription}: Feld \"{fieldDecl.Declaration}\" muss \"public\" sein.");
			}
			foreach (var declarator in fieldDecl.Declaration.Variables)
			{
				fields.Add(ResolveField(fieldDecl, declarator, ns, ownerDescription, ownerKind));
			}
		}
		SchemaValidation.ValidateFields(fields, ns, ownerDescription);
		return fields;
	}

	private FieldBase ResolveField(FieldDeclarationSyntax fieldDecl, VariableDeclaratorSyntax declarator, string ns, string ownerDescription, OwnerKind ownerKind)
	{
		var name = ToCamelCase(declarator.Identifier.Text);
		var description = AttributeHelpers.ExtractDescription(fieldDecl);
		var countAttr = fieldDecl.FindAttribute("BinaryCount");
		var fieldLabel = $"{ownerDescription}, Feld \"{name}\"";
		var type = fieldDecl.Declaration.Type;

		if (type is ArrayTypeSyntax arrayType)
		{
			var elementType = arrayType.ElementType;
			if (countAttr is not null)
			{
				var args = AttributeHelpers.ResolveArguments(countAttr, "count");
				var count = AttributeHelpers.GetIntArgument(args, "count");
				if (count <= 0) throw new SchemaException($"{fieldLabel}: [BinaryCount] muss > 0 sein.");
				var element = ResolveScalarElement(elementType, ns, fieldLabel);
				return new RepeatedField(name, description, element, count);
			}
			if (ownerKind != OwnerKind.Message)
			{
				throw new SchemaException($"{fieldLabel}: Arrays ohne [BinaryCount] sind nur in einer Message erlaubt -- in Struct/Class braucht ein Array-Feld [BinaryCount(N)].");
			}
			return ResolveMessageArrayField(name, description, elementType, ns, fieldLabel);
		}

		if (countAttr is not null)
		{
			throw new SchemaException($"{fieldLabel}: [BinaryCount] ist nur auf Array-Feldern (T[]) erlaubt.");
		}
		return ResolveScalarField(name, description, type, ns, fieldLabel, ownerKind);
	}

	// Fixed/EnumRef/StructRef only -- for RepeatedField ([BinaryCount]) and UniformPackedArrayField
	// elements, both of which need a compile-time-known, blittable element shape.
	private FieldBase ResolveScalarElement(TypeSyntax type, string ns, string fieldLabel)
	{
		if (type is PredefinedTypeSyntax pts)
		{
			if (pts.Keyword.Text == "string")
			{
				throw new SchemaException($"{fieldLabel}: \"string\" ist als Array-/Wiederholungs-Element nicht erlaubt (nur feste Feldtypen).");
			}
			return MakeFixedField("", null, pts.Keyword.Text, fieldLabel);
		}
		var (kind, key) = ResolveNamedType(type, ns, fieldLabel);
		return kind switch
		{
			RefKind.Enum => new EnumRefField("", null, ResolveEnum(key, fieldLabel)),
			RefKind.Struct => new StructRefField("", null, ResolveStruct(key, fieldLabel)),
			RefKind.Union => throw new SchemaException($"{fieldLabel}: ein [BinaryUnion]-Typ ist als Array-/Wiederholungs-Element nicht erlaubt (nur feste Feldtypen)."),
			RefKind.Class => throw new SchemaException($"{fieldLabel}: eine Klasse ist als Array-/Wiederholungs-Element nicht erlaubt (nur feste Feldtypen: Primitive/Enum/Struct)."),
			_ => throw new SchemaException($"{fieldLabel}: unbekannter Typ \"{type}\"."),
		};
	}

	private FieldBase ResolveScalarField(string name, string? description, TypeSyntax type, string ns, string fieldLabel, OwnerKind ownerKind)
	{
		if (type is PredefinedTypeSyntax pts)
		{
			if (pts.Keyword.Text == "string")
			{
				if (ownerKind == OwnerKind.Struct)
				{
					throw new SchemaException($"{fieldLabel}: \"string\" ist in einem Struct nicht erlaubt (nur feste Feldtypen) -- als Class deklarieren, wenn ein String benoetigt wird.");
				}
				return new StringField(name, description);
			}
			return MakeFixedField(name, description, pts.Keyword.Text, fieldLabel);
		}

		var (kind, key) = ResolveNamedType(type, ns, fieldLabel);
		switch (kind)
		{
			case RefKind.Enum:
				return new EnumRefField(name, description, ResolveEnum(key, fieldLabel));
			case RefKind.Struct:
				return new StructRefField(name, description, ResolveStruct(key, fieldLabel));
			case RefKind.Union:
				if (ownerKind == OwnerKind.Struct)
				{
					throw new SchemaException($"{fieldLabel}: ein polymorphes ([BinaryUnion]) Feld ist in einem Struct nicht erlaubt -- als Class deklarieren.");
				}
				return new PolymorphicField(name, description, ResolveUnionVariants(key, fieldLabel));
			case RefKind.Class:
				throw new SchemaException($"{fieldLabel}: direkte Klassenreferenzen werden nicht unterstuetzt -- fuer ein einzelnes polymorphes Feld ein [BinaryUnion]-Interface verwenden (auch wenn nur eine Klasse implementiert).");
			default:
				throw new SchemaException($"{fieldLabel}: unbekannter Typ \"{type}\".");
		}
	}

	private FieldBase ResolveMessageArrayField(string name, string? description, TypeSyntax elementType, string ns, string fieldLabel)
	{
		if (elementType is PredefinedTypeSyntax pts)
		{
			if (pts.Keyword.Text == "string")
			{
				return new UniformVariableArrayField(name, description);
			}
			return new UniformPackedArrayField(name, description, MakeFixedField(name, description, pts.Keyword.Text, fieldLabel));
		}

		var (kind, key) = ResolveNamedType(elementType, ns, fieldLabel);
		return kind switch
		{
			RefKind.Enum => new UniformPackedArrayField(name, description, new EnumRefField(name, description, ResolveEnum(key, fieldLabel))),
			RefKind.Struct => new UniformPackedArrayField(name, description, new StructRefField(name, description, ResolveStruct(key, fieldLabel))),
			RefKind.Union => new PolymorphicArrayField(name, description, ResolveUnionVariants(key, fieldLabel)),
			RefKind.Class => throw new SchemaException($"{fieldLabel}: ein Array aus direkten Klassenreferenzen wird nicht unterstuetzt -- [BinaryUnion]-Interface verwenden (auch wenn nur eine Klasse implementiert)."),
			_ => throw new SchemaException($"{fieldLabel}: unbekannter Element-Typ \"{elementType}\"."),
		};
	}

	private enum RefKind { Enum, Struct, Union, Class }

	private (RefKind Kind, string Key) ResolveNamedType(TypeSyntax type, string ns, string fieldLabel)
	{
		var candidates = new List<(RefKind Kind, string Key)>();
		if (NameResolution.TryResolveKey(rawEnums, type, ns, fieldLabel, "Enum", out var ek)) candidates.Add((RefKind.Enum, ek));
		if (NameResolution.TryResolveKey(rawStructs, type, ns, fieldLabel, "Struct", out var sk)) candidates.Add((RefKind.Struct, sk));
		if (NameResolution.TryResolveKey(rawUnions, type, ns, fieldLabel, "Union", out var uk)) candidates.Add((RefKind.Union, uk));
		if (NameResolution.TryResolveKey(rawClasses, type, ns, fieldLabel, "Klasse", out var ck)) candidates.Add((RefKind.Class, ck));

		return candidates.Count switch
		{
			0 => throw new SchemaException($"{fieldLabel}: unbekannter Typ \"{type}\" -- weder als Enum/Struct/Class ([BinaryType]) noch als Union ([BinaryUnion]) deklariert."),
			1 => candidates[0],
			_ => throw new SchemaException($"{fieldLabel}: mehrdeutiger Typ \"{type}\" (passt als {string.Join(" UND ", candidates.Select(c => c.Kind))}) -- ein Name darf nicht fuer mehrere Schema-Kategorien verwendet werden."),
		};
	}

	private static FixedField MakeFixedField(string name, string? description, string keyword, string fieldLabel)
	{
		if (!PrimitiveTypes.TryGetValue(keyword, out var info))
		{
			throw new SchemaException($"{fieldLabel}: nicht unterstuetzter Feldtyp \"{keyword}\" (gueltig: byte/sbyte/ushort/short/uint/int/ulong/long/bool/float/string oder ein [BinaryType]-Enum/Struct/Class-Name).");
		}
		return new FixedField(name, description, info.Cpp, info.Ts, info.Size, info.IsBool, info.IsSigned, info.IsFloat);
	}

	private static long EvaluateIntLiteral(ExpressionSyntax expr) => expr switch
	{
		LiteralExpressionSyntax { Token.Value: int i } => i,
		LiteralExpressionSyntax { Token.Value: uint u } => u,
		LiteralExpressionSyntax { Token.Value: long l } => l,
		LiteralExpressionSyntax { Token.Value: ulong ul } => (long)ul,
		PrefixUnaryExpressionSyntax { OperatorToken.Text: "-" } prefix => -EvaluateIntLiteral(prefix.Operand),
		_ => throw new SchemaException($"BestBinaryBuffers: unerwarteter Enum-Wert-Ausdruck \"{expr}\" (erwartet ein Ganzzahl-Literal)."),
	};

	private static string ToCamelCase(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
}
