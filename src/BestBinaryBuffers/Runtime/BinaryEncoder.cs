using System.Collections;
using System.Reflection;
using System.Text;
using BestBinaryBuffers.Model;

namespace BestBinaryBuffers.Runtime;

/// <summary>
/// Kodiert Nachrichten im BestBinaryBuffers-Wire-Format direkt aus .NET-Objekten -- fuer
/// C#-Programme, die Daten fuer eine Gegenseite mit generiertem C++/TS-Code erzeugen (z. B. ein
/// Build-Werkzeug, das Konfigurationsdateien fuer eine Firmware schreibt). Arbeitet auf demselben
/// geparsten Schemamodell wie die Generatoren (SchemaCompiler.Parse), Feldreihenfolge, Ids und
/// Layout sind dadurch identisch mit dem generierten Code.
///
/// Werte: ein Objekt mit oeffentlichen Feldern/Eigenschaften gleichen Namens wie die Schemafelder
/// (Gross-/Kleinschreibung egal) oder ein IDictionary&lt;string, object?&gt;. Ein Element eines
/// polymorphen Feldes nennt seine Klasse ueber den .NET-Typnamen oder den Schluessel "$class".
/// Enums: Zahl, .NET-Enum oder Name des Schema-Enum-Werts. Listen: beliebige IEnumerable.
/// </summary>
public sealed class BinaryEncoder
{
	public const string ClassKey = "$class";
	private readonly IReadOnlyList<NamespaceDef> namespaces;

	public BinaryEncoder(IReadOnlyList<NamespaceDef> namespaces)
	{
		this.namespaces = namespaces;
	}

	/// <summary>Kodiert eine Nachricht samt 8-Byte-Kopf (namespaceId, messageTypeId, correlationId, flags=0).</summary>
	public byte[] EncodeMessage(string namespaceName, string messageName, object value, ushort correlationId = 0)
	{
		var ns = namespaces.FirstOrDefault(n => n.Name == namespaceName) ?? throw new ArgumentException($"Namespace {namespaceName} unbekannt.");
		var msg = ns.Messages.FirstOrDefault(m => m.Name == messageName) ?? throw new ArgumentException($"Nachricht {namespaceName}.{messageName} unbekannt.");
		var w = new List<byte>();
		U16(w, (ushort)ns.Id);
		U16(w, (ushort)msg.Id);
		U16(w, correlationId);
		U16(w, 0);
		EncodeFields(w, msg.Fields, value, $"{namespaceName}.{messageName}");
		return w.ToArray();
	}

	private void EncodeFields(List<byte> w, IReadOnlyList<FieldBase> fields, object value, string path)
	{
		foreach (var f in fields)
		{
			EncodeField(w, f, Get(value, f.Name, path), $"{path}.{f.Name}");
		}
	}

	private void EncodeField(List<byte> w, FieldBase f, object? v, string path)
	{
		switch (f)
		{
			case FixedField or EnumRefField or StructRefField:
				EncodeValue(w, f, v, path);
				break;
			case RepeatedField rf:
			{
				var items = Items(v, path);
				if (items.Count != rf.Count) throw new ArgumentException($"{path}: genau {rf.Count} Elemente erwartet, {items.Count} angegeben.");
				foreach (var item in items) EncodeValue(w, rf.Element, item, path);
				break;
			}
			case StringField sf:
				EncodeString(w, Required(v, path) as string ?? throw new ArgumentException($"{path}: String erwartet."), sf.MaxLength, path);
				break;
			case UniformPackedArrayField af:
			{
				var items = Items(v, path);
				Count(items.Count, af.MaxCount, path);
				U16(w, (ushort)items.Count);
				foreach (var item in items) EncodeValue(w, af.Element, item, path);
				break;
			}
			case UniformVariableArrayField vf:
			{
				var items = Items(v, path);
				Count(items.Count, vf.MaxCount, path);
				U16(w, (ushort)items.Count);
				foreach (var item in items) EncodeString(w, item as string ?? throw new ArgumentException($"{path}: String-Elemente erwartet."), vf.MaxElementLength, path);
				break;
			}
			case PolymorphicArrayField paf:
			{
				var items = Items(v, path);
				Count(items.Count, paf.MaxCount, path);
				var body = new List<byte>();
				for (int i = 0; i < items.Count; i++) EncodeVariant(body, paf.Variants, items[i]!, $"{path}[{i}]");
				U16(w, (ushort)items.Count);
				U32(w, (uint)body.Count);
				w.AddRange(body);
				break;
			}
			case PolymorphicField pf:
			{
				var body = new List<byte>();
				EncodeVariant(body, pf.Variants, Required(v, path), path);
				U32(w, (uint)body.Count);
				w.AddRange(body);
				break;
			}
			default:
				throw new NotSupportedException($"{path}: Feldart {f.GetType().Name}");
		}
	}

