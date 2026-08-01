namespace BestBinaryBuffers;

// Persistent, hand-readable name->id lookup table for namespaces/messages/classes. Schema files never
// declare a numeric id -- types are identified purely by their (flat) name. SchemaCompiler assigns the
// next free id per counter circle the first time it encounters a name and leaves already-assigned ids
// untouched -- ids stay stable across repeated codegen runs (even as the schema grows). Structs/Enums
// never need an id (never addressed standalone on the wire, always embedded/only referenced at codegen
// time). The NULL namespace ("") always gets id 0, reserved, never handed out via this table.
//
// This is a plain, caller-owned data structure: BestBinaryBuffers never touches a file itself. The
// caller loads it (or starts empty), passes it into SchemaCompiler.Compile, and persists it afterwards
// if SaveIfDirty(path) says something changed.
public sealed class IdMap
{
	private readonly Dictionary<string, int> ids = new(StringComparer.Ordinal);
	private readonly List<string> insertionOrder = new();
	private bool dirty;

	public static IdMap Load(string path)
	{
		var map = new IdMap();
		if (!File.Exists(path)) return map;
		foreach (var rawLine in File.ReadAllLines(path))
		{
			var line = rawLine.Trim();
			if (line.Length == 0 || line.StartsWith('#')) continue;
			var lastSpace = line.LastIndexOf(' ');
			if (lastSpace < 0 || !int.TryParse(line[(lastSpace + 1)..], out var id))
			{
				throw new InvalidOperationException($"IdMap: invalid line in {path}: \"{rawLine}\" (expected \"<kind> <name> <id>\").");
			}
			var key = line[..lastSpace];
			if (map.ids.ContainsKey(key))
			{
				throw new InvalidOperationException($"IdMap: duplicate entry \"{key}\" in {path}.");
			}
			map.ids[key] = id;
			map.insertionOrder.Add(key);
		}
		return map;
	}

	public void SaveIfDirty(string path)
	{
		if (!dirty) return;
		var dir = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
		var lines = new List<string>
		{
			"# BestBinaryBuffers id lookup table -- automatically maintained, do not hand-reorder/renumber.",
			"# New lines are appended on the next codegen run; existing lines are NEVER changed or removed",
			"# (see IdMap in BestBinaryBuffers) -- that's what keeps wire ids stable over time.",
			"# One entry per line: <kind> <name> <id>",
		};
		lines.AddRange(insertionOrder.Select(key => $"{key} {ids[key]}"));
		File.WriteAllLines(path, lines);
	}

	private int NextIdFor(string kindPrefix) =>
		ids.Where(kv => kv.Key.StartsWith(kindPrefix, StringComparison.Ordinal)).Select(kv => kv.Value).DefaultIfEmpty(0).Max() + 1;

	// Only message keys WITHOUT a dot in the name part (i.e. "message X", not "message ns.X") -- see
	// GetOrAssignMessage.
	private int NextIdForNullNamespaceMessages() =>
		ids.Where(kv => kv.Key.StartsWith("message ", StringComparison.Ordinal) && !kv.Key["message ".Length..].Contains('.'))
			.Select(kv => kv.Value).DefaultIfEmpty(0).Max() + 1;

	public int GetOrAssignNamespace(string namespaceName)
	{
		if (namespaceName.Length == 0) return 0; // NULL namespace: reserved, never handed out via this table.
		var key = $"namespace {namespaceName}";
		if (ids.TryGetValue(key, out var existing)) return existing;
		var id = NextIdFor("namespace ");
		ids[key] = id;
		insertionOrder.Add(key);
		dirty = true;
		return id;
	}

	// Message ids are assigned per namespace (only need to be unique within their own namespace -- see
	// wire format: namespaceId+messageTypeId together identify a message). For the NULL namespace
	// (ns==""), the prefix "message " alone isn't enough to match only ITS messages -- that's a prefix
	// of every message key (including "message sensact.Foo") -- hence the separate filter that only
	// counts keys without a dot in the name part.
	public int GetOrAssignMessage(string fullMessageName)
	{
		var key = $"message {fullMessageName}";
		if (ids.TryGetValue(key, out var existing)) return existing;
		var dot = fullMessageName.IndexOf('.');
		var ns = dot < 0 ? "" : fullMessageName[..dot];
		var id = ns.Length == 0 ? NextIdForNullNamespaceMessages() : NextIdFor($"message {ns}.");
		ids[key] = id;
		insertionOrder.Add(key);
		dirty = true;
		return id;
	}

	// Class ids are assigned GLOBALLY (a single counter circle across all namespaces) -- a polymorphic
	// list can mix classes from different namespaces, and the 2-byte discriminator in front of each
	// element must be unique without a namespace prefix.
	public int GetOrAssignClass(string fullClassName)
	{
		var key = $"class {fullClassName}";
		if (ids.TryGetValue(key, out var existing)) return existing;
		var id = NextIdFor("class ");
		ids[key] = id;
		insertionOrder.Add(key);
		dirty = true;
		return id;
	}
}
