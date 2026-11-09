namespace BestBinaryBuffers.Tests;

public class GeneratorTests
{
	[Fact]
	public void GeneratesCppAndTsWithoutThrowingForFullFixture()
	{
		using var schema = new TestSchema();
		schema.AddFile(Fixtures.FullSchema);

		var (cpp, ts) = SchemaCompiler.Generate(schema.Files, new IdMap());

		Assert.Contains("namespace WsProtocol", cpp);
		Assert.Contains("namespace testns", cpp);
		Assert.Contains("export enum MessageKind", ts);
		Assert.Contains("testns", ts);
	}

	[Fact]
	public void TsDecodeLocalsDoNotCollideWithFieldNames()
	{
		// A field named like a local of the generated decode() (its parameters view/offset, the Reader r)
		// became a second "const" of the same name: "The symbol 'offset' has already been declared"
		// (found with plc.UploadRequest.offset in the Factory Control Unit's schema).
		using var schema = new TestSchema();
		schema.AddFile("""
			using BestBinaryBuffers;
			namespace clash;
			[BinaryType] public struct Pos { public uint r; public uint offset; }
			[BinaryType] public class Item { public uint view; public uint r; }
			[BinaryMessage(MessageKind.Request)]
			public class Chunk { public uint offset; public uint view; public uint r; public Pos pos;
				[BinaryMaxItemCount(4)] public byte[] data = System.Array.Empty<byte>(); }
			""");
		var (_, ts) = SchemaCompiler.Generate(schema.Files, new IdMap());

		// every decode function: its parameters and its consts must be distinct names
		var functions = System.Text.RegularExpressions.Regex.Matches(ts,
			@"export function decode\w*\(([^)]*)\)[^{]*\{(.*?)\n\t*return \{", System.Text.RegularExpressions.RegexOptions.Singleline);
		Assert.True(functions.Count >= 2, "the message's and the struct's decode functions");
		foreach (System.Text.RegularExpressions.Match f in functions)
		{
			var locals = new HashSet<string>(f.Groups[1].Value.Split(',').Select(p => p.Split(':')[0].Trim()).Where(n => n.Length > 0));
			var consts = System.Text.RegularExpressions.Regex.Matches(f.Groups[2].Value, @"^\t*const (\$?\w+)", System.Text.RegularExpressions.RegexOptions.Multiline);
			foreach (System.Text.RegularExpressions.Match c in consts)
			{
				var head = f.Value.Split('\n')[0];
				Assert.True(locals.Add(c.Groups[1].Value), $"'{c.Groups[1].Value}' declared twice in {head}");
			}
		}
	}

	[Fact]
	public void StringFieldIsNullTerminatedNotLengthPrefixed()
	{
		using var schema = new TestSchema();
		schema.AddFile(Fixtures.FullSchema);
		var (cpp, ts) = SchemaCompiler.Generate(schema.Files, new IdMap());

		// DeviceInfo.name / NotifyThing.message are StringField -- encode must write a 0x00 terminator,
		// never a 4-byte length prefix (the old JSON-schema generator's wire format, deliberately dropped).
		Assert.Contains("dest[pos++] = 0;", cpp);
		Assert.DoesNotContain("Length >> 24", cpp); // old length-prefix encoding pattern must be gone
		Assert.Contains("readCString", ts);
	}

	[Fact]
	public void ArrayCountPrefixesAreTwoBytesNotFour()
	{
		using var schema = new TestSchema();
		schema.AddFile(Fixtures.FullSchema);
		var (cpp, _) = SchemaCompiler.Generate(schema.Files, new IdMap());

		// UniformPackedArrayField (waypoints) / UniformVariableArrayField (tags) / PolymorphicArrayField
		// (commands) counts must be read/written as uint16_t (2 bytes), not the old uint32_t (4 bytes).
		Assert.Contains("uint16_t waypointsCount_", cpp);
		Assert.Contains("uint16_t tagsCount_", cpp);
		Assert.Contains("uint16_t commandsCount_", cpp);
	}

	[Fact]
	public void PolymorphicArrayGeneratesAppendHelperPerVariant()
	{
		using var schema = new TestSchema();
		schema.AddFile(Fixtures.FullSchema);
		var (cpp, ts) = SchemaCompiler.Generate(schema.Files, new IdMap());

		Assert.Contains("AppendResponseStatusCommandsLightCommandElement", cpp);
		Assert.Contains("AppendResponseStatusCommandsTextCommandElement", cpp);
		Assert.Contains("LightCommand.CLASS_ID", ts);
		Assert.Contains("TextCommand.CLASS_ID", ts);
	}

	[Fact]
	public void MessageEmitsConstexprMaxSizeDerivedFromSchemaBounds()
	{
		using var schema = new TestSchema();
		schema.AddFile(Fixtures.FullSchema);
		var (cpp, _) = SchemaCompiler.Generate(schema.Files, new IdMap());

		// ResponseStatus mixes every variable-length kind (StringField via requestId is fixed, but
		// Waypoints/Tags/Commands are Uniform*/Polymorphic) -- MAX_SIZE must be a real constexpr,
		// not the runtime-dependent EncodedSize(). Exact arithmetic (header 4 + requestId 2 +
		// Timestamp 4 + Position 12 + Mood 1 + Address 6 + Waypoints (2+8*12) + Tags (2+4*16) +
		// Commands (2+4*(2+64))) = 459, recomputed here so the test breaks loudly if the formula in
		// CppGenerator.MaxSizeOf ever silently drifts from EncodeField's actual wire layout.
		Assert.Contains("constexpr size_t ResponseStatus_MAX_SIZE = 459;", cpp);
		// NotifyThing has just one bounded string (MaxLength 127) plus the 4-byte header.
		Assert.Contains("constexpr size_t NotifyThing_MAX_SIZE = 132;", cpp);
	}

	[Fact]
	public void RepeatedFieldEmbedsFixedSizeArrayWithoutWirePrefix()
	{
		using var schema = new TestSchema();
		schema.AddFile(Fixtures.FullSchema);
		var (cpp, ts) = SchemaCompiler.Generate(schema.Files, new IdMap());

		Assert.Contains("uint8_t bytes[6];", cpp);
		Assert.Contains("bytes: number[];", ts);
	}

	[Fact]
	public void SameIdMapProducesStableIdsAcrossRepeatedRuns()
	{
		using var schema = new TestSchema();
		schema.AddFile(Fixtures.FullSchema);
		var idMap = new IdMap();

		var (cpp1, _) = SchemaCompiler.Generate(schema.Files, idMap);
		var (cpp2, _) = SchemaCompiler.Generate(schema.Files, idMap);

		Assert.Equal(cpp1, cpp2);
	}
}