	private void EncodeVariant(List<byte> w, IReadOnlyList<ClassDef> variants, object item, string path)
	{
		string className = item is IDictionary<string, object?> d && d.TryGetValue(ClassKey, out var c) && c is string s ? s : item.GetType().Name;
		var cls = variants.FirstOrDefault(x => x.Name == className)
			?? throw new ArgumentException($"{path}: Klasse {className} ist hier nicht erlaubt (erlaubt: {string.Join(", ", variants.Select(x => x.Name))}).");
		U16(w, (ushort)cls.Id);
		EncodeFields(w, cls.Fields, item, $"{path}<{cls.Name}>");
	}

	private void EncodeValue(List<byte> w, FieldBase kind, object? v, string path)
	{
		switch (kind)
		{
			case FixedField ff when ff.IsBool:
				w.Add(Convert.ToBoolean(Required(v, path)) ? (byte)1 : (byte)0);
				break;
			case FixedField ff when ff.IsFloat:
				w.AddRange(ff.Size == 4 ? BitConverter.GetBytes(Convert.ToSingle(Required(v, path))) : BitConverter.GetBytes(Convert.ToDouble(Required(v, path))));
				break;
			case FixedField ff:
				Int(w, ToInteger(Required(v, path), path), ff.Size, ff.IsSigned, path);
				break;
			case EnumRefField erf:
			{
				var raw = Required(v, path);
				long value = raw is string name
					? erf.Enum.Values.FirstOrDefault(x => x.Name == name)?.Value ?? throw new ArgumentException($"{path}: {name} ist kein Wert von {erf.Enum.Name}.")
					: ToInteger(raw, path);
				Int(w, value, erf.Enum.Size, false, path);
				break;
			}
			case StructRefField srf:
				EncodeFields(w, srf.Struct.Fields, Required(v, path), path);
				break;
			default:
				throw new NotSupportedException($"{path}: Elementart {kind.GetType().Name}");
		}
	}

	private static void EncodeString(List<byte> w, string s, int maxLength, string path)
	{
		var bytes = Encoding.UTF8.GetBytes(s);
		if (bytes.Length > maxLength) throw new ArgumentException($"{path}: \"{s}\" ist {bytes.Length} Byte lang, erlaubt sind {maxLength}.");
		if (bytes.Contains((byte)0)) throw new ArgumentException($"{path}: String darf kein NUL enthalten.");
		w.AddRange(bytes);
		w.Add(0);
	}

	private static long ToInteger(object v, string path) => v switch
	{
		Enum e => Convert.ToInt64(e),
		ulong u when u > long.MaxValue => unchecked((long)u),
		IConvertible c => c.ToInt64(null),
		_ => throw new ArgumentException($"{path}: Zahl erwartet, {v.GetType().Name} angegeben."),
	};

	private static void Int(List<byte> w, long value, int size, bool signed, string path)
	{
		if (size < 8)
		{
			long min = signed ? -(1L << (size * 8 - 1)) : 0;
			long max = signed ? (1L << (size * 8 - 1)) - 1 : (1L << (size * 8)) - 1;
			if (value < min || value > max) throw new ArgumentException($"{path}: {value} passt nicht in {size} Byte{(signed ? " (mit Vorzeichen)" : "")}.");
		}
		for (int i = 0; i < size; i++) w.Add((byte)(value >> (8 * i)));
	}

	private static void U16(List<byte> w, ushort v) { w.Add((byte)v); w.Add((byte)(v >> 8)); }
	private static void U32(List<byte> w, uint v) { for (int i = 0; i < 4; i++) w.Add((byte)(v >> (8 * i))); }

	private static void Count(int count, int max, string path)
	{
		if (count > max) throw new ArgumentException($"{path}: {count} Elemente, erlaubt sind {max}.");
	}

	private static object Required(object? v, string path) => v ?? throw new ArgumentException($"{path}: Wert fehlt.");

	private static List<object?> Items(object? v, string path) => v switch
	{
		null => [],
		string => throw new ArgumentException($"{path}: Liste erwartet, String angegeben."),
		IEnumerable e => e.Cast<object?>().ToList(),
		_ => throw new ArgumentException($"{path}: Liste erwartet."),
	};

	private static object? Get(object owner, string name, string path)
	{
		if (owner is IDictionary<string, object?> d)
		{
			foreach (var kv in d)
			{
				if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
			}
			return null;
		}
		var t = owner.GetType();
		const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
		if (t.GetField(name, flags) is FieldInfo fi) return fi.GetValue(owner);
		if (t.GetProperty(name, flags) is PropertyInfo pi) return pi.GetValue(owner);
		throw new ArgumentException($"{path}: {t.Name} hat kein Feld \"{name}\".");
	}
}
