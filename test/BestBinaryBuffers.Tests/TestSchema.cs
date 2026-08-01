namespace BestBinaryBuffers.Tests;

// Writes one or more C# snippets to real temp .cs files (SchemaParser reads from disk, no in-memory
// overload -- schema files are meant to be real files on a developer's machine, so tests exercise the
// exact same path) and cleans them up afterwards.
internal sealed class TestSchema : IDisposable
{
	private readonly List<string> paths = new();

	public string AddFile(string content)
	{
		var path = Path.Combine(Path.GetTempPath(), $"bbb_test_{Guid.NewGuid():N}.cs");
		File.WriteAllText(path, content);
		paths.Add(path);
		return path;
	}

	public IReadOnlyList<string> Files => paths;

	public void Dispose()
	{
		foreach (var p in paths)
		{
			try { File.Delete(p); } catch { /* best effort */ }
		}
	}
}
