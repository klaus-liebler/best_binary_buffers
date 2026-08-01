using BestBinaryBuffers.Generators;
using BestBinaryBuffers.Parsing;

namespace BestBinaryBuffers;

/// <summary>Public entry point: reads a set of C# schema files and produces matching C++/TypeScript
/// source text. Callers own all file I/O around this -- resolving directories to source files, and
/// loading/persisting the <see cref="IdMap"/> -- BestBinaryBuffers itself only turns
/// (files, id map) into (C++ text, TS text).</summary>
public static class SchemaCompiler
{
	/// <summary>Parses <paramref name="sourceFiles"/> and generates the C++ header and TypeScript module
	/// text. Does not touch the filesystem beyond reading the given source files; assigns any new
	/// namespace/message/class ids into <paramref name="idMap"/> as a side effect (call
	/// <see cref="IdMap.SaveIfDirty"/> afterwards if they should be persisted).</summary>
	public static (string Cpp, string Ts) Generate(IReadOnlyList<string> sourceFiles, IdMap idMap)
	{
		var namespaces = SchemaParser.Parse(sourceFiles, idMap);
		return (CppGenerator.Generate(namespaces), TsGenerator.Generate(namespaces));
	}

	/// <summary>Like <see cref="Generate"/>, but also writes the two results to
	/// <paramref name="cppOutputFilePath"/>/<paramref name="tsOutputFilePath"/> (creating parent
	/// directories as needed).</summary>
	public static void Compile(IReadOnlyList<string> sourceFiles, IdMap idMap, string cppOutputFilePath, string tsOutputFilePath)
	{
		var (cpp, ts) = Generate(sourceFiles, idMap);
		WriteFile(cppOutputFilePath, cpp);
		WriteFile(tsOutputFilePath, ts);
	}

	private static void WriteFile(string path, string content)
	{
		var dir = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
		File.WriteAllText(path, content);
	}
}
